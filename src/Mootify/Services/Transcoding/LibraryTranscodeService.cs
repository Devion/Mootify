using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Services.Library;

namespace Mootify.Services.Transcoding;

public sealed record TranscodeProgress(
    bool IsRunning,
    int Total,
    int Done,
    int Failed,
    string? Current,
    DateTimeOffset? FinishedAt,
    string? LastError = null)
{
    public static readonly TranscodeProgress Idle = new(false, 0, 0, 0, null, null);

    public int Percent => Total == 0 ? 0 : (int)(100.0 * (Done + Failed) / Total);
}

/// <summary>
/// Converts everything in the library the scanner can't index — see
/// <see cref="Transcoder.NeedsTranscode"/>, which is neither MP3 nor FLAC.
///
/// The request pipeline already transcodes what it fetches, but that only covers albums
/// Mootify itself asked for. Anything else — an artist added straight in Lidarr, a folder
/// copied in by hand — can land as OGG or M4A and is invisible until it's an MP3. This is
/// the sweep for all of that. FLAC is not in it: the scanner indexes FLAC natively, and a
/// browser that can't decode one gets a cached MP3 on demand instead of losing the original.
///
/// Deliberately manual. Re-encoding hundreds of albums saturates a CPU for hours, and doing
/// that unannounced to somebody's machine while they're listening is rude.
/// </summary>
public sealed class LibraryTranscodeService(
    Transcoder transcoder,
    LibraryScanner scanner,
    LibraryFiler filer,
    IOptionsMonitor<LibraryOptions> options,
    ILogger<LibraryTranscodeService> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    public TranscodeProgress Progress { get; private set; } = TranscodeProgress.Idle;

    public event Action? Changed;

    /// <summary>Files the scanner is currently ignoring that could be rescued.</summary>
    public IReadOnlyList<string> FindConvertible()
    {
        var root = options.CurrentValue.MusicRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        try
        {
            return
            [
                .. Directory
                    .EnumerateFiles(root, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.System,
                    })
                    .Where(Transcoder.NeedsTranscode)
                    // The folders that aren't the library. Only MP3 and FLAC are ever quarantined
                    // today, so nothing in there needs converting anyway — but "safe because two
                    // unrelated lists happen not to overlap" is a fact that stops being true
                    // quietly, and the sweep writes an MP3 next to whatever it finds.
                    .Where(f => !filer.IsOutsideTheLibrary(f))
                    // Already converted on a previous run — the original is kept unless
                    // Transcode:DeleteSourceAfterTranscode says otherwise.
                    .Where(f => !File.Exists(Path.ChangeExtension(f, ".mp3")))
            ];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not enumerate the library for convertible files");
            return [];
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Runs the sweep in the background and returns immediately — the caller is a Blazor
    /// circuit and this takes hours.
    /// </summary>
    public bool Start()
    {
        if (!_gate.Wait(0)) return false;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(token);
            }
            catch (OperationCanceledException)
            {
                log.LogInformation("Library conversion cancelled");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Library conversion failed");
            }
            finally
            {
                Update(Progress with { IsRunning = false, Current = null, FinishedAt = DateTimeOffset.UtcNow });
                _gate.Release();
            }
        }, CancellationToken.None);

        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (!transcoder.IsAvailable)
        {
            log.LogError("FFmpeg isn't available; nothing can be converted.");
            return;
        }

        var files = FindConvertible();
        Update(new TranscodeProgress(true, files.Count, 0, 0, null, null));

        if (files.Count == 0) return;

        log.LogInformation("Converting {Count} non-MP3 file(s) to MP3", files.Count);

        var done = 0;
        var failed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            Update(Progress with { Current = Path.GetFileName(file) });

            var output = await transcoder.TranscodeAsync(file, ct);
            if (output is null) failed++; else done++;

            Update(Progress with
            {
                Done = done,
                Failed = failed,
                LastError = transcoder.LastError ?? Progress.LastError,
            });

            // Every single file failing means something systemic — a missing codec, a
            // read-only share — not 700 individually unlucky files. Stop and say so rather
            // than grinding through the lot to reach the same conclusion an hour later.
            if (failed >= 5 && done == 0)
            {
                log.LogError(
                    "Stopping: the first {Failed} files all failed. Last error: {Error}",
                    failed, transcoder.LastError);
                return;
            }
        }

        log.LogInformation("Conversion finished: {Done} converted, {Failed} failed", done, failed);

        // Nothing is visible until the scanner has seen the new MP3s.
        await scanner.ScanAllAsync(ct);
    }

    private void Update(TranscodeProgress progress)
    {
        Progress = progress;
        Changed?.Invoke();
    }
}
