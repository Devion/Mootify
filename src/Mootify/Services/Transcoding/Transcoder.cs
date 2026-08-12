using System.Diagnostics;
using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Transcoding;

/// <summary>
/// Converts anything Lidarr imported that isn't an MP3 into one, in place.
///
/// This runs *before* the library rescan, and that ordering is the whole design: a FLAC
/// never becomes a Track row, so it can never reach a playlist or the player. The invariant
/// is enforced by sequencing rather than by checks scattered through the UI.
/// </summary>
public sealed class Transcoder(
    IOptionsMonitor<TranscodeOptions> options,
    ILogger<Transcoder> log)
{
    private static readonly string[] ConvertibleExtensions =
        [".flac", ".ogg", ".m4a", ".aac", ".wma", ".alac", ".ape", ".wv", ".wav", ".opus"];

    /// <summary>Transcoding competes with playback for CPU. A whole discography importing at
    /// once must not make the app stutter for everyone listening.</summary>
    private readonly SemaphoreSlim _gate = new(
        Math.Max(1, options.CurrentValue.MaxConcurrent),
        Math.Max(1, options.CurrentValue.MaxConcurrent));

    public bool IsAvailable { get; private set; } = true;

    public static bool NeedsTranscode(string path) =>
        ConvertibleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Verified at startup: finding out FFmpeg is missing when the first FLAC lands
    /// at 2am is worse than finding out at boot.</summary>
    public async Task<(bool Ok, string? Version)> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var (exit, stdout, _) = await RunAsync(options.CurrentValue.FfmpegPath, "-version", ct);
            IsAvailable = exit == 0;
            var version = stdout.Split('\n').FirstOrDefault()?.Trim();
            return (IsAvailable, version);
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            log.LogError(ex, "FFmpeg not found at {Path}. Non-MP3 imports cannot be converted.",
                options.CurrentValue.FfmpegPath);
            return (false, null);
        }
    }

    /// <summary>
    /// Converts every convertible file under a folder. Returns the paths it produced.
    /// </summary>
    public async Task<List<string>> TranscodeFolderAsync(string folder, CancellationToken ct = default)
    {
        var produced = new List<string>();

        if (!Directory.Exists(folder)) return produced;

        var candidates = Directory
            .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(NeedsTranscode)
            .ToList();

        if (candidates.Count == 0) return produced;

        log.LogInformation("Transcoding {Count} file(s) under {Folder}", candidates.Count, folder);

        foreach (var source in candidates)
        {
            var output = await TranscodeAsync(source, ct);
            if (output is not null)
            {
                produced.Add(output);
            }
        }

        return produced;
    }

    public async Task<string?> TranscodeAsync(string sourcePath, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var target = Path.ChangeExtension(sourcePath, ".mp3");

        if (File.Exists(target))
        {
            log.LogDebug("{Target} already exists; skipping", target);
            return target;
        }

        await _gate.WaitAsync(ct);
        try
        {
            // Write to a temp name first — a half-written .mp3 picked up by the watcher
            // would be scanned as a real track.
            var temp = target + ".partial";

            var args = $"-hide_banner -loglevel error -y -i \"{sourcePath}\" " +
                       $"-map 0:a -map_metadata 0 -id3v2_version 3 " +
                       $"-codec:a libmp3lame -b:a {opts.Bitrate} \"{temp}\"";

            var (exit, _, stderr) = await RunAsync(opts.FfmpegPath, args, ct);

            if (exit != 0 || !File.Exists(temp))
            {
                log.LogError("FFmpeg failed on {Source}: {Error}", sourcePath, stderr.Trim());
                if (File.Exists(temp)) File.Delete(temp);
                return null;
            }

            File.Move(temp, target, overwrite: true);
            log.LogInformation("Transcoded {Source} -> {Target}", Path.GetFileName(sourcePath), Path.GetFileName(target));

            if (opts.DeleteSourceAfterTranscode)
            {
                try
                {
                    File.Delete(sourcePath);
                }
                catch (Exception ex)
                {
                    // Keeping a stray FLAC is harmless — the scanner ignores it.
                    log.LogWarning(ex, "Could not delete {Source} after transcoding", sourcePath);
                }
            }

            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdout, await stderr);
    }
}
