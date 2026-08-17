using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Library;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Requests;
using Mootify.Services.Teams;
using Mootify.Services.Transcoding;

namespace Mootify.Tests;

public sealed class LibraryScannerTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private string _root = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _root = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private LibraryScanner CreateScanner(LibraryOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<MootifyDbContext>>(_db);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MootifyDbContext>>().CreateDbContext());

        // The scanner resolves the matcher out of this at the end of a full scan, so it has to
        // be the real graph — the whole point of the end-to-end test below is the wiring.
        services.AddSingleton<NotificationDispatcher>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<TeamService>();
        services.AddScoped<RequestFulfiller>();
        services.AddScoped<ImportRequestMatcher>();

        var monitor = new StaticOptionsMonitor<LibraryOptions>(
            options ?? new LibraryOptions { MusicRoot = _root });

        return new LibraryScanner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new LibraryFiler(monitor, NullLogger<LibraryFiler>.Instance),
            // No credentials configured, so this is a no-op that reports "nothing to connect".
            new NetworkShareConnector(monitor, NullLogger<NetworkShareConnector>.Instance),
            monitor,
            NullLogger<LibraryScanner>.Instance);
    }

    private void WriteFile(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Content doesn't matter — TagLib will fail to parse it and the scanner falls back
        // to the filename, which is itself the behaviour under test.
        File.WriteAllBytes(full, new byte[512]);
    }

    [Fact]
    public async Task Mp3_and_flac_both_become_tracks()
    {
        // FLAC is what Lidarr actually fetches and every current browser decodes it, so
        // indexing MP3 only left most of the library invisible. Browsers that can't cope
        // get a transcoded copy from /media/{id}/mp3 instead of losing the original.
        WriteFile(@"Cowbells\Album\01 - Morning Graze.mp3");
        WriteFile(@"Cowbells\Album\02 - Lossless Lament.flac");
        WriteFile(@"Cowbells\Album\cover.jpg");
        WriteFile(@"Cowbells\Album\03 - Old Format.wma");

        var report = await CreateScanner().ScanAllAsync();

        Assert.Equal(2, report.Added);

        await using var db = _db.CreateDbContext();
        var titles = await db.Tracks.Select(t => t.Title).OrderBy(t => t).ToListAsync();
        Assert.Equal(["01 - Morning Graze", "02 - Lossless Lament"], titles);
    }

    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("song.MP3", true)]
    [InlineData("song.flac", true)]
    [InlineData("song.FLAC", true)]
    // Still needs converting — no browser plays these.
    [InlineData("song.wma", false)]
    [InlineData("song.ape", false)]
    [InlineData("cover.jpg", false)]
    public void The_indexable_formats_are_the_ones_browsers_play(string fileName, bool expected)
    {
        Assert.Equal(expected, LibraryScanner.IsIndexable(fileName));
    }

    [Fact]
    public async Task Untagged_files_take_their_album_from_the_folder()
    {
        // These files have no readable tags, so the scanner falls back on the layout.
        // Without this every untagged track collapses into one "Unknown Album" per artist.
        WriteFile(@"DevionNL\Bored\Chip on the Dancefloor.mp3");
        WriteFile(@"DevionNL\Bored\Celebrations.mp3");
        WriteFile(@"DevionNL\Other Record\Time.mp3");

        await CreateScanner().ScanAllAsync();

        await using var db = _db.CreateDbContext();
        var albums = await db.Albums.OrderBy(a => a.Title).Select(a => a.Title).ToListAsync();

        Assert.Equal(["Bored", "Other Record"], albums);
    }

    [Fact]
    public async Task A_file_loose_in_the_music_root_gets_no_invented_album()
    {
        WriteFile("stray.mp3");

        await CreateScanner().ScanAllAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal("Unknown Album", (await db.Albums.SingleAsync()).Title);
    }

    [Fact]
    public async Task A_folder_named_after_the_artist_is_not_treated_as_an_album()
    {
        // Artist/track.mp3 — the folder name would just repeat the artist, which is
        // worse than admitting we don't know.
        WriteFile(@"Unknown Artist\loose.mp3");

        await CreateScanner().ScanAllAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal("Unknown Album", (await db.Albums.SingleAsync()).Title);
    }

    [Fact]
    public async Task Rescanning_unchanged_files_changes_nothing()
    {
        WriteFile(@"Cowbells\Album\01 - Morning Graze.mp3");
        var scanner = CreateScanner();

        var first = await scanner.ScanAllAsync();
        var second = await scanner.ScanAllAsync();

        Assert.Equal(1, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task A_deleted_file_is_marked_absent_rather_than_removed()
    {
        // Deleting the row would cascade and silently empty somebody's playlist.
        WriteFile(@"Cowbells\Album\01 - Morning Graze.mp3");
        var scanner = CreateScanner();
        await scanner.ScanAllAsync();

        File.Delete(Path.Combine(_root, @"Cowbells\Album\01 - Morning Graze.mp3"));
        var report = await scanner.ScanAllAsync();

        Assert.Equal(1, report.Removed);

        await using var db = _db.CreateDbContext();
        var track = await db.Tracks.SingleAsync();
        Assert.False(track.IsPresent);
    }

    [Fact]
    public async Task Missing_music_root_is_survivable()
    {
        var scanner = CreateScanner();
        var report = await scanner.ScanPathAsync(Path.Combine(_root, "nope"));

        Assert.Equal(ScanReport.Empty, report);
    }

    // ---- the drop folder -------------------------------------------------

    [Fact]
    public async Task The_drop_folder_is_filed_and_then_indexed_where_it_landed()
    {
        // Filing before indexing is what makes this one row rather than two: indexed where it
        // landed and again where it went, with the first going absent on the following pass
        // and taking any playlist entry made in between with it.
        TestAudio.Write(Path.Combine(_root, "import", "whatever.mp3"), artist: "Cowbells", album: "Bored");

        var report = await CreateScanner().ScanAllAsync();

        Assert.Equal(1, report.Added);
        Assert.True(File.Exists(Path.Combine(_root, "Cowbells", "Bored", "whatever.mp3")));

        await using var db = _db.CreateDbContext();
        var track = await db.Tracks.SingleAsync();
        Assert.Equal(Path.Combine(_root, "Cowbells", "Bored", "whatever.mp3"), track.Path);
    }

    [Fact]
    public async Task Whatever_is_left_in_the_drop_folder_is_not_indexed()
    {
        // A file still being copied has a plausible size and unreadable tags, which is exactly
        // the shape of a Track row nobody wants. It gets indexed once it has been filed.
        var stuck = Path.Combine(_root, "import", "copying.mp3");
        TestAudio.Write(stuck, artist: "Cowbells");
        using var holding = File.Open(stuck, FileMode.Open, FileAccess.Write, FileShare.Read);

        var report = await CreateScanner().ScanAllAsync();

        Assert.Equal(0, report.Added);
    }

    [Fact]
    public async Task A_track_already_indexed_in_the_drop_folder_keeps_its_row_through_the_move()
    {
        // The drop folder predates the filer, so its files have been indexed like anywhere else
        // and are sitting in people's playlists. Absent-plus-new would empty those playlists on
        // the first scan after the upgrade; following the row keeps its id and everything hung
        // off it.
        var source = Path.Combine(_root, "import", "whatever.mp3");
        TestAudio.Write(source, artist: "Cowbells", album: "Bored");

        // A scanner that has never heard of a drop folder, which is the state being upgraded from.
        await CreateScanner(new LibraryOptions { MusicRoot = _root, ImportFolder = "" }).ScanAllAsync();

        Guid before;
        await using (var seeded = _db.CreateDbContext())
        {
            before = (await seeded.Tracks.SingleAsync()).Id;
        }

        await CreateScanner().ScanAllAsync();

        await using var db = _db.CreateDbContext();
        var track = await db.Tracks.SingleAsync();

        Assert.Equal(before, track.Id);
        Assert.Equal(Path.Combine(_root, "Cowbells", "Bored", "whatever.mp3"), track.Path);
        Assert.True(track.IsPresent);
    }

    [Fact]
    public async Task A_dropped_file_closes_the_request_that_asked_for_it()
    {
        // End to end: file it, index it, match it, append it. The matching itself is covered in
        // ImportRequestMatcherTests; what this asserts is that a full scan actually gets there.
        var user = await _db.AddUserAsync("devion");

        await using (var seed = _db.CreateDbContext())
        {
            seed.Requests.Add(new Request
            {
                Id = Guid.NewGuid(),
                RequesterId = user.Id,
                Kind = RequestKind.Track,
                Status = RequestStatus.NotFound,
                Query = "Morning Graze",
                ArtistName = "Cowbells",
                TrackTitle = "Morning Graze",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await seed.SaveChangesAsync();
        }

        TestAudio.Write(
            Path.Combine(_root, "import", "01 - Morning Graze.mp3"),
            artist: "Cowbells", album: "Bored", title: "Morning Graze");

        await CreateScanner().ScanAllAsync();

        await using var db = _db.CreateDbContext();
        Assert.Equal(RequestStatus.Available, (await db.Requests.SingleAsync()).Status);
    }
}
