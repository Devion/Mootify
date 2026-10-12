using Mootify.Services.Playback;

namespace Mootify.Tests;

public sealed class SharedPlaybackProgressTests
{
    [Fact]
    public void Playing_progress_advances_and_stops_at_duration()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new SharedQueueSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Host",
            [], 0, true, 10, 60, now);
        Assert.Equal(15, snapshot.PositionAt(now.AddSeconds(5)));
        Assert.Equal(60, snapshot.PositionAt(now.AddMinutes(1)));
        Assert.Equal(10, snapshot.PositionAt(now.AddSeconds(-5)));
    }

    [Fact]
    public void Pause_and_seek_replace_the_displayed_position()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new SharedQueueSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Host",
            [], 0, false, 10, 60, now);
        Assert.Equal(10, snapshot.PositionAt(now.AddSeconds(5)));
        snapshot = snapshot with { Position = 40, IsPlaying = true, UpdatedAt = now.AddSeconds(5) };
        Assert.Equal(42, snapshot.PositionAt(now.AddSeconds(7)));
    }
}
