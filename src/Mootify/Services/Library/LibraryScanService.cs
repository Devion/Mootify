using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Library;

/// <summary>
/// Drives the scanner: startup scan, periodic full scan, and a debounced FileSystemWatcher.
/// The periodic scan stays the source of truth — watchers drop events on network shares.
/// </summary>
public sealed class LibraryScanService(
    LibraryScanner scanner,
    LibraryFiler filer,
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

        EnsureDropFolder();

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

    /// <summary>
    /// A drop folder nobody can find is a feature nobody uses, and "create this folder yourself
    /// first" is the step that gets skipped. Making it is free and creates nothing to clean up.
    /// </summary>
    private void EnsureDropFolder()
    {
        if (filer.DropFolder is not { } drop) return;

        try
        {
            if (Directory.Exists(drop)) return;

            Directory.CreateDirectory(drop);
            log.LogInformation("Created the import drop folder at {Drop}", drop);
        }
        catch (Exception ex)
        {
            // A read-only share is a perfectly good library; it just can't take drops.
            log.LogWarning(ex, "Could not create the import drop folder at {Drop}", drop);
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
            };

            // Everything worth reacting to, not just MP3. This stayed "*.mp3" after FLAC became
            // a library entry in its own right, so a FLAC arriving fired no event at all and
            // stayed invisible for up to Library:FullScanInterval. The convertible formats are
            // in here too because a file dropped into the import folder should be filed within
            // the debounce rather than at the next six-hourly scan.
            //
            // The events only ever start a full scan; the filter just decides whether one is
            // worth starting, which is why a broad list is cheap and a wrong one is not.
            foreach (var extension in LibraryFiler.MusicExtensions)
            {
                _watcher.Filters.Add($"*{extension}");
            }

            _watcher.Created += (_, e) => DebouncedScan(e.FullPath, stoppingToken);
            _watcher.Deleted += (_, e) => DebouncedScan(e.FullPath, stoppingToken);
            _watcher.Renamed += (_, e) => DebouncedScan(e.FullPath, stoppingToken);
            _watcher.Changed += (_, e) => DebouncedScan(e.FullPath, stoppingToken);
            _watcher.Error += (_, e) => log.LogWarning(e.GetException(), "File watcher error; periodic scan still covers it");

            _watcher.EnableRaisingEvents = true;

            log.LogInformation(
                "Watching {Root} for {Extensions}",
                root, string.Join(", ", LibraryFiler.MusicExtensions));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not start the file watcher; falling back to periodic scans only");
        }
    }

    /// <summary>
    /// An album import fires hundreds of events. Collapse them into one scan once writes settle.
    ///
    /// <b>The duplicates folder is not worth waking up for.</b> An organize pass moves every
    /// merged-away copy into it, which is hundreds of events for files that have deliberately
    /// just left the library — and the scan they'd start would run straight into the pass that
    /// is still going. The drop folder stays watched, because a file landing there is exactly
    /// what the debounce exists to notice.
    /// </summary>
    private void DebouncedScan(string path, CancellationToken stoppingToken)
    {
        if (filer.DuplicatesFolder is { } quarantine && LibraryFiler.Contains(quarantine, path))
        {
            return;
        }

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
