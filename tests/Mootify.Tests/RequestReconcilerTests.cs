using Mootify.Services.Requests;

namespace Mootify.Tests;

/// <summary>
/// When to ask Lidarr again.
///
/// This is the half that was missing, and it's why a big import looked half broken: a request
/// is searched once when it's made, several hundred of those land on Lidarr at the same time,
/// and a search that comes back empty leaves no trace anywhere. The ones that got grabbed show
/// up in the queue; the rest sat at "Searching" with nothing in the system that would ever ask
/// again — 504 requests, 24 downloads, and both numbers correct.
/// </summary>
public sealed class RequestReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Something_never_searched_is_due_immediately()
    {
        // Null is the normal state for a request that rode in on somebody else's album add,
        // and for every row made before any of this existed.
        Assert.True(RequestReconciler.DueForSearch(attempts: 0, lastSearch: null, Now));
    }

    [Fact]
    public void A_search_a_minute_ago_is_not_due_again()
    {
        Assert.False(RequestReconciler.DueForSearch(1, Now.AddMinutes(-1), Now));
    }

    [Fact]
    public void After_the_first_miss_it_waits_half_an_hour()
    {
        Assert.False(RequestReconciler.DueForSearch(1, Now.AddMinutes(-29), Now));
        Assert.True(RequestReconciler.DueForSearch(1, Now.AddMinutes(-31), Now));
    }

    [Fact]
    public void The_wait_grows_with_each_miss()
    {
        var waits = Enumerable.Range(0, 6).Select(RequestReconciler.SearchBackoff).ToList();

        Assert.Equal(TimeSpan.Zero, waits[0]);
        Assert.Equal(waits, [.. waits.OrderBy(w => w)]);
        Assert.Equal(TimeSpan.FromHours(24), waits[^1]);
    }

    [Fact]
    public void An_album_nobody_carries_stops_being_asked_for()
    {
        // Eight tries over a week is plenty; the seven-day timeout is what ends it after that,
        // and re-asking every ten minutes until then is a thousand searches to learn nothing.
        Assert.False(RequestReconciler.DueForSearch(8, Now.AddDays(-30), Now));
        Assert.False(RequestReconciler.DueForSearch(99, null, Now));
    }

    [Fact]
    public void The_last_attempt_before_the_cap_still_gets_its_turn()
    {
        Assert.True(RequestReconciler.DueForSearch(7, Now.AddDays(-2), Now));
    }

    [Fact]
    public void A_summary_of_nothing_is_all_zeroes()
    {
        Assert.Equal(0, ReconcileSummary.Idle.Open);
        Assert.Equal(0, ReconcileSummary.Idle.AwaitingSearch);
    }
}
