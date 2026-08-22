using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Library;

namespace Mootify.Tests;

/// <summary>
/// An <see cref="AlbumArtService"/> whose cache is its own temp folder.
///
/// The default is <c>data/art-cache</c> relative to the working directory, which every test in
/// the run would share — and this service both writes marker files and deletes them wholesale,
/// so one test's <c>Clear()</c> would be another's missing cover. It is deliberately not put
/// under the music root either: the scanner walks that, and an extracted cover sitting in it
/// would be indexed as though somebody had filed it there.
/// </summary>
internal static class TestArt
{
    public static AlbumArtService Service(TestDatabase db, out string cacheDirectory)
    {
        cacheDirectory = Path.Combine(Path.GetTempPath(), "mootify-tests", Guid.NewGuid().ToString("n"));

        return new AlbumArtService(
            db,
            new StaticOptionsMonitor<ApiOptions>(new ApiOptions { ArtCacheDirectory = cacheDirectory }),
            NullLogger<AlbumArtService>.Instance);
    }

    /// <summary>For the callers that only need the scanner to have one.</summary>
    public static AlbumArtService Service(TestDatabase db) => Service(db, out _);
}
