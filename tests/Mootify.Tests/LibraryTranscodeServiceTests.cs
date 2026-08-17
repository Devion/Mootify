using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Library;
using Mootify.Services.Transcoding;

namespace Mootify.Tests;

/// <summary>
/// Which files the library sweep decides to rescue. FFmpeg itself isn't run here — the
/// selection is what silently leaves 25 GB of music invisible when it's wrong.
/// </summary>
public sealed class LibraryTranscodeServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private string _root = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _root = Path.Combine(Path.GetTempPath(), "mootify-transcode", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private LibraryTranscodeService Create()
    {
        var options = new StaticOptionsMonitor<LibraryOptions>(new LibraryOptions { MusicRoot = _root });

        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MootifyDbContext>>(_db);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MootifyDbContext>>().CreateDbContext());

        var scanner = new LibraryScanner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new LibraryFiler(options, NullLogger<LibraryFiler>.Instance),
            new NetworkShareConnector(options, NullLogger<NetworkShareConnector>.Instance),
            options,
            NullLogger<LibraryScanner>.Instance);

        var transcoder = new Transcoder(
            new StaticOptionsMonitor<TranscodeOptions>(new TranscodeOptions()),
            NullLogger<Transcoder>.Instance);

        return new LibraryTranscodeService(transcoder, scanner, options, NullLogger<LibraryTranscodeService>.Instance);
    }

    private void Write(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[64]);
    }

    [Fact]
    public void Only_formats_no_browser_plays_are_offered_for_conversion()
    {
        Write(@"Artist\Other\03.m4a");
        Write(@"Artist\Other\04.ogg");
        Write(@"Artist\Other\05.wma");

        var found = Create().FindConvertible();

        Assert.Equal(3, found.Count);
    }

    [Fact]
    public void Mp3s_flacs_and_artwork_are_left_alone()
    {
        // FLAC is indexed and played as-is; converting it would discard the quality the
        // download was chosen for.
        Write(@"Artist\Album\01.mp3");
        Write(@"Artist\Album\02.flac");
        Write(@"Artist\Album\cover.jpg");
        Write(@"Artist\Album\notes.txt");

        Assert.Empty(Create().FindConvertible());
    }

    [Fact]
    public void A_file_already_converted_is_not_offered_again()
    {
        // The original is kept by default, so without this every run would redo the lot.
        Write(@"Artist\Album\01.wma");
        Write(@"Artist\Album\01.mp3");
        Write(@"Artist\Album\02.wma");

        var remaining = Assert.Single(Create().FindConvertible());

        Assert.EndsWith("02.wma", remaining);
    }

    [Fact]
    public void A_missing_music_root_is_survivable()
    {
        Directory.Delete(_root, recursive: true);

        Assert.Empty(Create().FindConvertible());
    }

    [Fact]
    public void Progress_starts_idle()
    {
        var progress = Create().Progress;

        Assert.False(progress.IsRunning);
        Assert.Equal(0, progress.Percent);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(10, 5, 50)]
    [InlineData(10, 10, 100)]
    [InlineData(3, 1, 33)]
    public void Percent_counts_finished_files(int total, int done, int expected)
    {
        var progress = new TranscodeProgress(true, total, done, 0, null, null);

        Assert.Equal(expected, progress.Percent);
    }

    [Fact]
    public void Failures_count_towards_finished_so_the_bar_still_completes()
    {
        // Otherwise a run with a few broken files sticks at 97% forever.
        var progress = new TranscodeProgress(true, 10, 7, 3, null, null);

        Assert.Equal(100, progress.Percent);
    }
}
