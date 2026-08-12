using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Transcoding;

/// <summary>
/// On-demand MP3s for browsers that can't play the original.
///
/// The library stores FLAC as it arrived; this is the escape hatch for a client that says it
/// can't decode it. Converted files are kept in a cache directory beside the database, keyed
/// by track, so the wait happens once per track rather than once per play.
///
/// A cached file on disk — rather than piping FFmpeg straight to the response — is what makes
/// range requests work, and without those seeking silently breaks and Safari won't play at all.
/// </summary>
public sealed class TranscodeCache(
    Transcoder transcoder,
    IOptionsMonitor<TranscodeOptions> options,
    ILogger<TranscodeCache> log)
{
    /// <summary>One conversion per track at a time; ten tabs opening the same song shouldn't
    /// start ten FFmpeg processes writing to one path.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Always absolute. Results.File resolves a relative path against the <i>web root</i>
    /// (wwwroot), not the working directory, so a relative cache path yields a confident
    /// FileNotFoundException pointing at a file that is plainly sitting right there.
    /// </summary>
    public string CacheDirectory
    {
        get
        {
            var configured = options.CurrentValue.CacheDirectory;
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine("data", "transcode-cache")
                : configured;

            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Path to a playable MP3 of this track, converting first if needed. Null when the source
    /// is gone or FFmpeg can't manage it.
    /// </summary>
    public async Task<string?> GetOrCreateAsync(Guid trackId, string sourcePath, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath)) return null;

        // Already an MP3 — nothing to do, serve the original.
        if (Path.GetExtension(sourcePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        Directory.CreateDirectory(CacheDirectory);
        var cached = Path.Combine(CacheDirectory, $"{trackId:n}.mp3");

        // Re-convert if the source has changed since we cached it.
        if (File.Exists(cached) && File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(sourcePath))
        {
            return cached;
        }

        await Gate.WaitAsync(ct);
        try
        {
            // Somebody else may have finished it while we queued.
            if (File.Exists(cached) && File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(sourcePath))
            {
                return cached;
            }

            log.LogInformation("Transcoding {Source} for a client that can't play it", Path.GetFileName(sourcePath));

            return await transcoder.TranscodeToAsync(sourcePath, cached, ct) ? cached : null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Size of the cache, for the admin panel.</summary>
    public (int Files, long Bytes) Measure()
    {
        if (!Directory.Exists(CacheDirectory)) return (0, 0);

        try
        {
            var files = Directory.GetFiles(CacheDirectory, "*.mp3");
            return (files.Length, files.Sum(f => new FileInfo(f).Length));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not measure the transcode cache");
            return (0, 0);
        }
    }

    public int Clear()
    {
        if (!Directory.Exists(CacheDirectory)) return 0;

        var removed = 0;
        foreach (var file in Directory.GetFiles(CacheDirectory, "*.mp3"))
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
}
