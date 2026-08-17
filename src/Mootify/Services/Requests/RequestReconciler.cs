using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Library;
using Mootify.Services.Lidarr;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Teams;
using Mootify.Services.Transcoding;

namespace Mootify.Services.Requests;

/// <summary>
/// What a pass actually did. "Check now" used to be a button with no answer, which is how a
/// list of 500 requests and a Lidarr queue of 24 can sit side by side looking like a bug.
/// </summary>
public sealed record ReconcileSummary(
    int Open,
    int Albums,
    int Downloading,
    int Completed,
    int Searched,
    int AwaitingSearch,
    int GaveUp)
{
    public static readonly ReconcileSummary Idle = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Walks every open request and reconciles it against what Lidarr actually has.
///
/// Polling rather than trusting a webhook: webhooks get missed, and a request stuck at
/// "Downloading" forever is the thing that generates support messages. This is idempotent
/// and self-healing — running it twice changes nothing.
///
/// **Work is per album, not per request.** An import turns 500 songs into 500 rows pointing at
/// maybe 180 releases, and Lidarr fetches releases. Asking it about each row separately is 500
/// HTTP calls to learn 180 facts, and it is what made a pass over a real import take longer than
/// the interval between passes.
/// </summary>
public sealed class RequestReconciler(
    IDbContextFactory<MootifyDbContext> dbFactory,
    LidarrClient lidarr,
    LibraryScanner scanner,
    Transcoder transcoder,
    RequestFulfiller fulfiller,
    IOptionsMonitor<LidarrOptions> lidarrOptions,
    ILogger<RequestReconciler> log)
{
    /// <summary>
    /// After this many fruitless searches, stop asking and let the seven-day timeout call it.
    /// Some albums simply aren't on anybody's indexers, and re-asking every ten minutes for a
    /// week is a thousand searches to learn that.
    /// </summary>
    private const int MaxSearchAttempts = 8;

    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

    public async Task<ReconcileSummary> ReconcileAllAsync(CancellationToken ct = default)
    {
        if (!lidarr.IsConfigured) return ReconcileSummary.Idle;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var open = await db.Requests
            .Where(r => r.Status != RequestStatus.Available
                     && r.Status != RequestStatus.NotFound
                     && r.Status != RequestStatus.Failed)
            .ToListAsync(ct);

        if (open.Count == 0) return ReconcileSummary.Idle;

        var queue = await lidarr.GetQueueAsync(ct);

        // One group per release. Everything below costs one round trip per group rather than
        // one per row, which for a big import is the difference between a minute and an hour.
        var albums = open
            .Where(r => r.LidarrAlbumId is not null)
            .GroupBy(r => r.LidarrAlbumId!.Value)
            .ToList();

        var downloading = 0;
        var completed = 0;
        var gaveUp = 0;

        // Albums Lidarr has nothing for and hasn't been asked about recently enough.
        var wantSearch = new List<int>();

        foreach (var group in albums)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var outcome = await ReconcileAlbumAsync(db, group.Key, [.. group], queue, ct);

                downloading += outcome.Downloading;
                completed += outcome.Completed;
                gaveUp += outcome.GaveUp;

                if (outcome.NeedsSearch) wantSearch.Add(group.Key);
            }
            catch (Exception ex)
            {
                // One bad album must not stall the other hundred and seventy-nine.
                log.LogError(ex, "Reconciling Lidarr album {AlbumId} failed", group.Key);
            }
        }

        var searched = await ReSearchAsync(db, open, wantSearch, ct);

        await db.SaveChangesAsync(ct);

        var summary = new ReconcileSummary(
            Open: open.Count,
            Albums: albums.Count,
            Downloading: downloading,
            Completed: completed,
            Searched: searched,
            AwaitingSearch: Math.Max(0, wantSearch.Count - searched),
            GaveUp: gaveUp);

        log.LogInformation(
            "Reconciled {Open} open request(s) across {Albums} album(s): {Downloading} downloading, " +
            "{Completed} completed, {Searched} re-searched ({Awaiting} still queued for a search), {GaveUp} gave up",
            summary.Open, summary.Albums, summary.Downloading, summary.Completed,
            summary.Searched, summary.AwaitingSearch, summary.GaveUp);

        return summary;
    }

    /// <summary>
    /// Tells Lidarr to go looking again, for albums it has come back empty on.
    ///
    /// This is the half that was missing, and it is why a big import looks like it half worked:
    /// <c>AddAlbumAsync</c> searches once when the request is made, several hundred of those land
    /// on Lidarr at once, and a search that finds nothing right then leaves no trace anywhere. The
    /// grabs that did work show up in the queue; the rest sit at "Searching" until the seven-day
    /// timeout, with nothing in the system that would ever ask again.
    ///
    /// Batched into one command because Lidarr runs commands in series, and capped per pass
    /// because behind every search is somebody's indexer with a rate limit.
    /// </summary>
    private async Task<int> ReSearchAsync(
        MootifyDbContext db, List<Request> open, List<int> albumIds, CancellationToken ct)
    {
        if (albumIds.Count == 0) return 0;

        var cap = Math.Max(1, lidarrOptions.CurrentValue.MaxSearchesPerPass);

        // Least recently asked first, so a pass that can't cover everything still works its way
        // round rather than re-asking the same fifty every ten minutes.
        var lastSearch = open
            .Where(r => r.LidarrAlbumId is not null)
            .GroupBy(r => r.LidarrAlbumId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(r => r.LastSearchAt));

        var batch = albumIds
            .OrderBy(id => lastSearch.GetValueOrDefault(id) ?? DateTimeOffset.MinValue)
            .Take(cap)
            .ToList();

        if (!await lidarr.SearchAlbumsAsync(batch, ct))
        {
            log.LogWarning("Lidarr refused the AlbumSearch command for {Count} album(s)", batch.Count);
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var searched = batch.ToHashSet();

        foreach (var request in open.Where(r =>
                     r.IsOpen && r.LidarrAlbumId is { } id && searched.Contains(id)))
        {
            request.LastSearchAt = now;
            request.SearchAttempts++;
            request.UpdatedAt = now;
        }

        return batch.Count;
    }

    private sealed record AlbumOutcome(int Downloading, int Completed, int GaveUp, bool NeedsSearch);

    /// <summary>
    /// One Lidarr release and every request waiting on it. The Lidarr calls, the transcode and
    /// the rescan happen once here no matter how many songs off it somebody asked for; only the
    /// track matching, the playlist append and the cowbell are per request.
    /// </summary>
    private async Task<AlbumOutcome> ReconcileAlbumAsync(
        MootifyDbContext db,
        int albumId,
        List<Request> requests,
        List<LidarrQueueItem> queue,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Still downloading?
        var queued = queue.FirstOrDefault(q => q.AlbumId == albumId);
        if (queued is not null)
        {
            var status = queued.TrackedDownloadState switch
            {
                "importPending" or "importing" => RequestStatus.Imported,
                _ => RequestStatus.Downloading,
            };

            foreach (var request in requests)
            {
                if (request.Status == status) continue;

                request.Status = status;
                request.UpdatedAt = now;
            }

            return new AlbumOutcome(requests.Count, 0, 0, NeedsSearch: false);
        }

        var album = await lidarr.GetAlbumAsync(albumId, ct);
        if (album is null) return new AlbumOutcome(0, 0, 0, NeedsSearch: false);

        var hasFiles = album.Statistics is { TrackFileCount: > 0 };
        if (!hasFiles)
        {
            // Not queued and no files: Lidarr is still searching, or its search came back empty.
            var gaveUp = 0;

            foreach (var request in requests)
            {
                if (request.Status == RequestStatus.Pending)
                {
                    request.Status = RequestStatus.Searching;
                    request.UpdatedAt = now;
                }
                else if (now - request.CreatedAt > GiveUpAfter)
                {
                    Fail(request, RequestStatus.NotFound, "Nothing found after a week of searching.");
                    gaveUp++;
                }
            }

            // Nobody left waiting on it means nothing left to search for — the whole group can
            // hit the seven-day mark in the same pass.
            var stillWanted = requests.Any(r => r.IsOpen);

            return new AlbumOutcome(0, 0, gaveUp, NeedsSearch: stillWanted && DueForSearch(requests, now));
        }

        // Files exist. Transcode anything unplayable, then scan, then append, then notify —
        // in that order, or the playlist append finds no Track rows to append.
        var files = await lidarr.GetTrackFilesAsync(albumId, ct);
        var folders = files
            .Select(f => Path.GetDirectoryName(MapPath(f.Path)))
            .OfType<string>()
            .Distinct()
            .ToList();

        var needsTranscode = files.Any(f => Transcoder.NeedsTranscode(f.Path));
        if (needsTranscode)
        {
            if (!transcoder.IsAvailable)
            {
                foreach (var request in requests)
                {
                    Fail(request, RequestStatus.Failed,
                        "Lidarr grabbed a non-MP3 release and FFmpeg isn't available to convert it.");
                }

                return new AlbumOutcome(0, 0, requests.Count, NeedsSearch: false);
            }

            foreach (var request in requests)
            {
                request.Status = RequestStatus.Transcoding;
                request.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);

            foreach (var folder in folders)
            {
                await transcoder.TranscodeFolderAsync(folder, ct);
            }
        }

        foreach (var folder in folders)
        {
            await scanner.ScanPathAsync(folder, ct);
        }

        var completed = 0;

        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();

            var trackIds = await MatchTracksAsync(request, folders, ct);
            if (trackIds.Count == 0)
            {
                log.LogWarning("Request {RequestId} has Lidarr files but nothing matched in the library", request.Id);
                continue;
            }

            // Appending, marking available and notifying are the same three things whether the
            // music came from Lidarr or was dropped into the import folder by hand.
            await fulfiller.CompleteAsync(db, request, trackIds, ct);
            completed++;
        }

        return new AlbumOutcome(0, completed, 0, NeedsSearch: false);
    }

    private static bool DueForSearch(List<Request> requests, DateTimeOffset now) =>
        DueForSearch(requests.Max(r => r.SearchAttempts), requests.Max(r => r.LastSearchAt), now);

    /// <summary>
    /// Whether it's time to ask Lidarr again. Backs off so the first few misses are retried
    /// quickly — most of them are an indexer that was busy — and the hopeless ones taper off
    /// instead of grinding away for a week.
    /// </summary>
    public static bool DueForSearch(int attempts, DateTimeOffset? lastSearch, DateTimeOffset now)
    {
        if (attempts >= MaxSearchAttempts) return false;

        // Never asked — which is every request that rode in on somebody else's album add, and
        // every request made before this existed at all.
        return lastSearch is null || now - lastSearch.Value >= SearchBackoff(attempts);
    }

    /// <summary>
    /// How long to wait before the next ask. The tail is a day apart on purpose: past four
    /// attempts the album is probably not out there, and the seven-day timeout is what ends it.
    /// </summary>
    public static TimeSpan SearchBackoff(int attempts) => attempts switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromMinutes(30),
        2 => TimeSpan.FromHours(2),
        3 => TimeSpan.FromHours(6),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>
    /// Match in order of confidence: recording MBID, then title within the imported folders.
    /// For an album request take everything; for a track request take the single best match.
    /// </summary>
    private async Task<List<Guid>> MatchTracksAsync(Request request, List<string> folders, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.Tracks.AsNoTracking().Where(t => t.IsPresent);

        var candidates = await query
            .Where(t => folders.Any(f => t.Path.StartsWith(f)))
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .Select(t => new { t.Id, t.Title, t.RecordingMusicBrainzId })
            .ToListAsync(ct);

        if (candidates.Count == 0) return [];

        if (request.Kind != RequestKind.Track)
        {
            return [.. candidates.Select(c => c.Id)];
        }

        var byMbid = request.RecordingMusicBrainzId is { } mbid
            ? candidates.FirstOrDefault(c => c.RecordingMusicBrainzId == mbid)
            : null;

        if (byMbid is not null)
        {
            return [byMbid.Id];
        }

        var wanted = (request.TrackTitle ?? "").Trim();
        var byTitle = candidates.FirstOrDefault(c => string.Equals(c.Title, wanted, StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault(c => c.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (byTitle is null)
        {
            // Log the runners-up so a mismatch is diagnosable instead of mysterious.
            log.LogWarning("No title match for \"{Wanted}\". Candidates: {Candidates}",
                wanted, string.Join(", ", candidates.Take(10).Select(c => c.Title)));
            return [];
        }

        return [byTitle.Id];
    }

    private void Fail(Request request, RequestStatus status, string reason)
    {
        request.Status = status;
        request.FailureReason = reason;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        log.LogWarning("Request {RequestId} -> {Status}: {Reason}", request.Id, status, reason);
    }

    /// <summary>
    /// Lidarr reports paths as its own container sees them (/music/...). If Mootify sees the
    /// same files somewhere else, this is where that translation belongs.
    /// </summary>
    private static string MapPath(string lidarrPath) => lidarrPath;
}

public sealed class RequestReconcilerService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<LidarrOptions> options,
    ILogger<RequestReconcilerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.IsConfigured)
        {
            log.LogInformation("Lidarr is not configured; the reconciler is idle.");
            return;
        }

        using var timer = new PeriodicTimer(options.CurrentValue.PollInterval);

        try
        {
            do
            {
                using var scope = scopeFactory.CreateScope();
                var reconciler = scope.ServiceProvider.GetRequiredService<RequestReconciler>();

                try
                {
                    await reconciler.ReconcileAllAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Reconciliation pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
