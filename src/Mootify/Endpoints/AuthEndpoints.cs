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
            CancellationToken ct) =>
        {
            var result = await accounts.SignInAsync(username, password, ct);

            if (!result.Succeeded)
            {
                return Redirect(MootifyAuth.LoginPath, result.Error!, returnUrl);
            }

            await SignInAsync(http, result);
            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).RequireRateLimiting("login");

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

            await SignInAsync(http, result);
            return Results.Redirect("/");
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
