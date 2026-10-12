using Mootify.Services.Playback;

namespace Mootify.Tests;

public sealed class PlaybackConnectionStateTests
{
    [Fact]
    public async Task Only_a_connected_browser_can_receive_server_heartbeats()
    {
        var connection = new PlaybackConnectionState();
        Assert.False(connection.IsConnected);

        await connection.OnConnectionUpAsync(null!, CancellationToken.None);
        Assert.True(connection.IsConnected);
        await connection.OnConnectionDownAsync(null!, CancellationToken.None);
        Assert.False(connection.IsConnected);

        await connection.OnConnectionUpAsync(null!, CancellationToken.None);
        Assert.True(connection.IsConnected);
        await connection.OnCircuitClosedAsync(null!, CancellationToken.None);
        Assert.False(connection.IsConnected);
    }
}
