namespace Mootify.Services.Auth;

/// <summary>
/// While an account is on a one-time password from an admin, everything it asks for goes to
/// /password. The flag rides in the cookie (see <c>AccountService.BuildPrincipal</c>) and is
/// reconciled by <c>OnValidatePrincipal</c>, so this costs one claim lookup per request and no
/// database round trip — the same deal <see cref="Settings.SetupMiddleware"/> gets from its bool.
///
/// The redirect is what makes "immediately" true. It is not the only thing enforcing the change:
/// /api refuses to issue a token to a flagged account in the first place, and the reset revokes
/// the tokens it already had.
/// </summary>
public sealed class PasswordChangeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!MootifyAuth.MustChangePassword(context.User))
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";

        // The change page, its form post, the way out, and the assets the page renders with.
        // /_blazor is allowed on purpose: the flag can arrive mid-session, and cutting the
        // circuit out from under an open tab replaces a redirect with a "connection lost"
        // overlay. Every document request still lands on /password, which is static SSR and
        // opens no circuit of its own.
        var allowed = path.StartsWith(MootifyAuth.ChangePasswordPath, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/auth/password", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/auth/logout", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        if (allowed)
        {
            await next(context);
            return;
        }

        // Unreachable today — a token principal never carries the claim — but a redirect to
        // HTML is the one answer a phone can't do anything with, so it doesn't get to become
        // reachable by accident later.
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "An admin reset this password. Sign in on the website to choose a new one.",
            });
            return;
        }

        context.Response.Redirect(MootifyAuth.ChangePasswordPath);
    }
}
