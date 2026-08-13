using Mootify.Services.Auth;

namespace Mootify.Services.Settings;

/// <summary>
/// Until the admin account exists, everything goes to /setup. Cheap: one volatile bool
/// per request, no database round trip.
/// </summary>
public sealed class SetupMiddleware(RequestDelegate next, SetupState state)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (state.IsComplete)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";

        // The setup page itself, its form post, and the assets it needs to render.
        var allowed = path.StartsWith(MootifyAuth.SetupPath, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/auth/setup", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        if (allowed)
        {
            await next(context);
            return;
        }

        // A redirect to an HTML page is a useless answer for the Android app: it would read a
        // 200 full of markup and have nothing to say to the user. Tell it the truth instead —
        // the server is up, but nobody has finished setting it up yet.
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This Mootify hasn't been set up yet. Open it in a browser to create the admin account.",
            });
            return;
        }

        context.Response.Redirect(MootifyAuth.SetupPath);
    }
}
