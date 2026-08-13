using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Lidarr;
using Mootify.Services.Settings;

namespace Mootify.Endpoints.Api;

/// <summary>
/// Getting a device onto the server, and getting it off again.
///
/// Sign-in goes through the same <see cref="AccountService"/> and the same
/// <see cref="LoginThrottle"/> as the website. That matters more than it looks: the throttle
/// counts per username <i>and</i> per address, so an API that kept its own counters would hand
/// an attacker a second, unlimited door to the same accounts.
/// </summary>
public static class ApiAuthEndpoints
{
    public static void MapApiAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by necessity — this is where a token comes from. Antiforgery off because
        // there is no cookie to protect and no token client can mint an antiforgery token.
        var anonymous = app.MapGroup("/api/v1/auth").DisableAntiforgery();

        anonymous.MapPost("/token", async (
            HttpContext http,
            TokenRequest body,
            AccountService accounts,
            ApiTokenService tokens,
            LoginThrottle throttle,
            ServerInfoProvider info,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();

            // Locked out: no password check, and no failure recorded for the attempt. Same
            // reasoning as the web form — attempts made during a lockout mustn't extend it.
            if (throttle.GetLockout(body.Username, ip) is { } remaining)
            {
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
                return Results.Json(
                    new { error = $"Too many wrong passwords. Try again in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} minutes." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            // Before the check and regardless of the outcome: a correct password answering
            // instantly while a wrong one waits tells an attacker which was which.
            var delay = throttle.GetDelay(body.Username, ip);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            var result = await accounts.SignInAsync(body.Username, body.Password, ct);

            if (!result.Succeeded)
            {
                throttle.RecordFailure(body.Username, ip);
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.RecordSuccess(body.Username, ip);

            var userId = ApiPrincipal.GetRequiredUserId(result.Principal!);
            var issued = await tokens.IssueAsync(userId, body.DeviceName, ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);

            return Results.Ok(new ApiTokenResponse(
                issued.Secret,
                issued.ExpiresAt,
                ApiMap.User(user),
                await info.GetAsync(ct)));
        }).RequireRateLimiting("login");

        // ---- authenticated ------------------------------------------------
        var api = app.MapApiGroup("/api/v1");

        api.MapGet("/me", async (
            HttpContext http,
            ServerInfoProvider info,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new { user = ApiMap.User(user), server = await info.GetAsync(ct) });
        });

        api.MapGet("/auth/tokens", async (
            HttpContext http, ApiTokenService tokens, CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var current = ApiPrincipal.GetTokenId(http.User);

            var rows = await tokens.ListAsync(userId, current, ct);

            return Results.Ok(rows
                .Select(t => new ApiDeviceToken(
                    t.Id, t.DeviceName, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, t.IsCurrent))
                .ToList());
        });

        api.MapDelete("/auth/tokens/{tokenId:guid}", async (
            HttpContext http, Guid tokenId, ApiTokenService tokens, CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            return await tokens.RevokeAsync(tokenId, userId, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        // Signing out this device. Separate from the revoke-by-id call so the client doesn't
        // have to remember its own token id.
        api.MapPost("/auth/logout", async (
            HttpContext http, ApiTokenService tokens, CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            if (ApiPrincipal.GetTokenId(http.User) is { } tokenId)
            {
                await tokens.RevokeAsync(tokenId, userId, ct);
            }

            return Results.NoContent();
        });
    }
}

/// <summary>
/// What the client is allowed to assume about this server. Asked once at sign-in and again on
/// <c>/me</c>, so a Lidarr that appears later shows up without reinstalling the app.
/// </summary>
public sealed class ServerInfoProvider(
    LidarrClient lidarr,
    SettingsService settings,
    IOptionsMonitor<ApiOptions> options)
{
    public async Task<ApiServerInfo> GetAsync(CancellationToken ct = default) => new(
        Name: "Mootify",
        ApiVersion: ApiMap.Version,
        LidarrConfigured: lidarr.IsConfigured,
        RegistrationOpen: await settings.GetRegistrationEnabledAsync(ct),
        MaxPageSize: options.CurrentValue.MaxPageSize);
}
