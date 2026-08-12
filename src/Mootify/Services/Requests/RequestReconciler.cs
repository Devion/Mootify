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
/// Walks every open request and reconciles it against what Lidarr actually has.
///
/// Polling rather than trusting a webhook: webhooks get missed, and a request stuck at
/// "Downloading" forever is the thing that generates support messages. This is idempotent
/// and self-healing — running it twice changes nothing.
/// </summary>
public sealed class RequestReconciler(
    IDbContextFactory<MootifyDbContext> dbFactory,
    LidarrClient lidarr,
    LibraryScanner scanner,
    Transcoder transcoder,
    PlaylistService playlists,
    TeamService teams,
    NotificationDispatcher notifications,
    ILogger<RequestReconciler> log)
{
    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        if (!lidarr.IsConfigured) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var open = await db.Requests
            .Where(r => r.Status != RequestStatus.Available
                     && r.Status != RequestStatus.NotFound
                     && r.Status != RequestStatus.Failed)
            .ToListAsync(ct);

        if (open.Count == 0) return;

        var queue = await lidarr.GetQueueAsync(ct);

        foreach (var request in open)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ReconcileAsync(db, request, queue, ct);
            }
            catch (Exception ex)
            {
                // One bad request must not stall the other nine.
                log.LogError(ex, "Reconciling request {RequestId} failed", request.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileAsync(
        MootifyDbContext db,
        Request request,
        List<LidarrQueueItem> queue,
        CancellationToken ct)
    {
        if (request.LidarrAlbumId is not { } albumId) return;

        var previous = request.Status;

        // Still downloading?
        var queued = queue.FirstOrDefault(q => q.AlbumId == albumId);
        if (queued is not null)
        {
            request.Status = queued.TrackedDownloadState switch
            {
                "importPending" or "importing" => RequestStatus.Imported,
                _ => RequestStatus.Downloading,
            };

            if (request.Status != previous)
            {
                request.UpdatedAt = DateTimeOffset.UtcNow;
            }

            return;
        }

        var album = await lidarr.GetAlbumAsync(albumId, ct);
        if (album is null) return;

        var hasFiles = album.Statistics is { TrackFileCount: > 0 };
        if (!hasFiles)
        {
            // Not queued and no files: Lidarr is still searching, or found nothing.
            if (request.Status == RequestStatus.Pending)
            {
                request.Status = RequestStatus.Searching;
                request.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (DateTimeOffset.UtcNow - request.CreatedAt > TimeSpan.FromDays(7))
            {
                Fail(request, RequestStatus.NotFound, "Nothing found after a week of searching.");
            }

            return;
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
                Fail(request, RequestStatus.Failed,
                    "Lidarr grabbed a non-MP3 release and FFmpeg isn't available to convert it.");
                return;
            }

            request.Status = RequestStatus.Transcoding;
            request.UpdatedAt = DateTimeOffset.UtcNow;
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

        var trackIds = await MatchTracksAsync(request, folders, ct);
        if (trackIds.Count == 0)
        {
            log.LogWarning("Request {RequestId} has Lidarr files but nothing matched in the library", request.Id);
            return;
        }

        var addedTo = await AppendToPlaylistAsync(request, trackIds, ct);

        request.Status = RequestStatus.Available;
        request.CompletedAt = DateTimeOffset.UtcNow;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await NotifyAsync(request, trackIds.Count, addedTo, ct);
    }

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

    private sealed record AppendOutcome(string PlaylistName, Guid? TeamId);

    private async Task<AppendOutcome?> AppendToPlaylistAsync(Request request, List<Guid> trackIds, CancellationToken ct)
    {
        if (request.TargetPlaylistId is not { } playlistId) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Playlists
            .Where(p => p.Id == playlistId)
            .Select(p => new { p.Name, p.TeamId })
            .FirstOrDefaultAsync(ct);

        if (target is null)
        {
            // Playlist deleted while the download was in flight. Say so rather than failing
            // the request — the music did arrive.
            log.LogInformation("Target playlist for request {RequestId} is gone; skipping append", request.Id);
            return null;
        }

        // Runs as the requester, through the same authorization path as a manual add. If they
        // left the team while the download was in flight, this correctly refuses.
        var added = await playlists.AddTracksAsync(playlistId, request.RequesterId, trackIds, request.Id, ct);

        if (added == 0)
        {
            log.LogInformation(
                "Nothing appended for request {RequestId} — the requester may no longer have access to {Playlist}",
                request.Id, target.Name);
            return null;
        }

        return new AppendOutcome(target.Name, target.TeamId);
    }

    private async Task NotifyAsync(Request request, int trackCount, AppendOutcome? appended, CancellationToken ct)
    {
        var what = request.Kind == RequestKind.Track
            ? $"\"{request.TrackTitle}\" by {request.ArtistName}"
            : $"{request.AlbumTitle} by {request.ArtistName}";

        var tracks = $"{trackCount} track{(trackCount == 1 ? "" : "s")}";
        var url = request.TargetPlaylistId is { } id ? $"/playlist/{id}" : "/library";

        var body = appended is not null
            ? $"{tracks} added to {appended.PlaylistName}."
            : $"{tracks} added to your library.";

        await notifications.PublishAsync(
            request.RequesterId,
            NotificationType.RequestAvailable,
            $"{what} is ready",
            body,
            url,
            request.Id,
            ct);

        // A shared playlist that only the requester hears about is just a private playlist
        // in an awkward place. Tell the rest of the team too — with different wording, so
        // nobody thinks they asked for it.
        if (appended?.TeamId is not { } teamId) return;

        var requesterName = await GetDisplayNameAsync(request.RequesterId, ct);
        var others = await teams.GetMemberIdsExceptAsync(teamId, request.RequesterId, ct);

        foreach (var memberId in others)
        {
            await notifications.PublishAsync(
                memberId,
                NotificationType.RequestAvailable,
                $"{requesterName} added {what}",
                $"{tracks} added to {appended.PlaylistName}.",
                url,
                request.Id,
                ct);
        }
    }

    private async Task<string> GetDisplayNameAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "Somebody";
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
