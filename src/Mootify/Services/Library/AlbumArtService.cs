using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;

namespace Mootify.Services.Library;

public sealed record AlbumArt(string Path, string ContentType);

/// <summary>
/// Cover art for an album, found rather than stored.
///
/// The scanner doesn't record art (<see cref="Album.CoverPath"/> is never written), and it
/// doesn't need to: art lives either next to the files as <c>cover.jpg</c> or inside the MP3's
/// ID3 tag, and both are cheap to find on demand. Two rules follow from that:
///
/// - An adjacent image is served straight off disk. It's already a file; copying it into a
///   cache would only give us a second copy to invalidate.
/// - Embedded art is extracted once into the cache directory, because pulling it out means
///   parsing the tag of a file that may be on a network share — fine once per album, not fine
///   once per row of a list a car is scrolling.
///
/// Albums with no art at all get an empty marker file, so the miss is as cheap as the hit. A
/// car browsing a library of 800 albums asks for 800 covers, and most libraries answer "no" for
/// a good share of them.
/// </summary>
public sealed class AlbumArtService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    IOptionsMonitor<ApiOptions> options,
    ILogger<AlbumArtService> log)
{
    /// <summary>One extraction at a time — the same reasoning as <c>TranscodeCache.Gate</c>.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// What a folder of music calls its cover, in the order worth trying. Case-insensitive:
    /// these come off Windows shares and Linux volumes alike.
    /// </summary>
    private static readonly string[] AdjacentNames =
    [
        "cover", "folder", "front", "album", "albumart", "thumb",
    ];

    private static readonly string[] AdjacentExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>Marker for "we looked, there's nothing". Zero bytes, so the check is a stat call.</summary>
    private const string MissExtension = ".none";

    public string CacheDirectory
    {
        get
        {
            var configured = options.CurrentValue.ArtCacheDirectory;
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine("data", "art-cache")
                : configured;

            // Absolute, for the same reason the transcode cache is: Results.File resolves a
            // relative path against wwwroot rather than the working directory.
            return Path.GetFullPath(path);
        }
    }

    public async Task<AlbumArt?> GetAsync(Guid albumId, CancellationToken ct = default)
    {
        // Any present track will do — art belongs to the folder, and the whole album lives in one.
        var trackPath = await FirstTrackPathAsync(albumId, ct);
        if (trackPath is null) return null;

        if (FindAdjacent(trackPath) is { } adjacent)
        {
            return new AlbumArt(adjacent, ContentTypeFor(adjacent));
        }

        return await ExtractEmbeddedAsync(albumId, trackPath, ct);
    }

    private async Task<string?> FirstTrackPathAsync(Guid albumId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Tracks
            .AsNoTracking()
            .Where(t => t.AlbumId == albumId && t.IsPresent)
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber)
            .Select(t => t.Path)
            .FirstOrDefaultAsync(ct);
    }

    private static string? FindAdjacent(string trackPath)
    {
        var folder = Path.GetDirectoryName(trackPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

        foreach (var name in AdjacentNames)
        {
            foreach (var extension in AdjacentExtensions)
            {
                var candidate = Path.Combine(folder, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private async Task<AlbumArt?> ExtractEmbeddedAsync(Guid albumId, string trackPath, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDirectory);

        if (FindCached(albumId) is { } hit) return hit;
        if (File.Exists(MissPath(albumId))) return null;

        await Gate.WaitAsync(ct);
        try
        {
            // Somebody else may have done the work while we queued.
            if (FindCached(albumId) is { } raced) return raced;
            if (File.Exists(MissPath(albumId))) return null;

            var picture = ReadPicture(trackPath);

            if (picture is null)
            {
                await File.WriteAllBytesAsync(MissPath(albumId), [], ct);
                return null;
            }

            var (bytes, contentType) = picture.Value;
            var target = Path.Combine(CacheDirectory, $"{albumId:n}{ExtensionFor(contentType)}");

            // Write then move: a half-written cover would otherwise be cached as the answer.
            var temporary = target + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            File.Move(temporary, target, overwrite: true);

            return new AlbumArt(target, contentType);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Reads the first embedded picture. Wrapped broadly on purpose: TagLib throws a whole
    /// family of exceptions on files that play perfectly well, and a corrupt tag is a reason to
    /// show no art, never a reason to fail the request.
    /// </summary>
    private (byte[] Bytes, string ContentType)? ReadPicture(string trackPath)
    {
        try
        {
            using var file = TagLib.File.Create(trackPath);
            var picture = file.Tag.Pictures.FirstOrDefault(p => p.Data?.Count > 0);
            if (picture is null) return null;

            var mime = string.IsNullOrWhiteSpace(picture.MimeType) ? "image/jpeg" : picture.MimeType;
            return (picture.Data.Data, mime);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "No readable art in {File}", trackPath);
            return null;
        }
    }

    private AlbumArt? FindCached(Guid albumId)
    {
        foreach (var extension in AdjacentExtensions)
        {
            var candidate = Path.Combine(CacheDirectory, $"{albumId:n}{extension}");
            if (File.Exists(candidate)) return new AlbumArt(candidate, ContentTypeFor(candidate));
        }

        return null;
    }

    private string MissPath(Guid albumId) => Path.Combine(CacheDirectory, $"{albumId:n}{MissExtension}");

    /// <summary>
    /// Dropped wholesale after a rescan replaces files, and by the admin panel. Misses are
    /// cached too, so a library that gains art has to be told to look again.
    /// </summary>
    public int Clear()
    {
        if (!Directory.Exists(CacheDirectory)) return 0;

        var removed = 0;
        foreach (var file in Directory.GetFiles(CacheDirectory))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not delete {File}", file);
            }
        }

        return removed;
    }

    internal static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    internal static string ExtensionFor(string contentType) =>
        contentType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg",
        };
}
