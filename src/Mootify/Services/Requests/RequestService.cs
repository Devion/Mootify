using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Lidarr;
using Mootify.Services.Settings;

namespace Mootify.Services.Requests;

public sealed record CreateRequestResult(bool Ok, Guid? RequestId, string? Error);

/// <summary>Which slice of the list somebody is looking at.</summary>
public enum RequestFilter
{
    All,

    /// <summary>Still in flight — queued, searching, downloading, importing, converting.</summary>
    Open,

    /// <summary>Landed.</summary>
    Ready,

    /// <summary>Gave up: not found, or failed outright.</summary>
    Problem,
}

/// <summary>
/// Counts for the whole list, not the page. The page tells you what you're looking at; these
/// tell you how much there is, which is the thing a 50-row cap used to hide.
/// </summary>
public sealed record RequestCounts(int All, int Open, int Ready, int Problem)
{
    public static readonly RequestCounts Empty = new(0, 0, 0, 0);

    public int For(RequestFilter filter) => filter switch
    {
        RequestFilter.Open => Open,
        RequestFilter.Ready => Ready,
        RequestFilter.Problem => Problem,
        _ => All,
    };
}

public sealed record RequestPage(List<Request> Rows, int Total, int Skip, int Take, RequestCounts Counts)
{
    public static readonly RequestPage Empty = new([], 0, 0, 0, RequestCounts.Empty);

    public bool HasPrevious => Skip > 0;
    public bool HasNext => Skip + Rows.Count < Total;

    /// <summary>1-based, for "Page 3 of 11".</summary>
    public int PageNumber => Take <= 0 ? 1 : (Skip / Take) + 1;
    public int PageCount => Take <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)Take));
}

public sealed class RequestService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    LidarrClient lidarr,
    SettingsService settings,
    ILogger<RequestService> log)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>
    /// What counts as "the same request". Two asks share a key when granting the second would
    /// put the same song in the same place twice.
    ///
    /// This is deliberately one function rather than a query predicate: the search page uses it
    /// to grey out a song *before* it is clicked, and a check the UI and the service disagree
    /// about is worse than no check at all.
    /// </summary>
    public static string DuplicateKey(
        RequestKind kind,
        string albumMbid,
        string? trackTitle,
        string? recordingMbid,
        Guid? targetPlaylistId,
        Guid requesterId)
    {
        // A track is identified by its recording where MusicBrainz gave us one and by title
        // otherwise. Five songs off one album are five different requests against one release —
        // keying on the album alone would silently drop four of them.
        var what = kind switch
        {
            RequestKind.Track when !string.IsNullOrWhiteSpace(recordingMbid) => $"rec:{recordingMbid}",
            RequestKind.Track => $"title:{(trackTitle ?? "").Trim().ToLowerInvariant()}",
            _ => "whole",
        };

        // A playlist is a shared destination, so two people asking for the same song for the
        // same playlist is one request. With no playlist there is nothing shared to collide
        // with, and it is only a duplicate of that person's own ask.
        var where = targetPlaylistId is { } id ? $"pl:{id}" : $"user:{requesterId}";

        return $"{kind}|{albumMbid}|{what}|{where}";
    }

    private static string DuplicateKey(Request r) =>
        DuplicateKey(r.Kind, r.AlbumMusicBrainzId ?? "", r.TrackTitle, r.RecordingMusicBrainzId,
            r.TargetPlaylistId, r.RequesterId);

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

            // The admin's number if there is one, otherwise Requests:MaxOpenPerUser.
            var max = await settings.GetMaxOpenRequestsAsync(ct);
            if (openCount >= max)
            {
                return new CreateRequestResult(false, null,
                    $"You have {openCount} requests still in flight (limit {max}). Wait for one to land.");
            }
        }

        int? artistId = null;
        int? albumId = null;

        if (album.MusicBrainzId is { } albumMbid)
        {
            // Everything already asked for against this release, in one read. Both the duplicate
            // check and the Lidarr-id reuse below need it, and it is a handful of rows.
            var siblings = await db.Requests
                .AsNoTracking()
                .Where(r => r.AlbumMusicBrainzId == albumMbid)
                .Select(r => new
                {
                    r.Kind,
                    r.Status,
                    r.TrackTitle,
                    r.RecordingMusicBrainzId,
                    r.TargetPlaylistId,
                    r.RequesterId,
                    r.LidarrArtistId,
                    r.LidarrAlbumId,
                })
                .ToListAsync(ct);

            var wanted = DuplicateKey(kind, albumMbid, trackTitle, recordingMbid, targetPlaylistId, userId);

            var duplicate = siblings.Any(s =>
                s.Status != RequestStatus.NotFound
                && s.Status != RequestStatus.Failed
                && DuplicateKey(s.Kind, albumMbid, s.TrackTitle, s.RecordingMusicBrainzId,
                       s.TargetPlaylistId, s.RequesterId) == wanted);

            if (duplicate)
            {
                return new CreateRequestResult(false, null, "That one's already been requested.");
            }

            // Lidarr already knows this release, so don't tell it again. Re-adding costs a full
            // artist list and a second AlbumSearch per ask — which is how a 500-song import turns
            // into hundreds of redundant commands queued behind each other in Lidarr.
            var known = siblings.FirstOrDefault(s => s.LidarrAlbumId is not null);
            if (known is not null)
            {
                artistId = known.LidarrArtistId;
                albumId = known.LidarrAlbumId;
            }
        }

        var searchedNow = false;

        if (albumId is null)
        {
            var (addedArtistId, addedAlbumId, error) = await lidarr.AddAlbumAsync(album, ct);
            if (error is not null)
            {
                return new CreateRequestResult(false, null, error);
            }

            artistId = addedArtistId;
            albumId = addedAlbumId;
            searchedNow = lidarr.SearchesOnAdd;
        }

        var now = DateTimeOffset.UtcNow;

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
            // Riding on somebody else's add means nobody searched on our behalf. Leaving this
            // null is what tells the reconciler to pick it up on its next pass.
            LastSearchAt = searchedNow ? now : null,
            SearchAttempts = searchedNow ? 1 : 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Requests.Add(request);
        await db.SaveChangesAsync(ct);

        log.LogInformation("{User} requested {Album} (Lidarr album {LidarrId})", userId, album.Title, albumId);

        return new CreateRequestResult(true, request.Id, null);
    }

    /// <summary>
    /// One page of somebody's own requests, plus the counts for the whole list. Paged rather
    /// than capped: a 500-song import makes a list nobody can see the end of, and "the 50 most
    /// recent" quietly hides the 450 that are actually the problem.
    /// </summary>
    public async Task<RequestPage> GetForUserAsync(
        Guid userId,
        RequestFilter filter = RequestFilter.All,
        int skip = 0,
        int take = DefaultPageSize,
        CancellationToken ct = default) =>
        await PageAsync(r => r.RequesterId == userId, filter, skip, take, includeRequester: false, ct);

    public async Task<RequestPage> GetAllAsync(
        RequestFilter filter = RequestFilter.All,
        int skip = 0,
        int take = DefaultPageSize,
        CancellationToken ct = default) =>
        await PageAsync(_ => true, filter, skip, take, includeRequester: true, ct);

    private async Task<RequestPage> PageAsync(
        System.Linq.Expressions.Expression<Func<Request, bool>> scope,
        RequestFilter filter,
        int skip,
        int take,
        bool includeRequester,
        CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var scoped = db.Requests.AsNoTracking().Where(scope);

        // One grouped read for every chip, rather than four counts against the same table.
        var byStatus = await scoped
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var counts = new RequestCounts(
            All: byStatus.Sum(x => x.Count),
            Open: byStatus.Where(x => IsOpen(x.Status)).Sum(x => x.Count),
            Ready: byStatus.Where(x => x.Status == RequestStatus.Available).Sum(x => x.Count),
            Problem: byStatus.Where(x => IsProblem(x.Status)).Sum(x => x.Count));

        var total = counts.For(filter);

        // Asking for page 9 of a list that just shrank to three pages should show the last page,
        // not an empty one — the usual way to get here is cancelling rows off the end.
        if (skip >= total) skip = Math.Max(0, ((Math.Max(1, total) - 1) / take) * take);

        IQueryable<Request> query = Filtered(scoped, filter).Include(r => r.TargetPlaylist);
        if (includeRequester) query = query.Include(r => r.Requester);

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new RequestPage(rows, total, skip, take, counts);
    }

    private static IQueryable<Request> Filtered(IQueryable<Request> query, RequestFilter filter) => filter switch
    {
        RequestFilter.Open => query.Where(r => r.Status != RequestStatus.Available
                                            && r.Status != RequestStatus.NotFound
                                            && r.Status != RequestStatus.Failed),
        RequestFilter.Ready => query.Where(r => r.Status == RequestStatus.Available),
        RequestFilter.Problem => query.Where(r => r.Status == RequestStatus.NotFound
                                               || r.Status == RequestStatus.Failed),
        _ => query,
    };

    private static bool IsOpen(RequestStatus s) =>
        s is not (RequestStatus.Available or RequestStatus.NotFound or RequestStatus.Failed);

    private static bool IsProblem(RequestStatus s) =>
        s is RequestStatus.NotFound or RequestStatus.Failed;

    /// <summary>
    /// The duplicate keys of everything this user could collide with. The search page greys out
    /// what's already been asked for; without it the only way to find out is to click and be told
    /// no, which reads as a bug rather than as a duplicate.
    /// </summary>
    public async Task<HashSet<string>> GetActiveKeysAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Everything of the user's own, plus everyone's requests aimed at a playlist — those are
        // the two ways a new ask can turn out to be a duplicate. See DuplicateKey.
        var rows = await db.Requests
            .AsNoTracking()
            .Where(r => (r.RequesterId == userId || r.TargetPlaylistId != null)
                     && r.AlbumMusicBrainzId != null
                     && r.Status != RequestStatus.NotFound
                     && r.Status != RequestStatus.Failed)
            .Select(r => new
            {
                r.Kind,
                r.AlbumMusicBrainzId,
                r.TrackTitle,
                r.RecordingMusicBrainzId,
                r.TargetPlaylistId,
                r.RequesterId,
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => DuplicateKey(
            r.Kind, r.AlbumMusicBrainzId!, r.TrackTitle, r.RecordingMusicBrainzId,
            r.TargetPlaylistId, r.RequesterId))];
    }

    /// <summary>
    /// Takes a request off the list. Its own requester, or an admin.
    ///
    /// Deleting rather than marking cancelled, because the row's only job is to be waited on:
    /// a cancelled request that stays visible forever is the clutter this is meant to clear.
    /// Nothing points at it with a foreign key — <see cref="PlaylistItem.RequestId"/> and
    /// <see cref="Notification.RequestId"/> are bare values, so music that already landed keeps
    /// its playlist entry and its cowbell.
    /// </summary>
    public async Task<(bool Ok, string? Error)> CancelAsync(
        Guid userId, Guid requestId, bool asAdmin = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return (false, "That request is already gone.");
        }

        if (request.RequesterId != userId && !asAdmin)
        {
            // Same sentence as a missing row: whose requests exist is not this endpoint's to leak.
            return (false, "That request is already gone.");
        }

        var wasOpen = request.IsOpen;
        var albumId = request.LidarrAlbumId;

        db.Requests.Remove(request);
        await db.SaveChangesAsync(ct);

        // Stop Lidarr chasing something nobody is waiting for — but only once the last request
        // for that release is gone, or cancelling one song off an album would abandon the rest.
        // A download already handed to the client still finishes; unmonitoring only stops the
        // searching, and the file is welcome either way.
        if (wasOpen && albumId is { } id && lidarr.IsConfigured)
        {
            var stillWanted = await db.Requests.AnyAsync(
                r => r.LidarrAlbumId == id
                  && r.Status != RequestStatus.Available
                  && r.Status != RequestStatus.NotFound
                  && r.Status != RequestStatus.Failed, ct);

            if (!stillWanted)
            {
                await lidarr.SetAlbumsMonitoredAsync([id], false, ct);
            }
        }

        log.LogInformation("Request {RequestId} cancelled by {User}", requestId, userId);

        return (true, null);
    }

    /// <summary>
    /// Clears out everything that already landed or already gave up, for one user. The list is
    /// somewhere you check on things in flight; after a big import it is mostly history.
    /// </summary>
    public async Task<int> ClearFinishedAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Requests
            .Where(r => r.RequesterId == userId
                     && (r.Status == RequestStatus.Available
                      || r.Status == RequestStatus.NotFound
                      || r.Status == RequestStatus.Failed))
            .ExecuteDeleteAsync(ct);
    }
}
