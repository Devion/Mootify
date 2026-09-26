using Mootify.Configuration;

namespace Mootify.Services.Library;

/// <summary>Art and normalized audio belong on library storage, never under the webserver's data directory.</summary>
public static class LibraryCachePaths
{
    public static string Root(LibraryOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MusicRoot) || !Path.IsPathFullyQualified(options.MusicRoot))
            throw new InvalidOperationException("Library:MusicRoot must be an absolute storage path before caches can be used.");
        if (string.IsNullOrWhiteSpace(options.CacheFolder) || Path.IsPathRooted(options.CacheFolder))
            throw new InvalidOperationException("Library:CacheFolder must name a directory inside music storage.");
        var root = Path.GetFullPath(options.MusicRoot);
        var cache = Path.GetFullPath(Path.Combine(root, options.CacheFolder));
        if (!LibraryFiler.Contains(root, cache))
            throw new InvalidOperationException("Library:CacheFolder must stay inside music storage.");
        return cache;
    }

    public static string Art(LibraryOptions options) => Path.Combine(Root(options), "Art");
    public static string Normalized(LibraryOptions options) => Path.Combine(Root(options), "Normalized");

    public static void EnsureStorageAvailable(LibraryOptions options)
    {
        _ = Root(options);
        if (!Directory.Exists(options.MusicRoot))
            throw new IOException("Music storage is unavailable. No cache will be written to the webserver.");
    }
}
