using Mootify.Services.Library;

namespace Mootify.Tests;

/// <summary>
/// Searching the library.
///
/// The bug this replaced was three different answers to one question: the library page filtered
/// artist names only, the search box matched title/artist/album, and the API matched track titles
/// only — so "nevermind" found songs on one page, nothing on another, and nothing in the car.
/// The predicates now live in one place and these tests are what pin down what "matches" means.
///
/// Real SQLite, not the InMemory provider, because <c>EF.Functions.Like</c> is the whole point:
/// <c>string.Contains</c> translates to <c>instr()</c> and is case-sensitive, which finds nothing
/// anybody actually types.
/// </summary>
public sealed class LibrarySearchServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private LibrarySearchService _service = null!;

    public async Task InitializeAsync()
    {
        _db = new TestDatabase();
        _service = new LibrarySearchService(_db);

        await _db.AddAlbumAsync("Nirvana", "Nevermind", ["Smells Like Teen Spirit", "Come As You Are"], year: 1991);
        await _db.AddAlbumAsync("The Cowbells", "Pasture Sounds", ["Moo Anthem", "Grass Is Greener"], year: 1994);
        await _db.AddAlbumAsync("Blue Oyster Cult", "Agents of Fortune", ["Don't Fear the Reaper"], year: 1976);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---- what a term reaches ---------------------------------------------

    [Fact]
    public async Task A_song_title_finds_the_song()
    {
        var page = await _service.TracksAsync("teen spirit");

        Assert.Equal("Smells Like Teen Spirit", Assert.Single(page.Rows).Title);
    }

    [Fact]
    public async Task An_album_title_finds_the_songs_on_it()
    {
        // This is the case that used to find nothing: a track-title-only match can't answer
        // "play Nevermind", and that is what people type.
        var page = await _service.TracksAsync("nevermind");

        Assert.Equal(2, page.Total);
        Assert.All(page.Rows, r => Assert.Equal("Nevermind", r.AlbumTitle));
    }

    [Fact]
    public async Task An_artist_name_finds_their_songs()
    {
        var page = await _service.TracksAsync("nirvana");

        Assert.Equal(2, page.Total);
        Assert.All(page.Rows, r => Assert.Equal("Nirvana", r.ArtistName));
    }

    [Fact]
    public async Task An_artist_name_finds_their_albums()
    {
        // "nirvana" has to find Nevermind rather than finding nothing, so albums match on the
        // artist as well as on their own title.
        var page = await _service.AlbumsAsync("nirvana");

        Assert.Equal("Nevermind", Assert.Single(page.Rows).Title);
    }

    [Fact]
    public async Task Matching_is_case_insensitive_and_partial()
    {
        Assert.Equal(2, (await _service.TracksAsync("NEVERMIND")).Total);
        Assert.Equal(2, (await _service.TracksAsync("everm")).Total);
    }

    [Fact]
    public async Task Artists_match_on_their_name()
    {
        var page = await _service.ArtistsAsync("cowbell");

        var artist = Assert.Single(page.Rows);
        Assert.Equal("The Cowbells", artist.Name);
        Assert.Equal(1, artist.AlbumCount);
        Assert.Equal(2, artist.TrackCount);
    }

    [Fact]
    public async Task A_term_that_matches_nothing_returns_nothing()
    {
        Assert.Empty((await _service.TracksAsync("zzzz")).Rows);
        Assert.Empty((await _service.AlbumsAsync("zzzz")).Rows);
        Assert.Empty((await _service.ArtistsAsync("zzzz")).Rows);
    }

    // ---- the empty term ---------------------------------------------------

    [Fact]
    public async Task An_empty_term_leaves_a_browsable_list_whole()
    {
        // Filtering by nothing is not filtering — the library page with an empty box is still
        // the artist grid it always was.
        Assert.Equal(3, (await _service.ArtistsAsync("")).Total);
        Assert.Equal(3, (await _service.ArtistsAsync(null)).Total);
        Assert.Equal(3, (await _service.AlbumsAsync("")).Total);
    }

    [Fact]
    public async Task An_empty_term_is_not_a_request_for_every_song()
    {
        // Tracks are the one list where "everything" is never a useful answer, and building it
        // is the query worth never running by accident.
        Assert.Empty((await _service.TracksAsync("")).Rows);
        Assert.Empty((await _service.TracksAsync(null)).Rows);
        Assert.Empty(await _service.TrackIdsAsync(""));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("%%__%")]
    public async Task A_term_of_nothing_but_wildcards_means_nothing_was_typed(string term)
    {
        // Otherwise "%" is a search that matches the entire library, which is the opposite of
        // what somebody who typed it by accident wants.
        Assert.Empty((await _service.TracksAsync(term)).Rows);
        Assert.Equal(3, (await _service.ArtistsAsync(term)).Total);
    }

    [Fact]
    public async Task Wildcards_inside_a_real_term_are_dropped_not_honoured()
    {
        // "%nirvana%" is somebody pasting, not somebody writing a LIKE pattern.
        Assert.Equal(2, (await _service.TracksAsync("%nirvana%")).Total);
    }

    // ---- paging -----------------------------------------------------------

    [Fact]
    public async Task Results_page_and_the_total_is_the_whole_answer()
    {
        await _db.AddAlbumAsync("Padding", "Filler", [.. Enumerable.Range(0, 30).Select(i => $"Filler Song {i}")]);

        var first = await _service.TracksAsync("filler song", skip: 0, take: 10);

        Assert.Equal(10, first.Rows.Count);
        Assert.Equal(30, first.Total);
        Assert.Equal(3, first.PageCount);
        Assert.True(first.HasNext);
        Assert.False(first.HasPrevious);

        var last = await _service.TracksAsync("filler song", skip: 20, take: 10);

        Assert.False(last.HasNext);
        Assert.Equal(3, last.PageNumber);
    }

    [Fact]
    public async Task A_short_result_knows_its_own_total_without_a_second_scan()
    {
        // The count is a second full scan over a LIKE, so the first page of a short result
        // answers it from what came back. This asserts the answer, which is what matters —
        // the saving is why it's written that way.
        var page = await _service.TracksAsync("teen spirit", skip: 0, take: 50);

        Assert.Single(page.Rows);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task The_page_size_is_clamped()
    {
        var huge = await _service.ArtistsAsync(null, 0, 100_000);
        var zero = await _service.ArtistsAsync(null, 0, 0);

        Assert.Equal(LibrarySearchService.MaxPageSize, huge.Take);
        Assert.Equal(LibrarySearchService.DefaultPageSize, zero.Take);
    }

    // ---- ordering and absence ---------------------------------------------

    [Fact]
    public async Task Artists_come_back_in_sort_order()
    {
        Assert.Equal(
            ["Blue Oyster Cult", "Nirvana", "The Cowbells"],
            (await _service.ArtistsAsync(null)).Rows.Select(a => a.Name));
    }

    [Fact]
    public async Task An_artist_with_nothing_playable_is_hidden()
    {
        // Dead rows left by a removed folder. The scanner keeps them so playlists don't break;
        // every list hides them.
        await _db.AddAlbumAsync("Gone Band", "Gone Record", ["Vanished"], present: false);

        Assert.DoesNotContain("Gone Band", (await _service.ArtistsAsync(null)).Rows.Select(a => a.Name));
        Assert.Empty((await _service.AlbumsAsync("gone")).Rows);
        Assert.Empty((await _service.TracksAsync("vanished")).Rows);
    }

    // ---- play these -------------------------------------------------------

    [Fact]
    public async Task Track_ids_are_every_match_in_the_order_shown()
    {
        var page = await _service.TracksAsync("nirvana");
        var ids = await _service.TrackIdsAsync("nirvana");

        Assert.Equal(page.Rows.Select(r => r.Id), ids);
    }

    [Fact]
    public async Task Track_ids_are_bounded()
    {
        // A term like "the" matches thousands, and a queue that long is a mistake somebody made
        // with one click rather than a feature.
        await _db.AddAlbumAsync("Padding", "Filler", [.. Enumerable.Range(0, 60).Select(i => $"Filler Song {i}")]);

        Assert.Equal(10, (await _service.TrackIdsAsync("filler", limit: 10)).Count);
        Assert.Equal(60, (await _service.TrackIdsAsync("filler", limit: 100_000)).Count);
    }
}
