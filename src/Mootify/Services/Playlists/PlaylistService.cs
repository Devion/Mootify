using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Library;
using Mootify.Services.Teams;

namespace Mootify.Services.Playlists;

public sealed record PlaylistSummary(
    Guid Id,
    string Name,
    int TrackCount,
    TimeSpan TotalDuration,
    Guid? TeamId = null,
    string? TeamName = null)
{
    public bool IsTeamPlaylist => TeamId is not null;
}

/// <summary>
/// One row of a playlist, flattened. Everything both callers need is projected in the query that
/// pages it: the website wants a title, an artist and a duration, and the API additionally wants
/// the ids and numbers that make an <c>ApiTrack</c>. Projecting the union once beats loading the
/// entity graph and then asking for the same tracks again.
///
/// <see cref="DurationTicks"/> rather than a <c>TimeSpan</c>, for the reason in
/// <see cref="Data.Track.DurationTicks"/> — the mapped column is the one SQLite can aggregate.
/// </summary>
public sealed record PlaylistTrackRow(
    Guid ItemId,
    Guid TrackId,
    string Title,
    Guid ArtistId,
    string ArtistName,
    Guid AlbumId,
    string AlbumTitle,
    int? Year,
    int TrackNumber,
    int DiscNumber,
    long DurationTicks,
    int Bitrate,
    DateTimeOffset AddedAt)
{
    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
}

/// <summary>
/// One page of a playlist, with the totals for the whole thing.
///
/// <see cref="PlaylistTotal"/> and <see cref="TotalDuration"/> describe the playlist, not the page.
/// <see cref="Total"/> is the number of rows matching the current search (and is therefore the
/// same as <see cref="PlaylistTotal"/> when there is no search). A pager that can only count what
/// it fetched can't say where the current range ends.
/// </summary>
public sealed record PlaylistTrackPage(
    Guid Id,
    string Name,
    string? Description,
    Guid? TeamId,
    string? TeamName,
    bool CanDelete,
    int PlaylistTotal,
    int Total,
    TimeSpan TotalDuration,
    int Skip,
    int Take,
    List<PlaylistTrackRow> Rows)
{
    public bool IsTeamPlaylist => TeamId is not null;

    public bool HasPrevious => Skip > 0;
    public bool HasNext => Skip + Rows.Count < Total;

    /// <summary>1-based, for "Page 3 of 11".</summary>
    public int PageNumber => Take <= 0 ? 1 : (Skip / Take) + 1;
    public int PageCount => Take <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)Take));
}

/// <summary>
/// Every read and write goes through here so the ownership check lives in one place.
/// Scattering <c>if (playlist.OwnerId == userId)</c> through Razor components is how you
/// miss one — including the auto-append on request completion, which runs without a user
/// sitting in front of it. The rules themselves are in <see cref="PlaylistAccess"/>.
/// </summary>
public sealed class PlaylistService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    ILogger<PlaylistService> log)
{
    /// <summary>Gap between sort keys. Inserts take the midpoint, so reorders touch one row.</summary>
    private const double SortKeyStep = 1024;

    /// <summary>
    /// Rows per page when nobody says otherwise. Big enough that most playlists are one page,
    /// small enough that the 200-track ones stop being a wait.
    /// </summary>
    public const int DefaultPageSize = 100;

    /// <summary>Ceiling on a caller-supplied page size, for the reason <c>Api:MaxPageSize</c> exists.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Personal playlists and every playlist belonging to a team the user is in.</summary>
    public async Task<List<PlaylistSummary>> GetForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);

        var rows = await db.Playlists
            .AsNoTracking()
            .Where(p => p.OwnerUserId == userId || (p.TeamId != null && teamIds.Contains(p.TeamId.Value)))
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.TeamId,
                TeamName = p.Team!.Name,
                TrackCount = p.Items.Count(i => i.Track!.IsPresent),
                Ticks = p.Items.Where(i => i.Track!.IsPresent).Sum(i => i.Track!.DurationTicks),
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new PlaylistSummary(
                r.Id, r.Name, r.TrackCount, TimeSpan.FromTicks(r.Ticks), r.TeamId, r.TeamName))
        ];
    }

    public async Task<List<PlaylistSummary>> GetForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.Playlists
            .AsNoTracking()
            .Where(p => p.TeamId == teamId)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.TeamId,
                TeamName = p.Team!.Name,
                TrackCount = p.Items.Count(i => i.Track!.IsPresent),
                Ticks = p.Items.Where(i => i.Track!.IsPresent).Sum(i => i.Track!.DurationTicks),
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new PlaylistSummary(
                r.Id, r.Name, r.TrackCount, TimeSpan.FromTicks(r.Ticks), r.TeamId, r.TeamName))
        ];
    }

    /// <summary>
    /// Whether the user may write to this playlist. <see cref="AddTracksAsync"/> answers 0 both
    /// when it was refused and when there was nothing to add, so callers that have to tell those
    /// apart — the importer, which reports one as an error and the other as "already in there" —
    /// ask here first.
    /// </summary>
    public async Task<bool> CanEditAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);

        if (playlist is null) return false;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        return PlaylistAccess.CanEdit(playlist, userId, teamIds);
    }

    /// <summary>
    /// Whether the user may see this playlist at all. Today that is the same answer as
    /// <see cref="CanEditAsync"/> — see <see cref="PlaylistAccess.CanEdit"/> — but callers that
    /// mean "read" say read, so the day those two diverge they don't silently become write checks.
    /// </summary>
    public async Task<bool> CanReadAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);

        if (playlist is null) return false;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        return PlaylistAccess.CanRead(playlist, userId, teamIds);
    }

    /// <summary>
    /// One page of a playlist's tracks, plus what the header needs.
    ///
    /// <see cref="GetAsync"/> loads the entity graph — every item, its track, that track's artist
    /// and its album — which is the right shape for editing one row and the wrong shape for showing
    /// a long list. A 200-track playlist is 200 items joined four ways and materialised into tracked
    /// objects, which the API then re-queries to serialise. This is a projection and a LIMIT: the
    /// cost is the page, not the playlist.
    ///
    /// Absent tracks are left out of the rows <i>and</i> the total, because a playlist that says
    /// 200 and shows 197 is a bug report waiting to happen.
    /// </summary>
    public async Task<PlaylistTrackPage?> GetPageAsync(
        Guid playlistId,
        Guid userId,
        int skip = 0,
        int take = DefaultPageSize,
        string? query = null,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists
            .AsNoTracking()
            .Include(p => p.Team)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);

        if (playlist is null) return null;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        if (!PlaylistAccess.CanRead(playlist, userId, teamIds)) return null;

        var ownedTeamIds = await TeamService.GetOwnedTeamIdsAsync(db, userId, ct);
        var canDelete = PlaylistAccess.CanDelete(playlist, userId, teamIds, ownedTeamIds);

        var present = db.PlaylistItems
            .AsNoTracking()
            .Where(i => i.PlaylistId == playlistId && i.Track!.IsPresent);

        var playlistTotal = await present.CountAsync(ct);

        // Ticks, not Duration: the mapped column is the one SQLite can add up. SUM over nothing
        // is 0 in SQL, so the empty playlist needs no special case.
        var ticks = playlistTotal == 0 ? 0 : await present.SumAsync(i => i.Track!.DurationTicks, ct);

        // Search the playlist query, not the materialised page. Otherwise page two can contain a
        // match that the search box can never see. Use the same title/artist/album rule as the
        // library search so the same words mean the same thing throughout the app.
        var cleanedQuery = LibraryMatch.Clean(query);
        var matching = present;
        if (cleanedQuery is not null)
        {
            var pattern = LibraryMatch.LikePattern(cleanedQuery);
            matching = matching.Where(i =>
                EF.Functions.Like(i.Track!.Title, pattern)
                || EF.Functions.Like(i.Track!.Artist!.Name, pattern)
                || EF.Functions.Like(i.Track!.Album!.Title, pattern));
        }

        var total = cleanedQuery is null ? playlistTotal : await matching.CountAsync(ct);

        // A skip past the end is what removing the last page's rows leaves behind. Show the last
        // page rather than an empty one.
        if (skip >= total) skip = Math.Max(0, ((Math.Max(1, total) - 1) / take) * take);

        var rows = await matching
            .OrderBy(i => i.SortKey)
            .Skip(skip)
            .Take(take)
            .Select(i => new PlaylistTrackRow(
                i.Id,
                i.TrackId,
                i.Track!.Title,
                i.Track!.ArtistId,
                i.Track!.Artist!.Name,
                i.Track!.AlbumId,
                i.Track!.Album!.Title,
                i.Track!.Album!.Year,
                i.Track!.TrackNumber,
                i.Track!.DiscNumber,
                i.Track!.DurationTicks,
                i.Track!.Bitrate,
                i.AddedAt))
            .ToListAsync(ct);

        return new PlaylistTrackPage(
            playlist.Id,
            playlist.Name,
            playlist.Description,
            playlist.TeamId,
            playlist.Team?.Name,
            canDelete,
            playlistTotal,
            total,
            TimeSpan.FromTicks(ticks),
            skip,
            take,
            rows);
    }

    /// <summary>
    /// Every present track id, in playlist order. This is what "play the whole thing" needs, and
    /// it is deliberately separate from <see cref="GetPageAsync"/>: a queue wants all of the list
    /// and none of the metadata, so it stays one column of GUIDs however long the playlist is.
    /// Null means the playlist is gone or isn't theirs — the same answer <see cref="GetAsync"/>
    /// gives, and for the same reason.
    /// </summary>
    public async Task<List<Guid>?> GetTrackIdsAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);

        if (playlist is null) return null;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        if (!PlaylistAccess.CanRead(playlist, userId, teamIds)) return null;

        return await db.PlaylistItems
            .AsNoTracking()
            .Where(i => i.PlaylistId == playlistId && i.Track!.IsPresent)
            .OrderBy(i => i.SortKey)
            .Select(i => i.TrackId)
            .ToListAsync(ct);
    }

    public async Task<Playlist?> GetAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists
            .AsNoTracking()
            .Include(p => p.Team)
            .Include(p => p.Items.OrderBy(i => i.SortKey))
                .ThenInclude(i => i.Track)
                    .ThenInclude(t => t!.Artist)
            .Include(p => p.Items)
                .ThenInclude(i => i.Track)
                    .ThenInclude(t => t!.Album)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);

        if (playlist is null) return null;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        return PlaylistAccess.CanRead(playlist, userId, teamIds) ? playlist : null;
    }

    /// <summary>
    /// Creates a personal playlist, or a team one when <paramref name="teamId"/> is given.
    /// Creating inside a team requires membership — otherwise anyone could plant a playlist
    /// in a team they can't see.
    /// </summary>
    public async Task<Guid?> CreateAsync(
        Guid userId, string name, Guid? teamId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (teamId is { } id && !await db.TeamMembers.AnyAsync(m => m.TeamId == id && m.UserId == userId, ct))
        {
            log.LogWarning("{User} tried to create a playlist in team {Team} without being a member", userId, id);
            return null;
        }

        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "New playlist" : name.Trim(),
            OwnerUserId = teamId is null ? userId : null,
            TeamId = teamId,
            Visibility = teamId is null ? PlaylistVisibility.Private : PlaylistVisibility.Team,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(ct);
        return playlist.Id;
    }

    public async Task<bool> RenameAsync(Guid playlistId, Guid userId, string name, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null) return false;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        if (!PlaylistAccess.CanEdit(playlist, userId, teamIds)) return false;

        playlist.Name = name.Trim();
        playlist.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Hands a personal playlist to a team: everyone in it can then see and edit it, and
    /// requests into it notify the whole team.
    ///
    /// A move, not a copy. Two divergent copies of the same list is the thing people actually
    /// complain about, and the original owner is in the team anyway — they lose nothing but
    /// exclusivity. Deleting it afterwards becomes an owner's call, which is the point.
    /// </summary>
    public async Task<(bool Ok, string? Error)> ShareWithTeamAsync(
        Guid playlistId, Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null) return (false, "That playlist is gone.");

        if (playlist.TeamId is not null)
        {
            return (false, "That playlist already belongs to a team.");
        }

        if (playlist.OwnerUserId != userId)
        {
            return (false, "Only the owner can share a playlist.");
        }

        if (!await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct))
        {
            return (false, "You're not in that team.");
        }

        playlist.OwnerUserId = null;
        playlist.TeamId = teamId;
        playlist.Visibility = PlaylistVisibility.Team;
        playlist.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        log.LogInformation("{User} shared playlist {Playlist} with team {Team}", userId, playlist.Name, teamId);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null) return false;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        var ownedTeamIds = await TeamService.GetOwnedTeamIdsAsync(db, userId, ct);

        if (!PlaylistAccess.CanDelete(playlist, userId, teamIds, ownedTeamIds))
        {
            log.LogWarning("{User} was refused deletion of playlist {Playlist}", userId, playlistId);
            return false;
        }

        db.Playlists.Remove(playlist);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Appends only tracks not already present, preserving the supplied order.
    /// A transaction keeps simultaneous additions from inserting the same track twice.
    /// </summary>
    public async Task<int> AddTracksAsync(
        Guid playlistId,
        Guid userId,
        IReadOnlyList<Guid> trackIds,
        Guid? requestId = null,
        CancellationToken ct = default)
    {
        if (trackIds.Count == 0) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var playlist = await db.Playlists
            .Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);

        if (playlist is null) return 0;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        if (!PlaylistAccess.CanEdit(playlist, userId, teamIds))
        {
            log.LogWarning("Refused to add tracks to playlist {PlaylistId} for user {UserId}", playlistId, userId);
            return 0;
        }

        var nextKey = playlist.Items.Count == 0 ? SortKeyStep : playlist.Items.Max(i => i.SortKey) + SortKeyStep;
        var added = 0;
        var existing = playlist.Items.Select(i => i.TrackId).ToHashSet();

        foreach (var trackId in trackIds)
        {
            if (!existing.Add(trackId)) continue;

            db.PlaylistItems.Add(new PlaylistItem
            {
                Id = Guid.NewGuid(),
                PlaylistId = playlistId,
                TrackId = trackId,
                SortKey = nextKey,
                AddedByUserId = userId,
                AddedAt = DateTimeOffset.UtcNow,
                RequestId = requestId,
            });

            nextKey += SortKeyStep;
            added++;
        }

        if (added == 0) return 0;
        playlist.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return added;
    }

    public async Task<int?> RemoveDuplicatesAsync(Guid playlistId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var playlist = await db.Playlists.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null) return null;
        var teams = await TeamService.GetTeamIdsAsync(db, userId, ct);
        if (!PlaylistAccess.CanEdit(playlist, userId, teams)) return null;
        var seen = new HashSet<Guid>();
        var duplicates = playlist.Items.OrderBy(i => i.SortKey).ThenBy(i => i.Id)
            .Where(i => !seen.Add(i.TrackId)).ToList();
        if (duplicates.Count > 0)
        {
            db.PlaylistItems.RemoveRange(duplicates);
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return duplicates.Count;
    }

    public async Task<bool> RemoveItemAsync(Guid itemId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var item = await db.PlaylistItems
            .Include(i => i.Playlist)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);

        if (item?.Playlist is null) return false;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        if (!PlaylistAccess.CanEdit(item.Playlist, userId, teamIds)) return false;

        db.PlaylistItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Move an item between two neighbours by taking the midpoint of their sort keys —
    /// one row updated instead of renumbering the list.
    /// </summary>
    public async Task<bool> MoveAsync(
        Guid itemId, Guid userId, double? beforeKey, double? afterKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var item = await db.PlaylistItems
            .Include(i => i.Playlist)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);

        if (item?.Playlist is null) return false;

        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        if (!PlaylistAccess.CanEdit(item.Playlist, userId, teamIds)) return false;

        item.SortKey = (beforeKey, afterKey) switch
        {
            (null, null) => SortKeyStep,
            (null, { } after) => after - SortKeyStep,
            ({ } before, null) => before + SortKeyStep,
            ({ } before, { } after) => (before + after) / 2,
        };

        await db.SaveChangesAsync(ct);

        // Doubles run out of room after ~50 midpoint inserts in the same gap.
        if (beforeKey is { } b && afterKey is { } a && Math.Abs(a - b) < 0.0001)
        {
            await RenumberAsync(item.PlaylistId, ct);
        }

        return true;
    }

    private async Task RenumberAsync(Guid playlistId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var items = await db.PlaylistItems
            .Where(i => i.PlaylistId == playlistId)
            .OrderBy(i => i.SortKey)
            .ToListAsync(ct);

        var key = SortKeyStep;
        foreach (var item in items)
        {
            item.SortKey = key;
            key += SortKeyStep;
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Renumbered sort keys for playlist {PlaylistId}", playlistId);
    }
}
