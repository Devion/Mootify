using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Library;

/// <summary>
/// Drives the scanner: startup scan, periodic full scan, and a debounced FileSystemWatcher.
/// The periodic scan stays the source of truth — watchers drop events on network shares.
/// </summary>
public sealed class LibraryScanService(
    LibraryScanner scanner,
    NetworkShareConnector shares,
    IOptionsMonitor<LibraryOptions> options,
    ILogger<LibraryScanService> log) : BackgroundService
{
    private FileSystemWatcher? _watcher;
    private readonly Lock _debounceLock = new();
    private CancellationTokenSource? _debounceCts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.CurrentValue;

        // A credentialled share needs its session opened before anything can read the path.
        await shares.EnsureConnectedAsync(stoppingToken);

        if (string.IsNullOrWhiteSpace(opts.MusicRoot) || !Directory.Exists(opts.MusicRoot))
        {
            log.LogWarning(
                "Library:MusicRoot ({Root}) is not reachable — the scanner is idle. " +
                "Set it to the path where *this app* sees the music (Lidarr's root folder is a path inside its own container).",
                string.IsNullOrWhiteSpace(opts.MusicRoot) ? "<not set>" : opts.MusicRoot);
            return;
        }

        if (opts.ScanOnStartup)
        {
            await SafeScanAsync(stoppingToken);
        }

        if (opts.WatchFileSystem)
        {
            StartWatcher(opts.MusicRoot, stoppingToken);
        }

        using var timer = new PeriodicTimer(opts.FullScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SafeScanAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void StartWatcher(string root, CancellationToken stoppingToken)
    {
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                Filter = "*.mp3",
                EnableRaisingEvents = true,
            };

            _watcher.Created += (_, _) => DebouncedScan(stoppingToken);
            _watcher.Deleted += (_, _) => DebouncedScan(stoppingToken);
            _watcher.Renamed += (_, _) => DebouncedScan(stoppingToken);
            _watcher.Changed += (_, _) => DebouncedScan(stoppingToken);
            _watcher.Error += (_, e) => log.LogWarning(e.GetException(), "File watcher error; periodic scan still covers it");

            log.LogInformation("Watching {Root} for changes", root);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not start the file watcher; falling back to periodic scans only");
        }
    }

    /// <summary>
    /// An album import fires hundreds of events. Collapse them into one scan once writes settle.
    /// </summary>
    private void DebouncedScan(CancellationToken stoppingToken)
    {
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var token = _debounceCts.Token;
            var delay = options.CurrentValue.WatchDebounce;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token);
                    await SafeScanAsync(token);
                }
                catch (OperationCanceledException)
                {
                    // superseded by a newer event, or shutting down
                }
            }, CancellationToken.None);
        }
    }

    private async Task SafeScanAsync(CancellationToken ct)
    {
        try
        {
            // Network shares drop — a reboot at the other end, a flaky switch. Reconnecting
            // before each scan is what makes the library heal itself instead of emptying.
            await shares.EnsureConnectedAsync(ct);

            await scanner.ScanAllAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed scan must not take the background service down for the rest of the process lifetime.
            log.LogError(ex, "Library scan failed");
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounceCts?.Dispose();
        base.Dispose();
    }
}
