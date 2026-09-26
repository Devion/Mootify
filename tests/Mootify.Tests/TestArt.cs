using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Library;

namespace Mootify.Tests;

/// <summary>An art cache on isolated temporary library storage.</summary>
internal static class TestArt
{
    public static AlbumArtService Service(TestDatabase db, out string cacheDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var options = new LibraryOptions { MusicRoot = root };
        cacheDirectory = LibraryCachePaths.Art(options);

        return new AlbumArtService(
            db,
            new StaticOptionsMonitor<LibraryOptions>(options),
            NullLogger<AlbumArtService>.Instance);
    }

    /// <summary>For the callers that only need the scanner to have one.</summary>
    public static AlbumArtService Service(TestDatabase db) => Service(db, out _);
}
