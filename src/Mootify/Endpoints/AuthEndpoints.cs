using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Mootify.Services.Auth;

namespace Mootify.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Form posts rather than circuit calls: the sign-in cookie has to be written before
        // the response starts, which an interactive Blazor circuit can't do.

        app.MapPost("/auth/login", async (
            HttpContext http,
            [FromForm] string username,
            [FromForm] string password,
            [FromForm] string? returnUrl,
            AccountService accounts,
            LoginThrottle throttle,
            CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();

            // Locked out: no password check, no new failure recorded. Not counting attempts
            // made during a lockout is what keeps "try again in 12 minutes" true.
            if (throttle.GetLockout(username, ip) is { } remaining)
            {
                return Redirect(MootifyAuth.LoginPath, LockoutMessage(remaining), returnUrl);
            }

            // Before the check, not after, and regardless of the outcome — see LoginThrottle.
            // A correct password answering instantly while a wrong one waits would tell an
            // attacker which was which without reading the response.
            var delay = throttle.GetDelay(username, ip);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            var result = await accounts.SignInAsync(username, password, ct);

            if (!result.Succeeded)
            {
                throttle.RecordFailure(username, ip);

                // If that was the one that tripped it, say so now rather than letting them
                // discover it on the next try.
                if (throttle.GetLockout(username, ip) is { } locked)
                {
                    return Redirect(MootifyAuth.LoginPath, LockoutMessage(locked), returnUrl);
                }

                return Redirect(MootifyAuth.LoginPath, result.Error!, returnUrl);
            }

            throttle.RecordSuccess(username, ip);
            await SignInAsync(http, result);

            // Straight to the change screen rather than wherever they were headed. The
            // middleware would send them there anyway; doing it here is what makes the reason
            // obvious instead of looking like a bounced navigation.
            if (MootifyAuth.MustChangePassword(result.Principal))
            {
                return Results.Redirect(MootifyAuth.ChangePasswordPath);
            }

            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).RequireRateLimiting("login");

        // The forced change, and only that: it needs the current password like any other change,
        // which the user has — they just signed in with it. A static SSR post rather than the
        // interactive /account page because the whole app is gated while the flag is set.
        app.MapPost("/auth/password", async (
            HttpContext http,
            [FromForm] string currentPassword,
            [FromForm] string newPassword,
            [FromForm] string confirmPassword,
            AccountService accounts,
            CancellationToken ct) =>
        {
            var raw = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(raw, out var userId))
            {
                return Results.Redirect(MootifyAuth.LoginPath);
            }

            var (ok, error) = await accounts.ChangePasswordAsync(
                userId, currentPassword, newPassword, confirmPassword, ct);

            if (!ok)
            {
                return Redirect(MootifyAuth.ChangePasswordPath, error!, null);
            }

            // The cookie still carries the claim. OnValidatePrincipal drops it on the next
            // request — which this redirect is — so there's nothing to re-issue here.
            return Results.Redirect("/");
        }).RequireAuthorization().RequireRateLimiting("login");

        app.MapPost("/auth/register", async (
            HttpContext http,
            [FromForm] string username,
            [FromForm] string password,
            [FromForm] string confirmPassword,
            AccountService accounts,
            CancellationToken ct) =>
        {
            var result = await accounts.RegisterAsync(username, password, confirmPassword, ct);

            if (!result.Succeeded)
            {
                return Redirect(MootifyAuth.RegisterPath, result.Error!, null);
            }

            return Results.Redirect("/login?pending=true");
        }).RequireRateLimiting("login");

        app.MapPost("/auth/setup", async (
            HttpContext http,
            [FromForm] string username,
            [FromForm] string password,
            [FromForm] string confirmPassword,
            AccountService accounts,
            CancellationToken ct) =>
        {
            var result = await accounts.CompleteSetupAsync(username, password, confirmPassword, ct);

            if (!result.Succeeded)
            {
                return Redirect(MootifyAuth.SetupPath, result.Error!, null);
            }

            await SignInAsync(http, result);
            return Results.Redirect("/");
        }).RequireRateLimiting("login");

        // Antiforgery off here only: this form is posted from an interactive circuit, which has
        // no HttpContext to mint a token from. The worst a forged request achieves is signing
        // somebody out — worth the trade against a token round-trip on every page render.
        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect(MootifyAuth.LoginPath);
        }).DisableAntiforgery();
    }

    private static string LockoutMessage(TimeSpan remaining)
    {
        // Round up: "try again in 0 minutes" is worse than useless.
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"Too many wrong passwords. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
    }

    private static Task SignInAsync(HttpContext http, AuthResult result) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            result.Principal!,
            new AuthenticationProperties { IsPersistent = true });

    private static IResult Redirect(string path, string error, string? returnUrl)
    {
        var url = $"{path}?error={Uri.EscapeDataString(error)}";

        if (!string.IsNullOrWhiteSpace(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        {
            url += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        }

        return Results.Redirect(url);
    }

    /// <summary>Only ever redirect somewhere on this site.</summary>
    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
            ? returnUrl
            : "/";
}
