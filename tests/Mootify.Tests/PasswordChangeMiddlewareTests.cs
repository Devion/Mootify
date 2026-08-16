using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Mootify.Services.Auth;

namespace Mootify.Tests;

/// <summary>
/// The gate that makes "immediately" true. An account holding a one-time password an admin
/// handed out can reach the page that replaces it and nothing else — and the one thing it must
/// never do is answer a phone with a redirect to HTML.
/// </summary>
public sealed class PasswordChangeMiddlewareTests
{
    private static (DefaultHttpContext Context, bool CalledNext) Run(bool mustChange, string path)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (mustChange) claims.Add(new Claim(MootifyAuth.MustChangePasswordClaim, "true"));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, MootifyAuth.Scheme)),
        };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new PasswordChangeMiddleware(_ => { called = true; return Task.CompletedTask; });

        middleware.InvokeAsync(context).GetAwaiter().GetResult();
        return (context, called);
    }

    [Fact]
    public void Everything_goes_to_the_change_page()
    {
        var (context, calledNext) = Run(mustChange: true, "/library");

        Assert.False(calledNext);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/password", context.Response.Headers.Location);
    }

    [Fact]
    public void The_change_page_its_post_and_the_way_out_still_work()
    {
        // /_blazor included: the flag can land mid-session, and killing the circuit under an
        // open tab shows a "connection lost" overlay instead of the reason.
        foreach (var path in new[] { "/password", "/auth/password", "/auth/logout", "/_blazor", "/app.css" })
        {
            var (_, calledNext) = Run(mustChange: true, path);
            Assert.True(calledNext, $"{path} should have been allowed through");
        }
    }

    [Fact]
    public void The_api_gets_json_rather_than_a_redirect()
    {
        var (context, calledNext) = Run(mustChange: true, "/api/v1/library/artists");

        Assert.False(calledNext);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location));

        context.Response.Body.Position = 0;
        Assert.Contains("website", new StreamReader(context.Response.Body).ReadToEnd());
    }

    [Fact]
    public void An_account_that_isnt_flagged_never_notices()
    {
        var (_, calledNext) = Run(mustChange: false, "/library");
        Assert.True(calledNext);
    }
}
