using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Mootify.Services.Auth;

/// <summary>
/// <c>Authorization: Bearer moo_…</c> for clients with no cookie jar. Registered as a second
/// scheme rather than replacing the cookie: the website keeps signing in with a cookie, and both
/// schemes produce the same claims, so nothing below the endpoint layer knows the difference.
///
/// It never challenges with a redirect. A phone that is handed the HTML login page instead of a
/// 401 has no way to tell "signed out" from "the server is fine, you're just wrong" — which is
/// the failure mode that makes token clients hang rather than re-authenticate.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        // NoResult, not Fail: on /media the cookie scheme gets its turn after this one, and a
        // browser request carries no Authorization header at all.
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var secret = header[BearerPrefix.Length..].Trim();
        var identity = await tokens.ValidateAsync(secret, Context.RequestAborted);

        if (identity is null)
        {
            // Deliberately not saying which of expired / revoked / banned / never existed it
            // was. The client's move is the same in every case: sign in again.
            return AuthenticateResult.Fail("That token isn't valid any more.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(
            MootifyAuth.BuildApiPrincipal(identity), MootifyAuth.ApiScheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"Mootify\"";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>Reads back what the handler put in. Endpoints ask here rather than digging claims.</summary>
public static class ApiPrincipal
{
    public static Guid? GetUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid GetRequiredUserId(ClaimsPrincipal principal) =>
        GetUserId(principal) ?? throw new InvalidOperationException("No authenticated user on this request.");

    /// <summary>Which device token this request came in on — null for a cookie session.</summary>
    public static Guid? GetTokenId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(MootifyAuth.TokenIdClaim), out var id) ? id : null;
}
