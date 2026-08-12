using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Lidarr;

namespace Mootify.Services.Requests;

public sealed record CreateRequestResult(bool Ok, Guid? RequestId, string? Error);

public sealed class RequestService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    LidarrClient lidarr,
    IOptionsMonitor<RequestOptions> options,
    ILogger<RequestService> log)
{
    /// <summary>
    /// Dispatches straight to Lidarr — no approval step, by design. The quota is the only
    /// brake, and it exists to protect the disk rather than to police anyone.
    /// </summary>
    public async Task<CreateRequestResult> CreateAsync(
        Guid userId,
        LidarrAlbum album,
        RequestKind kind,
        string? trackTitle,
        string? recordingMbid,
        Guid? targetPlaylistId,
        CancellationToken ct = default,
        /// <summary>
        /// Imports set this false. The quota is there to stop somebody casually queueing a
        /// discography; an import is somebody deliberately queueing a discography, having
        /// been shown the number first.
        /// </summary>
        bool enforceQuota = true)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (enforceQuota)
        {
            var openCount = await db.Requests.CountAsync(
                r => r.RequesterId == userId
                  && r.Status != RequestStatus.Available
                  && r.Status != RequestStatus.NotFound
                  && r.Status != RequestStatus.Failed, ct);

            var max = options.CurrentValue.MaxOpenPerUser;
            if (openCount >= max)
            {
                return new CreateRequestResult(false, null,
                    $"You have {openCount} requests still in flight (limit {max}). Wait for one to land.");
            }
        }

        if (album.MusicBrainzId is not null)
        {
            // Scoped by kind. Five songs from one album are five separate track requests
            // against the same release — deduping on the album alone would silently drop
            // four of them, and only the first song would ever reach the playlist.
            var duplicate = kind == RequestKind.Track
                ? await db.Requests.AnyAsync(
                    r => r.AlbumMusicBrainzId == album.MusicBrainzId
                      && r.Kind == RequestKind.Track
                      && r.TrackTitle == trackTitle
                      && r.TargetPlaylistId == targetPlaylistId
                      && r.Status != RequestStatus.NotFound
                      && r.Status != RequestStatus.Failed, ct)
                : await db.Requests.AnyAsync(
                    r => r.AlbumMusicBrainzId == album.MusicBrainzId
                      && r.Kind != RequestKind.Track
                      && r.Status != RequestStatus.NotFound
                      && r.Status != RequestStatus.Failed, ct);

            if (duplicate)
            {
                return new CreateRequestResult(false, null, "That one's already been requested.");
            }
        }

        var (artistId, albumId, error) = await lidarr.AddAlbumAsync(album, ct);
        if (error is not null)
        {
            return new CreateRequestResult(false, null, error);
        }

        var request = new Request
        {
            Id = Guid.NewGuid(),
            RequesterId = userId,
            Kind = kind,
            Status = RequestStatus.Searching,
            Query = trackTitle ?? album.Title,
            ArtistName = album.Artist?.ArtistName ?? "",
            AlbumTitle = album.Title,
            TrackTitle = trackTitle,
            ArtistMusicBrainzId = album.Artist?.MusicBrainzId,
            AlbumMusicBrainzId = album.MusicBrainzId,
            RecordingMusicBrainzId = recordingMbid,
            LidarrArtistId = artistId,
            LidarrAlbumId = albumId,
            TargetPlaylistId = targetPlaylistId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Requests.Add(request);
        await db.SaveChangesAsync(ct);

        log.LogInformation("{User} requested {Album} (Lidarr album {LidarrId})", userId, album.Title, albumId);

        return new CreateRequestResult(true, request.Id, null);
    }

    public async Task<List<Request>> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Requests
            .AsNoTracking()
            .Include(r => r.TargetPlaylist)
            .Where(r => r.RequesterId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
    }

    public async Task<List<Request>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Requests
            .AsNoTracking()
            .Include(r => r.Requester)
            .Include(r => r.TargetPlaylist)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
    }
}
