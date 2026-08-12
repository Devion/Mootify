using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Library;
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

    private LibraryScanner CreateScanner()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MootifyDbContext>>(_db);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MootifyDbContext>>().CreateDbContext());

        return new LibraryScanner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<LibraryOptions>(new LibraryOptions { MusicRoot = _root }),
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

}
