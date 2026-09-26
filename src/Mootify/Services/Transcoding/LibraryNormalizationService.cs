using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Services.Transcoding;

public sealed record NormalizationProgress(
    bool IsRunning = false,
    int Total = 0,
    int Prepared = 0,
    int AlreadyReady = 0,
    int Failed = 0,
    string? Current = null,
    string? LastError = null,
    bool CancelRequested = false,
    bool Cancelled = false,
    DateTimeOffset? FinishedAt = null,
    int Parallelism = 1)
{
    public int Processed => Prepared + AlreadyReady + Failed;
    public int Percent => Total == 0 ? 0 : (int)(100.0 * Processed / Total);
}

/// <summary>
/// Manually prepares the same normalized cache entries that playback requests. One server-wide
/// job survives navigation; shutdown cancels it. Reruns reuse completed copies, including those
/// prepared before a cancellation or restart. Source files are never rewritten.
/// </summary>
public sealed class LibraryNormalizationService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    TranscodeCache cache,
    Transcoder transcoder,
    ILogger<LibraryNormalizationService> log) : BackgroundService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _requested = new(0, 1);
    private CancellationTokenSource? _runCancellation;
    private NormalizationProgress _progress = new();

    public NormalizationProgress Progress { get { lock (_sync) return _progress; } }
    public event Action? Changed;

    public async Task<(bool Ok, string? Error)> PrepareAsync(Guid adminId, CancellationToken ct = default, int parallelism = 1)
    {
        if (!await IsAdminAsync(adminId, ct)) return (false, "Admins only.");
        if (parallelism is < 1 or > 8) return (false, "Choose between 1 and 8 parallel normalizations.");
        if (!transcoder.IsAvailable) return (false, "FFmpeg is unavailable. Check its configuration first.");
        lock (_sync)
        {
            if (_runCancellation is not null) return (false, "Preparation is already running.");
            _runCancellation = new CancellationTokenSource();
            _progress = new(IsRunning: true, Parallelism: parallelism);
            _requested.Release();
        }
        Notify();
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> CancelAsync(Guid adminId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(adminId, ct)) return (false, "Admins only.");
        lock (_sync)
        {
            if (_runCancellation is null) return (false, "No preparation is running.");
            _progress = _progress with { CancelRequested = true };
            _runCancellation.Cancel();
        }
        Notify();
        return (true, null);
    }

    private async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin && !u.IsBanned && !u.ApprovalPending, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _requested.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

            CancellationTokenSource request;
            lock (_sync) request = _runCancellation!;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, request.Token);
            try
            {
                await PrepareLibraryAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Update(p => p with { Cancelled = true });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Library normalization preparation failed");
                Update(p => p with { LastError = ex.Message });
            }
            finally
            {
                lock (_sync)
                {
                    _progress = _progress with
                    {
                        IsRunning = false, Current = null,
                        Cancelled = _progress.Cancelled || linked.IsCancellationRequested,
                        FinishedAt = DateTimeOffset.UtcNow,
                    };
                    _runCancellation = null;
                    request.Dispose();
                }
                Notify();
            }
        }
    }

    private async Task PrepareLibraryAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tracks = await db.Tracks.AsNoTracking()
            .Where(t => t.IsPresent && t.Path.ToLower().EndsWith(".mp3"))
            .OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Path, t.Title })
            .ToListAsync(ct);
        Update(p => p with { Total = tracks.Count });

        var consecutiveFailures = 0;
        var stoppedForFailures = false;
        var active = new Dictionary<Guid, string>();
        await Parallel.ForEachAsync(tracks, new ParallelOptions
        {
            MaxDegreeOfParallelism = Progress.Parallelism,
            CancellationToken = ct,
        }, async (track, workerToken) =>
        {
            lock (_sync)
            {
                if (stoppedForFailures) return;
                active[track.Id] = track.Title;
                _progress = _progress with { Current = string.Join(" · ", active.Values) };
            }
            Notify();
            try
            {
                if (cache.FindReady(track.Id, track.Path, normalize: true) is not null)
                    Update(p => p with { AlreadyReady = p.AlreadyReady + 1 });
                else
                {
                    var path = await cache.GetOrCreateAsync(track.Id, track.Path, workerToken, normalize: true);
                    if (path is null)
                        throw new IOException(File.Exists(track.Path)
                            ? transcoder.LastError ?? "Could not prepare this track."
                            : "The source file is missing or inaccessible.");
                    Update(p => p with { Prepared = p.Prepared + 1 });
                }
                lock (_sync) consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not prepare normalized playback for {TrackId}", track.Id);
                lock (_sync)
                {
                    // Stop scheduling new work, but let tracks already in flight finish.
                    stoppedForFailures |= ++consecutiveFailures >= 5;
                    _progress = _progress with
                    {
                        Failed = _progress.Failed + 1,
                        LastError = (stoppedForFailures ? "Stopped after five consecutive failures. " : "")
                            + $"{track.Title}: {ex.Message}",
                    };
                }
            }
            finally
            {
                lock (_sync)
                {
                    active.Remove(track.Id);
                    _progress = _progress with { Current = active.Count == 0 ? null : string.Join(" · ", active.Values) };
                }
                Notify();
            }
        });
    }

    private void Update(Func<NormalizationProgress, NormalizationProgress> update)
    {
        lock (_sync) _progress = update(_progress);
        Notify();
    }

    private void Notify()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { log.LogDebug(ex, "Normalization progress subscriber disconnected"); }
        }
    }
}
