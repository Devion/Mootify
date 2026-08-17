using Mootify.Data;
using Mootify.Services.Playback;

namespace Mootify.Tests;

/// <summary>
/// What the queue drawer lists. The interesting part is that it works in positions of the
/// *play order* rather than of the queue — with shuffle on those differ, and a preview that
/// got that wrong would be believable and wrong at the same time.
/// </summary>
public sealed class QueuePreviewTests
{
    [Fact]
    public void Up_next_is_the_positions_after_the_cursor()
    {
        var positions = PlayerService.UpNextPositions(orderCount: 6, cursor: 1, RepeatMode.Off, take: 50);

        Assert.Equal([2, 3, 4, 5], positions);
    }

    [Fact]
    public void The_last_track_has_nothing_after_it()
    {
        Assert.Empty(PlayerService.UpNextPositions(orderCount: 6, cursor: 5, RepeatMode.Off, take: 50));
        Assert.Equal(0, PlayerService.UpNextTotal(orderCount: 6, cursor: 5, RepeatMode.Off));
    }

    [Fact]
    public void Nothing_is_playing_yet_means_nothing_is_up_next()
    {
        // The cursor is -1 until the first track starts; off-by-one here would list the whole
        // queue as "next" while the player is idle.
        Assert.Empty(PlayerService.UpNextPositions(orderCount: 6, cursor: -1, RepeatMode.Off, take: 50));
        Assert.Empty(PlayerService.UpNextPositions(orderCount: 0, cursor: -1, RepeatMode.Off, take: 50));
    }

    [Fact]
    public void Repeat_all_wraps_round_to_the_top()
    {
        var positions = PlayerService.UpNextPositions(orderCount: 5, cursor: 3, RepeatMode.All, take: 50);

        Assert.Equal([4, 0, 1, 2], positions);
    }

    [Fact]
    public void Repeat_all_stops_one_short_of_the_current_track()
    {
        // Otherwise the song you are listening to appears in its own "up next" list.
        var positions = PlayerService.UpNextPositions(orderCount: 5, cursor: 3, RepeatMode.All, take: 50);

        Assert.DoesNotContain(3, positions);
        Assert.Equal(4, PlayerService.UpNextTotal(orderCount: 5, cursor: 3, RepeatMode.All));
    }

    [Fact]
    public void Repeat_one_leaves_the_queue_alone()
    {
        // Repeat-one changes what the `ended` event does, not what is queued: pressing next
        // still walks on, so the list below is unchanged and the drawer says so in words.
        Assert.Equal(
            PlayerService.UpNextPositions(orderCount: 5, cursor: 1, RepeatMode.Off, take: 50),
            PlayerService.UpNextPositions(orderCount: 5, cursor: 1, RepeatMode.One, take: 50));
    }

    [Fact]
    public void Take_caps_the_list_but_not_the_count()
    {
        // The drawer renders `take` rows and prints the rest as "and N more" — a silent cut
        // would read as a queue that ends where it doesn't.
        var positions = PlayerService.UpNextPositions(orderCount: 600, cursor: 0, RepeatMode.Off, take: 50);

        Assert.Equal(50, positions.Count);
        Assert.Equal(599, PlayerService.UpNextTotal(orderCount: 600, cursor: 0, RepeatMode.Off));
    }

    [Fact]
    public void A_queue_of_one_never_previews_itself()
    {
        foreach (var repeat in new[] { RepeatMode.Off, RepeatMode.All, RepeatMode.One })
        {
            Assert.Empty(PlayerService.UpNextPositions(orderCount: 1, cursor: 0, repeat, take: 50));
        }
    }
}
