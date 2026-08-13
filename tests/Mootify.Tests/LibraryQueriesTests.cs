using Mootify.Endpoints.Api;

namespace Mootify.Tests;

/// <summary>
/// The API's library reads, against real SQLite. Every test here is a query that either
/// translates or doesn't — an in-memory provider would pass all of them and the app would still
/// return an empty list to a phone.
/// </summary>
public sealed class LibraryQueriesTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---- search ----------------------------------------------------------

    [Fact]
    public async Task Search_ignores_case()
    {
        // The regression this exists for: EF translates string.Contains to instr(), which SQLite
        // evaluates case-sensitively. Nobody types "Nevermind" with the capital in the right
        // place, and a case-sensitive library search finds nothing.
        await _db.AddAlbumAsync("Nirvana", "Nevermind", ["Smells Like Teen Spirit", "Come As You Are"]);

        await using var db = _db.CreateDbContext();

        var upper = await LibraryQueries.SearchAsync(db, "NEVERMIND", 20, default);
        var lower = await LibraryQueries.SearchAsync(db, "nevermind", 20, default);

        Assert.Single(upper.Albums);
        Assert.Single(lower.Albums);
        Assert.Equal("Nevermind", upper.Albums[0].Title);
    }

    [Fact]
    public async Task Search_finds_an_album_by_its_artist()
    {
        // "nirvana" has to find Nevermind. Matching only the title is the version of this that
        // looks fine in a test and is useless in a car.
        await _db.AddAlbumAsync("Nirvana", "Nevermind", ["Lithium"]);

        await using var db = _db.CreateDbContext();
        var results = await LibraryQueries.SearchAsync(db, "nirvana", 20, default);

        Assert.Single(results.Artists);
        Assert.Single(results.Albums);
        Assert.Equal("Nevermind", results.Albums[0].Title);
    }

    [Fact]
    public async Task Search_finds_tracks_titles_and_all()
    {
        await _db.AddAlbumAsync("Blue Öyster Cult", "Agents of Fortune", ["Don't Fear the Reaper"]);

        await using var db = _db.CreateDbContext();
        var results = await LibraryQueries.SearchAsync(db, "reaper", 20, default);

        Assert.Single(results.Tracks);
        Assert.Equal("Don't Fear the Reaper", results.Tracks[0].Title);
        Assert.Empty(results.Albums);
    }

    [Fact]
    public async Task A_wildcard_is_not_a_search_for_everything()
    {
        // The query goes into a LIKE pattern. Passing "%" through would turn a stray keystroke
        // into "match the entire library".
        await _db.AddAlbumAsync("Nirvana", "Nevermind", ["Lithium"]);

        await using var db = _db.CreateDbContext();
        var results = await LibraryQueries.SearchAsync(db, "%", 20, default);

        Assert.Empty(results.Albums);
        Assert.Empty(results.Tracks);
        Assert.Empty(results.Artists);
    }

    [Fact]
    public async Task Search_respects_its_limit()
    {
        await _db.AddAlbumAsync("Nirvana", "Nevermind", ["Song A", "Song B", "Song C", "Song D"]);

        await using var db = _db.CreateDbContext();
        var results = await LibraryQueries.SearchAsync(db, "song", 2, default);

        Assert.Equal(2, results.Tracks.Count);
    }

    // ---- albums ----------------------------------------------------------

    [Fact]
    public async Task Recently_added_aggregates_a_value_converted_timestamp()
    {
        // MAX(AddedAt) over a DateTimeOffset that goes through a value converter. This is the
        // sort the app's home screen opens on, and the aggregate is exactly the kind of thing
        // that compiles fine and throws at runtime.
        var now = DateTimeOffset.UtcNow;
        await _db.AddAlbumAsync("Old Band", "Ancient", ["One"], addedAt: now.AddDays(-30));
        await _db.AddAlbumAsync("New Band", "Fresh", ["Two"], addedAt: now.AddMinutes(-5));
        await _db.AddAlbumAsync("Middle Band", "Middling", ["Three"], addedAt: now.AddDays(-2));

        await using var db = _db.CreateDbContext();
        var page = await LibraryQueries.AlbumsAsync(
            db, null, null, LibraryQueries.AlbumOrder.RecentFirst, 0, 10, default);

        Assert.Equal(["Fresh", "Middling", "Ancient"], page.Items.Select(a => a.Title));
    }

    [Fact]
    public async Task Album_duration_is_summed_in_sql_and_reported_in_milliseconds()
    {
        await _db.AddAlbumAsync(
            "The Cowbells", "Pasture Sounds", ["A", "B", "C"], trackDuration: TimeSpan.FromMinutes(4));

        await using var db = _db.CreateDbContext();
        var page = await LibraryQueries.AlbumsAsync(
            db, null, null, LibraryQueries.AlbumOrder.ArtistOrder, 0, 10, default);

        var album = Assert.Single(page.Items);
        Assert.Equal(3, album.TrackCount);
        Assert.Equal((long)TimeSpan.FromMinutes(12).TotalMilliseconds, album.DurationMs);
    }

    [Fact]
    public async Task Albums_are_paged_and_report_the_total()
    {
        for (var i = 0; i < 5; i++)
        {
            await _db.AddAlbumAsync($"Artist {i}", $"Album {i}", ["Only track"]);
        }

        await using var db = _db.CreateDbContext();
        var page = await LibraryQueries.AlbumsAsync(
            db, null, null, LibraryQueries.AlbumOrder.ArtistOrder, 2, 2, default);

        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Skip);
    }

    [Fact]
    public async Task An_album_with_no_playable_files_is_hidden()
    {
        // The scanner marks tracks absent rather than deleting them, so playlists don't break.
        // Every browse list has to filter them out or the car offers songs that 404.
        await _db.AddAlbumAsync("Ghost Band", "Gone", ["Vanished"], present: false);
        await _db.AddAlbumAsync("Real Band", "Here", ["Playable"]);

        await using var db = _db.CreateDbContext();

        var albums = await LibraryQueries.AlbumsAsync(
            db, null, null, LibraryQueries.AlbumOrder.ArtistOrder, 0, 10, default);
        var artists = await LibraryQueries.ArtistsAsync(db, null, 0, 10, default);

        Assert.Equal("Here", Assert.Single(albums.Items).Title);
        Assert.Equal("Real Band", Assert.Single(artists.Items).Name);
    }

    // ---- tracks ----------------------------------------------------------

    [Fact]
    public async Task Album_tracks_come_back_in_album_order()
    {
        var album = await _db.AddAlbumAsync("Pink Floyd", "The Wall", ["In the Flesh?", "The Thin Ice", "Another Brick"]);

        await using var db = _db.CreateDbContext();
        var tracks = await LibraryQueries.AlbumTracksAsync(db, album.Id, default);

        Assert.Equal(["In the Flesh?", "The Thin Ice", "Another Brick"], tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task An_artists_tracks_are_grouped_by_album_oldest_first()
    {
        // "Play this artist" in the car. Chronological by album, then in album order — a shuffled
        // discography is what shuffle is for.
        await _db.AddAlbumAsync("Queen", "Queen II", ["Ogre Battle"], year: 1974);
        await _db.AddAlbumAsync("Queen", "A Night at the Opera", ["Death on Two Legs", "Lazing"], year: 1975);

        await using var db = _db.CreateDbContext();

        var artistId = (await LibraryQueries.ArtistsAsync(db, "Queen", 0, 1, default)).Items[0].Id;
        var tracks = await LibraryQueries.ArtistTracksAsync(db, artistId, default);

        Assert.Equal(["Ogre Battle", "Death on Two Legs", "Lazing"], tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task A_saved_queue_comes_back_in_the_order_it_was_saved()
    {
        // The whole point of TracksByIdAsync. A queue restored in SQL's preferred order is not
        // the queue anybody saved.
        await _db.AddAlbumAsync("Artist", "Album", ["First", "Second", "Third"]);

        await using var db = _db.CreateDbContext();
        var tracks = await LibraryQueries.AlbumTracksAsync(db, (await FirstAlbumIdAsync()), default);

        var shuffled = new List<Guid> { tracks[2].Id, tracks[0].Id, tracks[1].Id };
        var restored = await LibraryQueries.TracksByIdAsync(db, shuffled, default);

        Assert.Equal(["Third", "First", "Second"], restored.Select(t => t.Title));
    }

    [Fact]
    public async Task A_queue_entry_whose_track_is_gone_is_dropped_not_faked()
    {
        await _db.AddAlbumAsync("Artist", "Album", ["Still here"]);

        await using var db = _db.CreateDbContext();
        var present = (await LibraryQueries.AlbumTracksAsync(db, await FirstAlbumIdAsync(), default))[0];

        var restored = await LibraryQueries.TracksByIdAsync(db, [Guid.NewGuid(), present.Id], default);

        Assert.Equal("Still here", Assert.Single(restored).Title);
    }

    [Fact]
    public async Task A_track_carries_the_urls_a_client_needs()
    {
        // The client is handed relative URLs and never a file path — Track.Path is an absolute
        // path on the server's share.
        var album = await _db.AddAlbumAsync("Artist", "Album", ["Song"]);

        await using var db = _db.CreateDbContext();
        var track = (await LibraryQueries.AlbumTracksAsync(db, album.Id, default))[0];

        Assert.Equal($"/media/{track.Id}", track.StreamUrl);
        Assert.Equal($"/art/album/{album.Id}", track.ArtUrl);
    }

    // ---- stats -----------------------------------------------------------

    [Fact]
    public async Task Stats_on_an_empty_library_are_zeroes_not_an_exception()
    {
        // Every install starts here, and MAX() over nothing throws where SUM() returns 0.
        await using var db = _db.CreateDbContext();
        var stats = await LibraryQueries.StatsAsync(db, default);

        Assert.Equal(0, stats.Tracks);
        Assert.Equal(0, stats.Albums);
        Assert.Equal(0, stats.Artists);
        Assert.Equal(0, stats.DurationMs);
        Assert.Null(stats.LastAddedAt);
    }

    [Fact]
    public async Task Stats_count_what_is_playable()
    {
        await _db.AddAlbumAsync("Band", "Album", ["A", "B"], trackDuration: TimeSpan.FromMinutes(5));
        await _db.AddAlbumAsync("Band", "Missing", ["C"], present: false);

        await using var db = _db.CreateDbContext();
        var stats = await LibraryQueries.StatsAsync(db, default);

        Assert.Equal(2, stats.Tracks);
        Assert.Equal(1, stats.Albums);
        Assert.Equal(1, stats.Artists);
        Assert.Equal((long)TimeSpan.FromMinutes(10).TotalMilliseconds, stats.DurationMs);
        Assert.NotNull(stats.LastAddedAt);
    }

    private async Task<Guid> FirstAlbumIdAsync()
    {
        await using var db = _db.CreateDbContext();
        var page = await LibraryQueries.AlbumsAsync(
            db, null, null, LibraryQueries.AlbumOrder.ArtistOrder, 0, 1, default);
        return page.Items[0].Id;
    }
}
