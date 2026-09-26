using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Library;

namespace Mootify.Tests;

public sealed class LibraryCachePathsTests
{
    [Fact]
    public void Both_caches_live_under_music_storage_and_are_excluded_from_scanning()
    {
        var root = Path.Combine(Path.GetTempPath(), "mootify-cache-path-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new LibraryOptions { MusicRoot = root };
            var art = LibraryCachePaths.Art(options);
            var normalized = LibraryCachePaths.Normalized(options);
            Assert.Equal(Path.Combine(root, "Cache", "Art"), art);
            Assert.Equal(Path.Combine(root, "Cache", "Normalized"), normalized);
            Directory.CreateDirectory(art);
            Directory.CreateDirectory(normalized);
            File.WriteAllText(Path.Combine(normalized, "copy.mp3"), "cache");
            File.WriteAllText(Path.Combine(root, "song.mp3"), "original");
            var filer = new LibraryFiler(new StaticOptionsMonitor<LibraryOptions>(options), NullLogger<LibraryFiler>.Instance);
            var found = LibraryScanner.EnumerateAudioFiles(root, filer.NotLibrary).ToList();
            Assert.Equal("song.mp3", Assert.Single(found).Name);
            Assert.True(filer.IsOutsideTheLibrary(Path.Combine(normalized, "copy.mp3")));
            Assert.False(filer.IsOutsideTheLibrary(Path.Combine(root, "Cache-like-artist", "song.mp3")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-storage")]
    public void No_local_fallback_when_storage_is_unconfigured(string root)
    {
        var options = new LibraryOptions { MusicRoot = root };
        Assert.Throws<InvalidOperationException>(() => LibraryCachePaths.Art(options));
        Assert.Throws<InvalidOperationException>(() => LibraryCachePaths.Normalized(options));
    }

    [Fact]
    public void Unavailable_storage_is_not_created_on_the_webserver()
    {
        var root = Path.Combine(Path.GetTempPath(), "mootify-missing-storage", Guid.NewGuid().ToString("n"));
        Assert.Throws<IOException>(() => LibraryCachePaths.EnsureStorageAvailable(new LibraryOptions { MusicRoot = root }));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData(".")]
    public void Cache_folder_cannot_escape_music_storage(string folder)
    {
        var options = new LibraryOptions { MusicRoot = Path.GetTempPath(), CacheFolder = folder };
        Assert.Throws<InvalidOperationException>(() => LibraryCachePaths.Root(options));
    }
}
