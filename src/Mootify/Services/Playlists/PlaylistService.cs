using Microsoft.EntityFrameworkCore;
using Mootify.Data;
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
    /// Appends tracks. Idempotent when <paramref name="requestId"/> is set: the Lidarr webhook
    /// and the reconciliation poller will both fire for the same request eventually.
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

        foreach (var trackId in trackIds)
        {
            // Re-adding by hand is allowed (people do want a track twice); re-adding from the
            // same request is not.
            if (requestId is not null && playlist.Items.Any(i => i.TrackId == trackId && i.RequestId == requestId))
            {
                continue;
            }

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

        playlist.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return added;
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
