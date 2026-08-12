using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.MusicBrainz;

namespace Mootify.Tests;

/// <summary>
/// Which pressing of an album to show. No network — the payloads here are trimmed copies of
/// real MusicBrainz responses.
/// </summary>
public sealed class MusicBrainzClientTests
{
    private static MusicBrainzClient Create() =>
        new(new HttpClient { BaseAddress = new Uri("https://example.invalid/") },
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 }),
            new StaticOptionsMonitor<MusicBrainzOptions>(new MusicBrainzOptions()),
            NullLogger<MusicBrainzClient>.Instance);

    private static List<MbRelease> Releases(string json) =>
        JsonSerializer.Deserialize<List<MbRelease>>(json)!;

    /// <summary>A release with <paramref name="tracks"/> tracks on one disc.</summary>
    private static string Release(string title, string? status, string? date, int tracks)
    {
        var items = string.Join(",", Enumerable.Range(1, tracks).Select(i =>
            $$$"""{"position":{{{i}}},"title":"Track {{{i}}}","length":200000,"recording":{"id":"rec-{{{title}}}-{{{i}}}"}}"""));

        var statusJson = status is null ? "null" : $"\"{status}\"";
        var dateJson = date is null ? "null" : $"\"{date}\"";

        return $$"""
            {"id":"rel-{{title}}","title":"{{title}}","status":{{statusJson}},"date":{{dateJson}},
             "media":[{"position":1,"tracks":[{{items}}]}]}
            """;
    }

    // ---- network failure -------------------------------------------------

    [Fact]
    public async Task An_unreachable_MusicBrainz_yields_no_tracks_rather_than_throwing()
    {
        // A tracklist we can't fetch is a worse album view, not a broken page — the UI falls
        // back to "request the whole album".
        Assert.Empty(await Create().GetTracksAsync("48117b90-a16e-34ca-a514-19c702df1158"));
    }

    [Fact]
    public async Task A_blank_release_group_is_not_looked_up_at_all()
    {
        Assert.Empty(await Create().GetTracksAsync(""));
    }

    // ---- picking the pressing -------------------------------------------

    [Fact]
    public void Official_pressings_beat_everything_else()
    {
        // Bootlegs and promos carry odd and often wrong tracklists.
        var releases = Releases($"[{Release("Bootleg", "Bootleg", "1999", 20)},{Release("Proper", "Official", "2001", 14)}]");

        Assert.Equal("Proper", MusicBrainzClient.PickRelease(releases)!.Title);
    }

    [Fact]
    public void The_earliest_official_pressing_wins()
    {
        // The album as released, not a remaster with bonus discs bolted on.
        var releases = Releases($"[{Release("Remaster", "Official", "2014", 22)},{Release("Original", "Official", "2001-03-12", 14)}]");

        Assert.Equal("Original", MusicBrainzClient.PickRelease(releases)!.Title);
    }

    [Fact]
    public void A_fuller_listing_breaks_a_tie_on_date()
    {
        // Same day, two regions, one of them catalogued incompletely. Never show the short one.
        var releases = Releases($"[{Release("Partial", "Official", "2001", 3)},{Release("Complete", "Official", "2001", 14)}]");

        Assert.Equal("Complete", MusicBrainzClient.PickRelease(releases)!.Title);
    }

    [Fact]
    public void An_undated_pressing_sorts_last_rather_than_first()
    {
        // A missing date must not read as "year zero" and beat the real release.
        var releases = Releases($"[{Release("Undated", "Official", null, 14)},{Release("Dated", "Official", "2001", 14)}]");

        Assert.Equal("Dated", MusicBrainzClient.PickRelease(releases)!.Title);
    }

    [Fact]
    public void An_unofficial_pressing_is_better_than_none()
    {
        var releases = Releases($"[{Release("OnlyOne", null, "2001", 12)}]");

        Assert.Equal("OnlyOne", MusicBrainzClient.PickRelease(releases)!.Title);
    }

    [Fact]
    public void Trackless_pressings_are_ignored()
    {
        var releases = Releases($"[{Release("Empty", "Official", "1999", 0)},{Release("Real", "Official", "2001", 14)}]");

        Assert.Equal("Real", MusicBrainzClient.PickRelease(releases)!.Title);
    }

    [Fact]
    public void Nothing_usable_gives_nothing()
    {
        Assert.Null(MusicBrainzClient.PickRelease([]));
        Assert.Null(MusicBrainzClient.PickRelease(Releases($"[{Release("Empty", "Official", "2001", 0)}]")));
    }

    // ---- flattening ------------------------------------------------------

    [Fact]
    public void Multi_disc_track_numbers_run_straight_through()
    {
        // Numbering restarts per disc in MusicBrainz, so a double album would otherwise show
        // two track 1s and the request matcher would have two candidates.
        var json = """
            [{"id":"r","title":"Double","status":"Official","date":"2001","media":[
              {"position":2,"tracks":[{"position":1,"title":"D2T1","length":60000,"recording":{"id":"rec-c"}}]},
              {"position":1,"tracks":[
                {"position":1,"title":"D1T1","length":60000,"recording":{"id":"rec-a"}},
                {"position":2,"title":"D1T2","length":60000,"recording":{"id":"rec-b"}}]}
            ]}]
            """;

        var tracks = MusicBrainzClient.Flatten(MusicBrainzClient.PickRelease(Releases(json)));

        Assert.Equal([1, 2, 3], tracks.Select(t => t.Position));
        Assert.Equal(["D1T1", "D1T2", "D2T1"], tracks.Select(t => t.Title));
        Assert.Equal(["rec-a", "rec-b", "rec-c"], tracks.Select(t => t.RecordingId));
    }

    [Fact]
    public void A_track_with_no_length_still_appears()
    {
        var json = """
            [{"id":"r","title":"A","status":"Official","date":"2001","media":[
              {"position":1,"tracks":[{"position":1,"title":"Untimed","recording":{"id":"rec-a"}}]}]}]
            """;

        var track = Assert.Single(MusicBrainzClient.Flatten(MusicBrainzClient.PickRelease(Releases(json))));

        Assert.Equal("Untimed", track.Title);
        Assert.Equal(TimeSpan.Zero, track.Duration);
    }
}
