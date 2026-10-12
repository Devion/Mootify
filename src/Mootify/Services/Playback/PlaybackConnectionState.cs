using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Mootify.Services.Playback;

/// <summary>Server heartbeats must stop when the browser disconnects, even while its circuit is retained.</summary>
public sealed class PlaybackConnectionState : CircuitHandler
{
    public bool IsConnected { get; private set; }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }
}
