using System.Collections.Concurrent;
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
///
/// <b>The answer is memoized, and that is the layer that actually makes this cheap.</b> The disk
/// cache above only ever spared the <i>extraction</i>: every call still opened a
/// <c>DbContext</c> to find a track, and then swept the album's folder for six names in four
/// extensions before it looked at the cache at all. On a network share that is up to 24 SMB
/// stats per cover, and the library grid asks for fifty at once. <see cref="_resolved"/> holds
/// what the sweep concluded — <i>including</i> that there is nothing — so the second ask is a
/// dictionary lookup. It is bounded by the number of albums, because that is what the keys are.
///
/// Two things therefore have to invalidate it, and both did not exist before it did:
/// <see cref="Forget(IReadOnlyCollection{Guid})"/>, which the scanner calls with the albums a
/// pass touched, and <see cref="Clear"/>, which is the admin's hammer for a library that was
/// re-tagged underneath us. Both drop the disk entries too — a <c>.none</c> marker left behind
/// would keep answering "no art" for an album that has just gained some.
/// </summary>
public sealed class AlbumArtService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    IOptionsMonitor<ApiOptions> options,
    ILogger<AlbumArtService> log)
{
    /// <summary>One extraction at a time — the same reasoning as <c>TranscodeCache.Gate</c>.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// What the last look concluded, per album, null included. Not size-limited on purpose: the
    /// keys are album ids out of our own database, so this is bounded by the library rather than
    /// by whatever a caller asks for — unlike <c>LoginThrottle</c>, where the keys come from
    /// outside and the cap is the whole defence.
    ///
    /// Two requests for the same cold album both resolve; the extraction below is gated anyway
    /// and the loser finds the file the winner wrote, so a per-key lock would cost more than the
    /// duplicate it prevents.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, AlbumArt?> _resolved = new();

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
        if (_resolved.TryGetValue(albumId, out var known)) return known;

        var found = await ResolveAsync(albumId, ct);

        // Cached after the await rather than around it, so a cancelled request leaves no
        // half-formed answer behind for the next one to trust.
        _resolved[albumId] = found;
        return found;
    }

    /// <summary>
    /// Forget one album, so the next request goes and looks again. The endpoint calls this when
    /// the path it was handed no longer exists — a cover deleted under us is otherwise an answer
    /// that 404s for ever, since the memo would keep handing back the same dead path.
    /// </summary>
    public void Forget(Guid albumId)
    {
        _resolved.TryRemove(albumId, out _);
        DeleteCached(albumId);
    }

    /// <summary>
    /// Forget the albums a scan touched. Precise rather than wholesale on purpose: the watcher
    /// fires on every file dropped into the import folder, and re-extracting every embedded
    /// cover in the library because one album gained a track is a tag parse per album, over the
    /// share, for nothing. Returns how many were actually holding an answer.
    /// </summary>
    public int Forget(IReadOnlyCollection<Guid> albumIds)
    {
        var dropped = 0;

        foreach (var albumId in albumIds)
        {
            if (_resolved.TryRemove(albumId, out _)) dropped++;
            DeleteCached(albumId);
        }

        return dropped;
    }

    private async Task<AlbumArt?> ResolveAsync(Guid albumId, CancellationToken ct)
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
    /// Everything, for a library that was re-tagged or re-covered underneath us. This is the
    /// admin's hammer; the scan uses <see cref="Forget(IReadOnlyCollection{Guid})"/>, which knows
    /// which albums it changed.
    ///
    /// The memo is emptied last rather than first, so anything a request repopulates while the
    /// files are being deleted goes with it.
    /// </summary>
    public int Clear()
    {
        var removed = 0;

        if (Directory.Exists(CacheDirectory))
        {
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
        }

        _resolved.Clear();
        return removed;
    }

    /// <summary>
    /// The album's extracted cover and its "nothing here" marker. Deleting the marker is the
    /// half that matters: it is what stands between an album that has just gained a cover and
    /// anybody seeing it.
    /// </summary>
    private void DeleteCached(Guid albumId)
    {
        if (!Directory.Exists(CacheDirectory)) return;

        foreach (var extension in AdjacentExtensions.Append(MissExtension))
        {
            var candidate = Path.Combine(CacheDirectory, $"{albumId:n}{extension}");

            try
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
            catch (Exception ex)
            {
                // A cover we can't delete is a stale picture, not a broken library.
                log.LogWarning(ex, "Could not delete {File}", candidate);
            }
        }
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
