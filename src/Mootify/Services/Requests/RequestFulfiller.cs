using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Teams;

namespace Mootify.Services.Requests;

/// <summary>
/// What it means for a request to be done: the tracks go into the playlist it was made
/// against, the row goes to <see cref="RequestStatus.Available"/>, and everybody who should
/// hear about it does.
///
/// It lives here rather than inside <see cref="RequestReconciler"/> because Soulseek is not
/// the only way music arrives — a file dropped into the import folder satisfies a
/// request just as completely, and two code paths that each decide separately what "done"
/// means is how one of them ends up not notifying anybody.
/// </summary>
public sealed class RequestFulfiller(
    IDbContextFactory<MootifyDbContext> dbFactory,
    PlaylistService playlists,
    TeamService teams,
    NotificationDispatcher notifications,
    ILogger<RequestFulfiller> log)
{
    /// <summary>
    /// <paramref name="db"/> is the caller's context, tracking <paramref name="request"/> —
    /// the status change has to be saved by whoever loaded the row, not by a second context
    /// that would overwrite the rest of the caller's pass.
    /// </summary>
    public async Task CompleteAsync(
        MootifyDbContext db, Request request, IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
    {
        var appended = await AppendToPlaylistAsync(request, trackIds, ct);

        var now = DateTimeOffset.UtcNow;
        request.Status = RequestStatus.Available;
        request.CompletedAt = now;
        request.UpdatedAt = now;

        // The failure that got it here no longer applies, and leaving it on the row means the
        // request list shows "nothing found after a week" next to music that is sitting there.
        request.FailureReason = null;

        await db.SaveChangesAsync(ct);

        await NotifyAsync(request, trackIds.Count, appended, ct);
    }

    private sealed record AppendOutcome(string PlaylistName, Guid? TeamId);

    private async Task<AppendOutcome?> AppendToPlaylistAsync(
        Request request, IReadOnlyList<Guid> trackIds, CancellationToken ct)
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
        var added = await playlists.AddTracksAsync(playlistId, request.RequesterId, [.. trackIds], request.Id, ct);

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
}
