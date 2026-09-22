using System.Security.Claims;
using System.Text.Json.Serialization;
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
using Mootify.Endpoints.Api;
using Mootify.Services.Admin;
using Mootify.Services.Auth;
using Mootify.Services.Ideas;
using Mootify.Services.Import;
using Mootify.Services.Library;
using Microsoft.Extensions.Options;
using Mootify.Services.Soulseek;
using Mootify.Services.MusicBrainz;
using Mootify.Services.Notifications;
using Mootify.Services.Playback;
using Mootify.Services.Playlists;
using Mootify.Services.Recommendations;
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
// file would quietly override Soulseek__ApiKey and Library__Password, which is the opposite of
// what secrets are for.
builder.Configuration.AddEnvironmentVariables();

// Keep a durable log beside the deployed site so headless/service installations are diagnosable
// without access to their console. The file sink creates one file per day and keeps two weeks.
var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "logs");
Directory.CreateDirectory(logDirectory);
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "mootify-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1)));

// ---- options ------------------------------------------------------------
// ValidateOnStart: a missing music root should stop the app at boot with a readable
// message, not surface as a NullReferenceException the first time somebody clicks Scan.
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SoulseekOptions>()
    .Bind(builder.Configuration.GetSection(SoulseekOptions.Section))
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

builder.Services.AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Enums as names, not numbers. Only Minimal APIs read this — Blazor renders server-side and
// never serializes these types — so it changes the JSON the Android app sees and nothing else.
// `"status": "Downloading"` survives a client that hasn't been rebuilt after a new enum member;
// `"status": 3` silently becomes a different state.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
builder.Services.AddScoped<PreferenceService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddScoped<ServerInfoProvider>();
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
                .Select(u => new { u.IsBanned, u.IsAdmin, u.MustChangePassword })
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

            // Same treatment for the forced-password-change flag, and this one is load-bearing
            // rather than a convenience: an admin resetting a signed-in user has to bite on
            // their next request, and the page that clears it runs in a circuit that can't
            // write a cookie. Reconciling here is the only thing that moves it in either
            // direction after sign-in.
            var hasResetClaim = MootifyAuth.MustChangePassword(context.Principal);
            if (hasResetClaim != user.MustChangePassword && context.Principal!.Identity is ClaimsIdentity id)
            {
                if (user.MustChangePassword)
                {
                    id.AddClaim(new Claim(MootifyAuth.MustChangePasswordClaim, "true"));
                }
                else
                {
                    var claim = id.FindFirst(MootifyAuth.MustChangePasswordClaim);
                    if (claim is not null) id.RemoveClaim(claim);
                }

                context.ShouldRenew = true;
            }
        };
    })
    // The second door, for the Android app. A separate scheme rather than a second cookie:
    // it 401s instead of redirecting to /login, which is the difference between a phone that
    // re-authenticates and a phone that hangs on an HTML page it can't read.
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        MootifyAuth.ApiScheme, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(MootifyAuth.AdminPolicy, policy => policy.RequireRole(MootifyAuth.AdminRole))
    // Token only. Excluding the cookie is what lets the API turn antiforgery off: a
    // cookie-authenticated POST from a browser page would otherwise be forgeable.
    .AddPolicy(MootifyAuth.ApiPolicy, policy => policy
        .AddAuthenticationSchemes(MootifyAuth.ApiScheme)
        .RequireAuthenticatedUser())
    // Streaming takes either. The website plays through a cookie and the app through a token,
    // and it's a GET of the user's own library either way.
    .AddPolicy(MootifyAuth.MediaPolicy, policy => policy
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, MootifyAuth.ApiScheme)
        .RequireAuthenticatedUser());

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
builder.Services.AddSingleton<LibraryFiler>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<LibraryOrganizer>();
builder.Services.AddHostedService<LibraryScanService>();

builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<AlbumArtService>();
builder.Services.AddSingleton<Transcoder>();
builder.Services.AddSingleton<LibraryTranscodeService>();
builder.Services.AddSingleton<TranscodeCache>();

builder.Services.AddScoped<LibrarySearchService>();
builder.Services.AddScoped<PlaylistService>();
builder.Services.AddScoped<PlaylistEvents>();
builder.Services.AddScoped<ListeningService>();
builder.Services.AddScoped<TasteService>();
builder.Services.AddScoped<IdeaService>();
builder.Services.AddScoped<PlaylistImportService>();
// Singleton: one import runs at a time and its progress outlives any circuit.
builder.Services.AddSingleton<ImportRequestQueue>();
builder.Services.AddScoped<TeamService>();
builder.Services.AddScoped<PlayerService>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddScoped<RequestFulfiller>();
builder.Services.AddScoped<ImportRequestMatcher>();
// Scoped, because it reaches the request matcher, which reaches PlaylistService and the notifier.
builder.Services.AddScoped<TrackUploadService>();
builder.Services.AddScoped<RequestReconciler>();
builder.Services.AddHostedService<RequestReconcilerService>();

// Retry + circuit breaker: a Soulseek daemon that's down must not take Mootify down with it.
builder.Services.AddHttpClient<SoulseekClient>(client => client.Timeout = TimeSpan.FromSeconds(90))
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

// Pages get the friendly not-found page; machines don't. Re-executing an API 401 into a Razor
// render hands a phone 40KB of HTML in place of an empty body, and the same middleware turns a
// missing cover art file into a rendered web page. Excluded by prefix rather than by content type
// because the status code is decided long before anything writes a body.
app.UseWhen(
    context => !IsMachineFacing(context.Request.Path),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// After authentication, not before. An antiforgery token is bound to the identity it was
// rendered for, so the middleware has to be able to see who is asking — put it above
// UseAuthentication and every authenticated form post is validated against an anonymous user
// and fails with a raw 400.
app.UseAntiforgery();

// After auth so it can't be bypassed, before the endpoints so nothing else answers while
// the instance has no admin.
app.UseMiddleware<SetupMiddleware>();

// After authentication, so there's a principal to read the flag off — and after the setup gate,
// because a server with no admin has a bigger problem than one user's password.
app.UseMiddleware<PasswordChangeMiddleware>();

app.MapStaticAssets();
app.MapAuthEndpoints();
app.MapMediaEndpoints();

// The Android app's whole surface: /api/v1/** plus the unauthenticated /art path a car head
// unit can actually fetch. See Endpoints/Api/ArtEndpoints.cs for why that one is open.
app.MapApi();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Paths whose callers want a status code, not a page: the JSON API, audio streams and cover art.
/// </summary>
static bool IsMachineFacing(PathString path) =>
    path.StartsWithSegments("/api")
    || path.StartsWithSegments("/media")
    || path.StartsWithSegments("/art");
