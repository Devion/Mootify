using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Mootify;
using Mootify.Components;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Endpoints;
using Mootify.Services.Admin;
using Mootify.Services.Auth;
using Mootify.Services.Import;
using Mootify.Services.Library;
using Microsoft.Extensions.Options;
using Mootify.Services.Lidarr;
using Mootify.Services.MusicBrainz;
using Mootify.Services.Notifications;
using Mootify.Services.Playback;
using Mootify.Services.Playlists;
using Mootify.Services.Requests;
using Mootify.Services.Settings;
using Mootify.Services.Teams;
using Mootify.Services.Transcoding;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// mootify.json is the one file you edit. It is gitignored; mootify.example.json is the
// committed template.
builder.Configuration.AddJsonFile("mootify.json", optional: true, reloadOnChange: true);

// Re-added so it sits *above* mootify.json in precedence. Configuration is last-wins, and the
// default builder registers environment variables before this file — so without this line the
// file would quietly override Lidarr__ApiKey and Library__Password, which is the opposite of
// what secrets are for.
builder.Configuration.AddEnvironmentVariables();

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console());

// ---- options ------------------------------------------------------------
// ValidateOnStart: a missing music root should stop the app at boot with a readable
// message, not surface as a NullReferenceException the first time somebody clicks Scan.
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<LidarrOptions>()
    .Bind(builder.Configuration.GetSection(LidarrOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<MusicBrainzOptions>()
    .Bind(builder.Configuration.GetSection(MusicBrainzOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<LibraryOptions>()
    .Bind(builder.Configuration.GetSection(LibraryOptions.Section))
    .ValidateOnStart();

builder.Services.AddOptions<RequestOptions>()
    .Bind(builder.Configuration.GetSection(RequestOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<NotificationOptions>()
    .Bind(builder.Configuration.GetSection(NotificationOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TranscodeOptions>()
    .Bind(builder.Configuration.GetSection(TranscodeOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---- data ---------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/mootify.db";

Directory.CreateDirectory("data");

// Factory for components (a circuit outlives any single scope, and two components sharing
// one DbContext will eventually overlap two queries on it), plus a scoped instance for
// request-scoped services.
builder.Services.AddDbContextFactory<MootifyDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<MootifyDbContext>>().CreateDbContext());

// ---- auth ---------------------------------------------------------------
builder.Services.AddSingleton<SetupState>();

// Backs LoginThrottle. The size limit is the eviction policy: an attacker cycling random
// usernames would otherwise grow the counter table without bound.
builder.Services.AddMemoryCache(options => options.SizeLimit = 20_000);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = MootifyAuth.LoginPath;
        options.LogoutPath = MootifyAuth.LogoutPath;
        options.AccessDeniedPath = "/denied";
        options.ExpireTimeSpan = builder.Configuration
            .GetSection(AuthOptions.Section)
            .GetValue<TimeSpan?>(nameof(AuthOptions.SessionLifetime)) ?? TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "mootify";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // Re-check the account on every request. Three things this catches that a cookie
        // can't know about: the row is gone (wiped database), the account has been banned
        // since sign-in, or its admin rights changed. Ban has to take effect now, not
        // whenever the cookie happens to expire.
        options.Events.OnValidatePrincipal = async context =>
        {
            var raw = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(raw, out var userId))
            {
                context.RejectPrincipal();
                return;
            }

            var factory = context.HttpContext.RequestServices
                .GetRequiredService<IDbContextFactory<MootifyDbContext>>();

            await using var db = await factory.CreateDbContextAsync(context.HttpContext.RequestAborted);

            var user = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.IsBanned, u.IsAdmin })
                .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

            if (user is null || user.IsBanned)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            // Admin granted or revoked since sign-in — fix the claim rather than making
            // them sign out and back in.
            var hasAdminClaim = context.Principal!.IsInRole(MootifyAuth.AdminRole);
            if (hasAdminClaim != user.IsAdmin && context.Principal.Identity is ClaimsIdentity identity)
            {
                if (user.IsAdmin)
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, MootifyAuth.AdminRole));
                }
                else
                {
                    var claim = identity.FindFirst(c => c.Type == ClaimTypes.Role && c.Value == MootifyAuth.AdminRole);
                    if (claim is not null) identity.RemoveClaim(claim);
                }

                context.ShouldRenew = true;
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(MootifyAuth.AdminPolicy, policy => policy.RequireRole(MootifyAuth.AdminRole));

builder.Services.AddCascadingAuthenticationState();

// Sign-in and registration are the endpoints worth guessing at from the LAN.
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.PermitLimit = 20;
        limiter.QueueLimit = 0;
    });
});

// ---- application services ----------------------------------------------
builder.Services.AddSingleton<NetworkShareConnector>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddHostedService<LibraryScanService>();

builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<Transcoder>();
builder.Services.AddSingleton<LibraryTranscodeService>();
builder.Services.AddSingleton<TranscodeCache>();

builder.Services.AddScoped<PlaylistService>();
builder.Services.AddScoped<PlaylistEvents>();
builder.Services.AddScoped<PlaylistImportService>();
// Singleton: one import runs at a time and its progress outlives any circuit.
builder.Services.AddSingleton<ImportRequestQueue>();
builder.Services.AddScoped<TeamService>();
builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<RequestReconciler>();
builder.Services.AddHostedService<RequestReconcilerService>();

// Retry + circuit breaker: a Lidarr that's down must not take Mootify down with it.
builder.Services.AddHttpClient<LidarrClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .AddStandardResilienceHandler();

// MusicBrainz insists on an identifying User-Agent and throttles anyone without one.
builder.Services.AddHttpClient<MusicBrainzClient>((sp, client) =>
{
    var mb = sp.GetRequiredService<IOptions<MusicBrainzOptions>>().Value;
    client.BaseAddress = new Uri(mb.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mootify/0.1 ( {mb.Contact} )");
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// ---- startup checks -----------------------------------------------------
await StartupChecks.RunAsync(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// After auth so it can't be bypassed, before the endpoints so nothing else answers while
// the instance has no admin.
app.UseMiddleware<SetupMiddleware>();

app.MapStaticAssets();
app.MapAuthEndpoints();
app.MapMediaEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
