using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Soulseek;
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
    SoulseekClient soulseek,
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
        string username,
        string filename,
        Guid? targetPlaylistId,
        Guid requesterId)
    {
        // A playlist is a shared destination, so two people asking for the same song for the
        // same playlist is one request. With no playlist there is nothing shared to collide
        // with, and it is only a duplicate of that person's own ask.
        var where = targetPlaylistId is { } id ? $"pl:{id}" : $"user:{requesterId}";

        return $"{username.Trim().ToLowerInvariant()}|{filename.Trim().ToLowerInvariant()}|{where}";
    }

    private static string DuplicateKey(Request r) =>
        DuplicateKey(r.SoulseekUsername ?? "", r.SoulseekFilename ?? "", r.TargetPlaylistId, r.RequesterId);

    /// <summary>
    /// Dispatches the exact selected Soulseek file straight to slskd. The quota is the only
    /// brake, and it exists to protect the disk rather than to police anyone.
    /// </summary>
    public async Task<CreateRequestResult> CreateAsync(
        Guid userId,
        SoulseekFile file,
        string query,
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

        var wanted = DuplicateKey(file.Username, file.Filename, targetPlaylistId, userId);
        var active = await db.Requests.AsNoTracking()
            .Where(r => (r.RequesterId == userId || r.TargetPlaylistId != null)
                     && r.SoulseekUsername != null && r.SoulseekFilename != null
                     && r.Status != RequestStatus.NotFound && r.Status != RequestStatus.Failed)
            .ToListAsync(ct);
        if (active.Any(r => DuplicateKey(r) == wanted))
            return new CreateRequestResult(false, null, "That file is already being downloaded.");

        var now = DateTimeOffset.UtcNow;

        var request = new Request
        {
            Id = Guid.NewGuid(),
            RequesterId = userId,
            Kind = RequestKind.Track,
            Status = RequestStatus.Pending,
            Query = query.Trim(),
            ArtistName = query.Trim(),
            TrackTitle = file.DisplayName,
            SoulseekUsername = file.Username,
            SoulseekFilename = file.Filename,
            TargetPlaylistId = targetPlaylistId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        request.SoulseekBatchId = request.Id;

        // Persist first so the request remains visible with a useful failure reason if slskd
        // refuses it; a successful enqueue then advances it to Downloading.
        db.Requests.Add(request);
        await db.SaveChangesAsync(ct);

        var (enqueued, error) = await soulseek.EnqueueAsync(request.Id, file, ct);
        if (!enqueued)
        {
            request.Status = RequestStatus.Failed;
            request.FailureReason = string.IsNullOrWhiteSpace(error) ? "slskd refused the download." : error;
            request.CompletedAt = DateTimeOffset.UtcNow;
            request.UpdatedAt = request.CompletedAt.Value;
            await db.SaveChangesAsync(ct);
            return new CreateRequestResult(false, request.Id, request.FailureReason);
        }

        request.Status = RequestStatus.Downloading;
        request.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        log.LogInformation("{User} requested {File} from Soulseek user {Peer}", userId, file.Filename, file.Username);

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
                     && r.SoulseekUsername != null
                     && r.SoulseekFilename != null
                     && r.Status != RequestStatus.NotFound
                     && r.Status != RequestStatus.Failed)
            .Select(r => new
            {
                r.SoulseekUsername,
                r.SoulseekFilename,
                r.TargetPlaylistId,
                r.RequesterId,
            })
            .ToListAsync(ct);

        return [.. rows.Select(r => DuplicateKey(
            r.SoulseekUsername!, r.SoulseekFilename!, r.TargetPlaylistId, r.RequesterId))];
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
        var batchId = request.SoulseekBatchId;

        db.Requests.Remove(request);
        await db.SaveChangesAsync(ct);

        if (wasOpen && batchId is { } id && soulseek.IsConfigured)
            await soulseek.CancelBatchAsync(id, ct);

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
