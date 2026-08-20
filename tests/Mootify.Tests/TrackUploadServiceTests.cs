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

namespace Mootify.Tests;

/// <summary>
/// Uploading music from the browser.
///
/// The rules that matter are the ones that decide whether a file reaches the library at all, and
/// where: an upload must carry an artist and an album, the folders for them are made on demand,
/// nothing is ever overwritten, and <b>nothing about the destination comes from the file name</b>
/// — which is a string a browser sent us.
///
/// Real files on a real temp folder, because that is the only way "the folder didn't exist and now
/// it does" is a testable claim.
/// </summary>
public sealed class TrackUploadServiceTests : IAsyncLifetime
{
    private string _root = null!;
    private TestDatabase _db = null!;
    private TrackUploadService _service = null!;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        _db = new TestDatabase();
        _service = CreateService(new LibraryOptions { MusicRoot = _root });

        return Task.CompletedTask;
    }

    /// <summary>
    /// The real graph, not a stub of it. The scan and the request match at the end of an upload
    /// are the two steps that make a filed file actually visible, and both are resolved out of a
    /// scope the same way the app resolves them — a fake here would test the parts and not the
    /// sequence, which is where this feature can go wrong.
    /// </summary>
    private TrackUploadService CreateService(LibraryOptions options)
    {
        var monitor = new StaticOptionsMonitor<LibraryOptions>(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<MootifyDbContext>>(_db);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MootifyDbContext>>().CreateDbContext());
        services.AddSingleton<NotificationDispatcher>();
        services.AddScoped<PlaylistService>();
        services.AddScoped<TeamService>();
        services.AddScoped<RequestFulfiller>();
        services.AddScoped<ImportRequestMatcher>();

        var provider = services.BuildServiceProvider();

        var scanner = new LibraryScanner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new LibraryFiler(monitor, NullLogger<LibraryFiler>.Instance),
            // No credentials configured, so this is a no-op that reports "nothing to connect".
            new NetworkShareConnector(monitor, NullLogger<NetworkShareConnector>.Instance),
            monitor,
            NullLogger<LibraryScanner>.Instance);

        var notifications = provider.GetRequiredService<NotificationDispatcher>();
        var playlists = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);
        var teams = new TeamService(_db, notifications, NullLogger<TeamService>.Instance);

        var matcher = new ImportRequestMatcher(
            _db,
            new RequestFulfiller(_db, playlists, teams, notifications, NullLogger<RequestFulfiller>.Instance),
            NullLogger<ImportRequestMatcher>.Instance);

        return new TrackUploadService(scanner, matcher, monitor, NullLogger<TrackUploadService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A tagged MP3 as an upload would arrive: a name and a stream, nothing else.</summary>
    private static TrackUploadService.Incoming File(
        string fileName, string? artist = null, string? album = null, string? title = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n") + ".mp3");
        TestAudio.Write(temp, artist, album, title);

        var bytes = System.IO.File.ReadAllBytes(temp);
        System.IO.File.Delete(temp);

        return new TrackUploadService.Incoming(fileName, new MemoryStream(bytes));
    }

    private bool Exists(string relativePath) => System.IO.File.Exists(Path.Combine(_root, relativePath));

    private Task<UploadReport> UploadAsync(params TrackUploadService.Incoming[] files) =>
        _service.UploadAsync(Guid.NewGuid(), files);

    // ---- the happy path --------------------------------------------------

    [Fact]
    public async Task A_tagged_file_lands_under_artist_and_album()
    {
        var report = await UploadAsync(File("01 Intro.mp3", "The Cowbells", "Pasture Sounds"));

        var file = Assert.Single(report.Files);
        Assert.Equal(UploadOutcome.Filed, file.Outcome);
        Assert.True(Exists(Path.Combine("The Cowbells", "Pasture Sounds", "01 Intro.mp3")));
    }

    [Fact]
    public async Task The_folders_are_created_when_they_do_not_exist()
    {
        // This is the whole of "if the dir doesn't exist, make it" — the artist and the album are
        // both new, so two levels of folder have to appear.
        Assert.False(Directory.Exists(Path.Combine(_root, "Brand New Band")));

        await UploadAsync(File("song.mp3", "Brand New Band", "First Record"));

        Assert.True(Directory.Exists(Path.Combine(_root, "Brand New Band", "First Record")));
    }

    [Fact]
    public async Task An_uploaded_file_becomes_a_playable_track()
    {
        // Filing is only half of it: a file on disk that no scan picked up is invisible, so the
        // targeted rescan is part of the operation rather than something that happens later.
        await UploadAsync(File("song.mp3", "The Cowbells", "Pasture Sounds", "Moo Anthem"));

        await using var db = _db.CreateDbContext();
        var track = Assert.Single(await db.Tracks.Include(t => t.Artist).Include(t => t.Album).ToListAsync());

        Assert.Equal("Moo Anthem", track.Title);
        Assert.Equal("The Cowbells", track.Artist!.Name);
        Assert.Equal("Pasture Sounds", track.Album!.Title);
        Assert.True(track.IsPresent);
    }

    [Fact]
    public async Task A_whole_album_goes_up_in_one_batch()
    {
        var report = await UploadAsync(
            File("01.mp3", "The Cowbells", "Pasture Sounds", "One"),
            File("02.mp3", "The Cowbells", "Pasture Sounds", "Two"),
            File("03.mp3", "The Cowbells", "Pasture Sounds", "Three"));

        Assert.Equal(3, report.Filed);
        Assert.Equal(0, report.Rejected);

        await using var db = _db.CreateDbContext();
        Assert.Equal(3, await db.Tracks.CountAsync());
        // One folder written into means one rescan, not three.
        Assert.Equal(3, report.Scan.Added);
    }

    [Fact]
    public async Task Two_artists_in_one_batch_both_land()
    {
        var report = await UploadAsync(
            File("a.mp3", "The Cowbells", "Pasture Sounds", "One"),
            File("b.mp3", "Other Band", "Other Record", "Two"));

        Assert.Equal(2, report.Filed);
        Assert.True(Exists(Path.Combine("The Cowbells", "Pasture Sounds", "a.mp3")));
        Assert.True(Exists(Path.Combine("Other Band", "Other Record", "b.mp3")));
    }

    // ---- the tag rule ----------------------------------------------------

    [Fact]
    public async Task No_artist_tag_is_refused_and_nothing_is_written()
    {
        // The drop folder would put this in the catch-all folder, which is right for a mailbox
        // somebody works through and wrong here: the person is standing in front of the machine
        // and can fix the tag.
        var report = await UploadAsync(File("mystery.mp3", album: "Pasture Sounds"));

        var file = Assert.Single(report.Files);
        Assert.Equal(UploadOutcome.MissingTags, file.Outcome);
        Assert.Contains("artist", file.Detail);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task No_album_tag_is_refused()
    {
        var report = await UploadAsync(File("mystery.mp3", artist: "The Cowbells"));

        var file = Assert.Single(report.Files);
        Assert.Equal(UploadOutcome.MissingTags, file.Outcome);
        Assert.Contains("album", file.Detail);
    }

    [Fact]
    public async Task Neither_tag_says_so_once()
    {
        var report = await UploadAsync(File("mystery.mp3"));

        var file = Assert.Single(report.Files);
        Assert.Equal(UploadOutcome.MissingTags, file.Outcome);
        Assert.Contains("artist or album", file.Detail);
    }

    [Fact]
    public async Task Unknown_artist_counts_as_no_artist()
    {
        // Same rule LibraryFiler.SafeFolder applies, and it is the same call — a file tagged
        // literally "Unknown Artist" has not been tagged.
        var report = await UploadAsync(File("x.mp3", "Unknown Artist", "Unknown Album"));

        Assert.Equal(UploadOutcome.MissingTags, Assert.Single(report.Files).Outcome);
    }

    [Fact]
    public async Task A_rejection_shows_what_the_file_claims_to_be()
    {
        // So somebody can see the album is there and the artist isn't, without opening a tagger.
        var report = await UploadAsync(File("x.mp3", album: "Pasture Sounds"));

        var file = Assert.Single(report.Files);
        Assert.Null(file.Artist);
        Assert.Equal("Pasture Sounds", file.Album);
    }

    // ---- what may be uploaded --------------------------------------------

    [Fact]
    public async Task A_format_the_library_cannot_index_is_refused()
    {
        var report = await UploadAsync(
            new TrackUploadService.Incoming("holiday.mp4", new MemoryStream([1, 2, 3])));

        var file = Assert.Single(report.Files);
        Assert.Equal(UploadOutcome.WrongFormat, file.Outcome);
        Assert.Contains("import folder", file.Detail);
    }

    [Fact]
    public async Task Something_that_is_not_audio_is_refused_rather_than_filed()
    {
        var report = await UploadAsync(
            new TrackUploadService.Incoming("fake.mp3", new MemoryStream(new byte[512])));

        Assert.Equal(UploadOutcome.Failed, Assert.Single(report.Files).Outcome);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task An_oversized_file_is_refused_and_leaves_nothing_behind()
    {
        // 1_000_000 is the clamped floor for MaxUploadBytes, so two megabytes is over it. The
        // limit is counted against bytes that have actually arrived, not a declared length.
        var service = CreateService(new LibraryOptions { MusicRoot = _root, MaxUploadBytes = 1_000_000 });

        var big = new byte[2_000_000];
        big[0] = 0xFF;

        var report = await service.UploadAsync(
            Guid.NewGuid(), [new TrackUploadService.Incoming("huge.mp3", new MemoryStream(big))]);

        Assert.Equal(UploadOutcome.TooBig, Assert.Single(report.Files).Outcome);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    // ---- names are not trusted -------------------------------------------

    [Fact]
    public async Task A_file_name_cannot_walk_out_of_the_music_root()
    {
        // The tags decide the folder; the name only ever names the file, and it goes through
        // LibraryFiler.SafeName before it becomes part of a path.
        var report = await UploadAsync(
            File(Path.Combine("..", "..", "escaped.mp3"), "The Cowbells", "Pasture Sounds"));

        Assert.Equal(UploadOutcome.Filed, Assert.Single(report.Files).Outcome);

        var landed = Path.GetFullPath(Path.Combine(_root, report.Files[0].RelativePath!));
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, landed);
    }

    [Fact]
    public async Task Tags_that_would_make_an_illegal_folder_are_sanitised_not_refused()
    {
        var report = await UploadAsync(File("x.mp3", "AC/DC", "Back: In Black"));

        Assert.Equal(UploadOutcome.Filed, Assert.Single(report.Files).Outcome);

        // Reserved characters become underscores rather than the file being turned away — the
        // rule is LibraryFiler's, so the drop folder and an upload put the band in one place.
        Assert.False(Directory.Exists(Path.Combine(_root, "AC")));
        Assert.True(Exists(Path.Combine("AC_DC", "Back_ In Black", "x.mp3")));
    }

    // ---- never overwrite --------------------------------------------------

    [Fact]
    public async Task Uploading_the_same_name_twice_keeps_both()
    {
        // Two files can legitimately both be "01 - Intro.mp3". Replacing one with the other is
        // destroying music in order to tidy up.
        await UploadAsync(File("01 Intro.mp3", "The Cowbells", "Pasture Sounds", "First"));
        await UploadAsync(File("01 Intro.mp3", "The Cowbells", "Pasture Sounds", "Second"));

        Assert.True(Exists(Path.Combine("The Cowbells", "Pasture Sounds", "01 Intro.mp3")));
        Assert.True(Exists(Path.Combine("The Cowbells", "Pasture Sounds", "01 Intro (2).mp3")));
    }

    // ---- requests ---------------------------------------------------------

    [Fact]
    public async Task An_upload_completes_the_request_that_asked_for_it()
    {
        // Somebody who gave up on Lidarr and fetched the file themselves has answered their own
        // request, and it should close the same way a Lidarr import would.
        var user = await _db.AddUserAsync("devion");
        var playlistId = await new PlaylistService(_db, NullLogger<PlaylistService>.Instance)
            .CreateAsync(user.Id, "Wanted");

        await using (var db = _db.CreateDbContext())
        {
            db.Requests.Add(new Request
            {
                Id = Guid.NewGuid(),
                RequesterId = user.Id,
                Kind = RequestKind.Track,
                Status = RequestStatus.NotFound,
                Query = "Moo Anthem",
                ArtistName = "The Cowbells",
                AlbumTitle = "Pasture Sounds",
                TrackTitle = "Moo Anthem",
                TargetPlaylistId = playlistId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var report = await UploadAsync(File("moo.mp3", "The Cowbells", "Pasture Sounds", "Moo Anthem"));

        Assert.Equal(1, report.Matched.Requests);

        await using var check = _db.CreateDbContext();
        Assert.Equal(RequestStatus.Available, (await check.Requests.FirstAsync()).Status);
        Assert.Single(await check.PlaylistItems.Where(i => i.PlaylistId == playlistId).ToListAsync());
    }

    // ---- reporting --------------------------------------------------------

    [Fact]
    public async Task A_mixed_batch_reports_every_file_by_name()
    {
        // Never a silent drop: a batch that files three of five and says "done" is the answer
        // that costs somebody an afternoon looking for the other two.
        var report = await UploadAsync(
            File("good.mp3", "The Cowbells", "Pasture Sounds"),
            File("untagged.mp3"),
            new TrackUploadService.Incoming("video.mp4", new MemoryStream([1, 2, 3])));

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(1, report.Filed);
        Assert.Equal(2, report.Rejected);
        Assert.Equal(["good.mp3", "untagged.mp3", "video.mp4"], report.Files.Select(f => f.FileName).Order());
    }

    [Fact]
    public async Task An_empty_batch_does_nothing()
    {
        var report = await _service.UploadAsync(Guid.NewGuid(), []);

        Assert.Empty(report.Files);
        Assert.Equal(0, report.Scan.Added);
    }

    [Fact]
    public async Task The_relative_path_is_reported_and_the_absolute_one_is_not()
    {
        // Where it went is useful; the server's disk layout is nobody's business.
        var report = await UploadAsync(File("song.mp3", "The Cowbells", "Pasture Sounds"));

        var path = Assert.Single(report.Files).RelativePath!;
        Assert.False(Path.IsPathRooted(path));
        Assert.DoesNotContain(_root, path, StringComparison.OrdinalIgnoreCase);
    }
}
