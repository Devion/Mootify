using Microsoft.AspNetCore.Http;
using Mootify.Services.Settings;

namespace Mootify.Tests;

/// <summary>
/// What happens before anybody has created the admin account. The website is sent to /setup; a
/// client that speaks JSON has to be told in JSON, because a 200 full of HTML is a page the
/// Android app cannot read and cannot explain to its user.
/// </summary>
public sealed class SetupMiddlewareTests
{
    private static (DefaultHttpContext Context, bool CalledNext) Run(SetupState state, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var called = false;
        var middleware = new SetupMiddleware(_ => { called = true; return Task.CompletedTask; }, state);

        middleware.InvokeAsync(context).GetAwaiter().GetResult();
        return (context, called);
    }

    private static SetupState Incomplete()
    {
        var state = new SetupState();
        state.MarkIncomplete();
        return state;
    }

    [Fact]
    public void A_browser_is_sent_to_setup()
    {
        var (context, calledNext) = Run(Incomplete(), "/library");

        Assert.False(calledNext);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/setup", context.Response.Headers.Location);
    }

    [Fact]
    public void The_api_gets_a_503_with_a_readable_reason()
    {
        var (context, calledNext) = Run(Incomplete(), "/api/v1/library/artists");

        Assert.False(calledNext);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType?.Split(';')[0]);

        context.Response.Body.Position = 0;
        var body = new StreamReader(context.Response.Body).ReadToEnd();

        Assert.Contains("set up", body);
        // Never a redirect: a token client following one would parse the login page as its answer.
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.Location));
    }

    [Fact]
    public void The_setup_page_and_its_assets_still_load()
    {
        foreach (var path in new[] { "/setup", "/auth/setup", "/js/player.js", "/img/cow.png", "/app.css" })
        {
            var (_, calledNext) = Run(Incomplete(), path);
            Assert.True(calledNext, $"{path} should have been allowed through during setup");
        }
    }

    [Fact]
    public void Once_set_up_it_gets_out_of_the_way()
    {
        var state = new SetupState();
        state.MarkComplete();

        var (_, calledNext) = Run(state, "/api/v1/library/artists");
        Assert.True(calledNext);
    }
}
