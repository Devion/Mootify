using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Library;

namespace Mootify.Tests;

/// <summary>
/// Finding, remembering and forgetting cover art.
///
/// The remembering is the part worth pinning down. Before it, every request for a cover cost a
/// database query and then a sweep of the album's folder — six names in four extensions — before
/// anything cached was consulted at all, which on a network share is up to 24 stats per cover and
/// fifty covers per screen of the library grid. These tests assert the memo by taking the answer
/// away underneath it: once a look has been remembered, deleting the file it found must not
/// change what comes back, and only <c>Forget</c> or <c>Clear</c> may.
/// </summary>
public sealed class AlbumArtServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private AlbumArtService _art = null!;
    private string _cache = null!;
    private string _folder = null!;
    private Guid _albumId;

    public async Task InitializeAsync()
    {
        _db = new TestDatabase();
        _art = TestArt.Service(_db, out _cache);

        _folder = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_folder);

        await using var db = _db.CreateDbContext();

        var artist = new Artist { Id = Guid.NewGuid(), Name = "The Cowbells", SortName = "Cowbells, The" };
        var album = new Album { Id = Guid.NewGuid(), Title = "Pasture Sounds", ArtistId = artist.Id };

        db.Artists.Add(artist);
        db.Albums.Add(album);

        // The file itself never has to exist: an adjacent cover is found from the folder, and a
        // track TagLib can't open is exactly the "no embedded art" case.
        db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(),
            Path = Path.Combine(_folder, "01 Moo Anthem.mp3"),
            Title = "Moo Anthem",
            ArtistId = artist.Id,
            AlbumId = album.Id,
            TrackNumber = 1,
            Duration = TimeSpan.FromMinutes(3),
            AddedAt = DateTimeOffset.UtcNow,
            IsPresent = true,
        });

        await db.SaveChangesAsync();
        _albumId = album.Id;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cache, recursive: true); } catch { /* best effort */ }
    }

    private string WriteCover(string name = "cover.jpg")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[8]);
        return path;
    }

    private bool HasMissMarker => File.Exists(Path.Combine(_cache, $"{_albumId:n}.none"));

    // ---- finding it -------------------------------------------------------

    [Fact]
    public async Task A_cover_beside_the_music_is_served_from_where_it_already_is()
    {
        var cover = WriteCover();

        var found = await _art.GetAsync(_albumId);

        Assert.Equal(cover, found?.Path);
        Assert.Equal("image/jpeg", found?.ContentType);
    }

    [Fact]
    public async Task An_album_with_no_art_answers_nothing_and_writes_a_marker()
    {
        // The marker is what makes the second miss as cheap as a hit on disk. A library where
        // half the albums have no cover asks for those covers just as often as for the rest.
        Assert.Null(await _art.GetAsync(_albumId));
        Assert.True(HasMissMarker);
    }

    [Fact]
    public async Task An_album_with_no_present_track_has_nowhere_to_look()
    {
        await using (var db = _db.CreateDbContext())
        {
            await db.Tracks.ExecuteUpdateAsync(s => s.SetProperty(t => t.IsPresent, false));
        }

        Assert.Null(await _art.GetAsync(_albumId));
    }

    // ---- remembering it ---------------------------------------------------

    [Fact]
    public async Task The_answer_is_remembered_rather_than_swept_for_twice()
    {
        var cover = WriteCover();
        Assert.Equal(cover, (await _art.GetAsync(_albumId))?.Path);

        // Taking the file away is how a test can tell a remembered answer from a fresh look:
        // a second sweep would find nothing. The endpoint is what turns a dead path into a 404
        // — and it calls Forget when it does, so this doesn't outlive one request in practice.
        File.Delete(cover);

        Assert.Equal(cover, (await _art.GetAsync(_albumId))?.Path);
    }

    [Fact]
    public async Task Having_no_art_is_remembered_too()
    {
        Assert.Null(await _art.GetAsync(_albumId));

        // The cover arrives after we have already concluded there isn't one.
        WriteCover();

        Assert.Null(await _art.GetAsync(_albumId));
    }

    // ---- forgetting it ----------------------------------------------------

    [Fact]
    public async Task Forgetting_one_album_makes_the_next_request_look_again()
    {
        Assert.Null(await _art.GetAsync(_albumId));
        var cover = WriteCover();

        _art.Forget(_albumId);

        Assert.Equal(cover, (await _art.GetAsync(_albumId))?.Path);
    }

    [Fact]
    public async Task Forgetting_takes_the_no_art_marker_with_it()
    {
        // The half that matters. Dropping only the in-memory answer would leave the marker on
        // disk, and the fresh look would read it and conclude "no art" all over again.
        Assert.Null(await _art.GetAsync(_albumId));
        Assert.True(HasMissMarker);

        _art.Forget(_albumId);

        Assert.False(HasMissMarker);
    }

    [Fact]
    public async Task Forgetting_reports_only_the_albums_that_were_holding_an_answer()
    {
        await _art.GetAsync(_albumId);

        Assert.Equal(1, _art.Forget([_albumId, Guid.NewGuid()]));
        Assert.Equal(0, _art.Forget([_albumId]));
    }

    [Fact]
    public async Task Clearing_forgets_everything()
    {
        Assert.Null(await _art.GetAsync(_albumId));
        Assert.True(HasMissMarker);

        var cover = WriteCover();
        var removed = _art.Clear();

        Assert.Equal(1, removed);                 // the marker
        Assert.False(HasMissMarker);
        Assert.Equal(cover, (await _art.GetAsync(_albumId))?.Path);
    }

    [Fact]
    public void Clearing_an_untouched_cache_is_not_an_error()
    {
        // The admin can press the button on a server that has never served a cover.
        Assert.Equal(0, _art.Clear());
    }
}
