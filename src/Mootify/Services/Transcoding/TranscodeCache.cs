using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Services.Library;

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
    IOptionsMonitor<LibraryOptions> library,
    ILogger<TranscodeCache> log)
{
    /// <summary>One conversion per track at a time; ten tabs opening the same song shouldn't
    /// start ten FFmpeg processes writing to one path.</summary>
    private static readonly Dictionary<string, (SemaphoreSlim Gate, int Users)> Gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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

    public string NormalizedDirectory => LibraryCachePaths.Normalized(library.CurrentValue);

    /// <summary>A completed, up-to-date copy, shared by playback and library preparation.</summary>
    public string? FindReady(Guid trackId, string sourcePath, bool normalize = false)
    {
        if (!File.Exists(sourcePath)) return null;
        var cached = CachePath(trackId, normalize);
        return File.Exists(cached) && new FileInfo(cached).Length > 0
            && File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(sourcePath)
                ? cached : null;
    }

    private string CachePath(Guid trackId, bool normalize) =>
        Path.Combine(normalize ? NormalizedDirectory : CacheDirectory, $"{trackId:n}{(normalize ? "-normalized-v1" : "")}.mp3");

    /// <summary>
    /// Path to a playable MP3 of this track, converting first if needed. Null when the source
    /// is gone or FFmpeg can't manage it.
    /// </summary>
    public async Task<string?> GetOrCreateAsync(Guid trackId, string sourcePath, CancellationToken ct = default, bool normalize = false)
    {
        if (!File.Exists(sourcePath)) return null;

        // Already an MP3 — nothing to do, serve the original.
        if (!normalize && Path.GetExtension(sourcePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        var cached = CachePath(trackId, normalize);
        if (normalize) LibraryCachePaths.EnsureStorageAvailable(library.CurrentValue);
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);

        // Re-convert if the source has changed since we cached it.
        if (FindReady(trackId, sourcePath, normalize) is not null)
        {
            return cached;
        }

        SemaphoreSlim gate;
        lock (Gates)
        {
            if (!Gates.TryGetValue(cached, out var entry)) entry = (new SemaphoreSlim(1, 1), 0);
            gate = entry.Gate;
            Gates[cached] = (gate, entry.Users + 1);
        }
        var acquired = false;
        try
        {
            await gate.WaitAsync(ct);
            acquired = true;
            // Somebody else may have finished it while we queued.
            if (FindReady(trackId, sourcePath, normalize) is not null)
            {
                return cached;
            }

            if (normalize)
            {
                var legacy = Path.Combine(CacheDirectory, $"{trackId:n}-normalized-v1.mp3");
                if (!string.Equals(legacy, cached, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(legacy) && new FileInfo(legacy).Length > 0
                    && File.GetLastWriteTimeUtc(legacy) >= File.GetLastWriteTimeUtc(sourcePath))
                {
                    var temporary = cached + ".partial";
                    try
                    {
                        await using (var source = File.OpenRead(legacy))
                        await using (var destination = File.Create(temporary))
                            await source.CopyToAsync(destination, ct);
                        ct.ThrowIfCancellationRequested();
                        File.Move(temporary, cached, overwrite: true);
                        try { File.Delete(legacy); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { log.LogWarning(ex, "Copied normalized audio to storage but could not remove old cache {Path}", legacy); }
                        return cached;
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }

            log.LogInformation("Preparing {Source} for playback (normalized: {Normalized})", Path.GetFileName(sourcePath), normalize);

            return await transcoder.TranscodeToAsync(sourcePath, cached, ct, normalize) ? cached : null;
        }
        finally
        {
            if (acquired) gate.Release();
            lock (Gates)
            {
                var users = Gates[cached].Users - 1;
                if (users == 0)
                {
                    Gates.Remove(cached);
                    gate.Dispose();
                }
                else Gates[cached] = (gate, users);
            }
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
