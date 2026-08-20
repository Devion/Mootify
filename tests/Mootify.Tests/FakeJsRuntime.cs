using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;

namespace Mootify.Tests;

/// <summary>
/// Stands in for the browser so <see cref="Services.Playback.PlayerService"/> can be tested at all.
///
/// The player's interesting behaviour — writing down what was listened to, topping the queue up
/// when it runs out — is pure bookkeeping that happens to sit next to an <c>&lt;audio&gt;</c>
/// element. Without this the only way to exercise any of it is a real browser with a working
/// sound device, which is exactly the thing CI does not have.
///
/// Every call is recorded rather than ignored, so a test can assert what the player asked the page
/// to do ("play", "pause") as well as what it wrote to the database.
/// </summary>
public sealed class FakeJsRuntime : IJSRuntime
{
    public sealed record Call(string Identifier, object?[] Args);

    public List<Call> Calls { get; } = [];

    /// <summary>Just the identifiers, which is what most assertions actually care about.</summary>
    public IEnumerable<string> Names => Calls.Select(c => c.Identifier);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        Record<TValue>(identifier, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier, CancellationToken cancellationToken, object?[]? args) =>
        Record<TValue>(identifier, args);

    private ValueTask<TValue> Record<TValue>(string identifier, object?[]? args)
    {
        Calls.Add(new Call(identifier, args ?? []));

        // The one call that has to hand something back: importing the player module.
        if (typeof(TValue) == typeof(IJSObjectReference))
        {
            return ValueTask.FromResult((TValue)(object)new FakeModule(this));
        }

        return ValueTask.FromResult(default(TValue)!);
    }

    /// <summary>The imported module. Forwards straight back so there is one list of calls.</summary>
    private sealed class FakeModule(FakeJsRuntime owner) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            owner.Record<TValue>(identifier, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            owner.Record<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Makes <see cref="Mootify.Services.Auth.CurrentUser"/> resolve to a known account, since
/// everything the player records is attributed to one.
/// </summary>
public sealed class FakeAuthStateProvider(Guid userId)
    : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
{
    public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState>
        GetAuthenticationStateAsync()
    {
        var identity = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString()),
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.Name, "devion"),
            ],
            authenticationType: "Test");

        return Task.FromResult(
            new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
                new System.Security.Claims.ClaimsPrincipal(identity)));
    }
}
