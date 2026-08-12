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

        context.Response.Redirect(MootifyAuth.SetupPath);
    }
}
