using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Services.Admin;

public sealed record AdminUserRow(
    Guid Id,
    string DisplayName,
    bool IsAdmin,
    bool IsBanned,
    string? BanReason,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    int PlaylistCount,
    int TeamCount,
    int RequestCount);

public sealed record AdminTeamRow(
    Guid Id,
    string Name,
    string? Description,
    TeamJoinPolicy JoinPolicy,
    int MemberCount,
    int PlaylistCount,
    int PendingCount,
    string Owners);

public sealed record AdminPlaylistRow(
    Guid Id,
    string Name,
    string? OwnerName,
    Guid? TeamId,
    string? TeamName,
    int TrackCount,
    DateTimeOffset UpdatedAt);

public sealed record AdminStats(
    int Users, int BannedUsers, int Teams, int Playlists, int Tracks, int Artists, int OpenRequests);

/// <summary>
/// The management panel's operations. Every method re-checks that the caller is an admin
/// against the database — the [Authorize] attribute on the page is a convenience for the UI,
/// not the security boundary, and these are the calls that can wreck somebody's library.
/// </summary>
public sealed class AdminService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    ILogger<AdminService> log)
{
    public async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await IsAdminAsync(db, userId, ct);
    }

    private static Task<bool> IsAdminAsync(MootifyDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.IsAdmin && !u.IsBanned, ct);

    // ---- overview --------------------------------------------------------

    public async Task<AdminStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return new AdminStats(
            await db.Users.CountAsync(ct),
            await db.Users.CountAsync(u => u.IsBanned, ct),
            await db.Teams.CountAsync(ct),
            await db.Playlists.CountAsync(ct),
            await db.Tracks.CountAsync(t => t.IsPresent, ct),
            await db.Artists.CountAsync(ct),
            await db.Requests.CountAsync(
                r => r.Status != RequestStatus.Available
                  && r.Status != RequestStatus.NotFound
                  && r.Status != RequestStatus.Failed, ct));
    }

    // ---- users -----------------------------------------------------------

    public async Task<List<AdminUserRow>> GetUsersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Users
            .AsNoTracking()
            .OrderByDescending(u => u.IsAdmin)
            .ThenBy(u => u.DisplayName)
            .Select(u => new AdminUserRow(
                u.Id,
                u.DisplayName,
                u.IsAdmin,
                u.IsBanned,
                u.BanReason,
                u.MustChangePassword,
                u.CreatedAt,
                u.LastSeenAt,
                db.Playlists.Count(p => p.OwnerUserId == u.Id),
                db.TeamMembers.Count(m => m.UserId == u.Id),
                db.Requests.Count(r => r.RequesterId == u.Id)))
            .ToListAsync(ct);
    }

    public async Task<(bool Ok, string? Error)> SetBannedAsync(
        Guid actingUserId, Guid targetUserId, bool banned, string? reason = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        if (actingUserId == targetUserId)
        {
            return (false, "You can't ban yourself.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (user is null) return (false, "No such account.");

        // Locking every admin out of the panel would leave nobody able to unlock it.
        if (banned && user.IsAdmin && !await AnotherActiveAdminExistsAsync(db, targetUserId, ct))
        {
            return (false, "That's the last active admin.");
        }

        user.IsBanned = banned;
        user.BanReason = banned ? (string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()) : null;
        user.BannedAt = banned ? DateTimeOffset.UtcNow : null;

        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} {Action} {User}", actingUserId, banned ? "banned" : "unbanned", user.DisplayName);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SetAdminAsync(
        Guid actingUserId, Guid targetUserId, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (user is null) return (false, "No such account.");

        if (!isAdmin && !await AnotherActiveAdminExistsAsync(db, targetUserId, ct))
        {
            return (false, "That's the last admin — promote somebody else first.");
        }

        user.IsAdmin = isAdmin;
        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} made {User} {Role}", actingUserId, user.DisplayName, isAdmin ? "an admin" : "a normal user");
        return (true, null);
    }

    /// <summary>
    /// Deletes the account and everything cascading off it: their playlists, memberships,
    /// requests, notifications, history. Team playlists they merely contributed to survive,
    /// because those belong to the team.
    /// </summary>
    public async Task<(bool Ok, string? Error)> DeleteUserAsync(
        Guid actingUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        if (actingUserId == targetUserId)
        {
            return (false, "You can't delete your own account from here.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct);
        if (user is null) return (true, null);

        if (user.IsAdmin && !await AnotherActiveAdminExistsAsync(db, targetUserId, ct))
        {
            return (false, "That's the last active admin.");
        }

        // Teams they solely own would be left ownerless and unmanageable.
        var orphanedTeams = await db.TeamMembers
            .Where(m => m.UserId == targetUserId && m.Role == TeamRole.Owner)
            .Select(m => m.TeamId)
            .Where(teamId => !db.TeamMembers.Any(o => o.TeamId == teamId && o.Role == TeamRole.Owner && o.UserId != targetUserId))
            .Join(db.Teams, id => id, t => t.Id, (_, t) => t.Name)
            .ToListAsync(ct);

        if (orphanedTeams.Count > 0)
        {
            return (false,
                $"They're the only owner of {string.Join(", ", orphanedTeams)}. " +
                "Promote somebody there, or delete those teams first.");
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} deleted account {User}", actingUserId, user.DisplayName);
        return (true, null);
    }

    private static Task<bool> AnotherActiveAdminExistsAsync(MootifyDbContext db, Guid excludingUserId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.IsAdmin && !u.IsBanned && u.Id != excludingUserId, ct);

    // ---- teams -----------------------------------------------------------

    public async Task<List<AdminTeamRow>> GetTeamsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.Teams
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.Description,
                t.JoinPolicy,
                MemberCount = t.Members.Count,
                PlaylistCount = t.Playlists.Count,
                PendingCount = t.PendingRequests.Count,
                Owners = t.Members
                    .Where(m => m.Role == TeamRole.Owner)
                    .Select(m => m.User!.DisplayName)
                    .ToList(),
            })
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new AdminTeamRow(
                r.Id, r.Name, r.Description, r.JoinPolicy, r.MemberCount, r.PlaylistCount, r.PendingCount,
                r.Owners.Count == 0 ? "nobody" : string.Join(", ", r.Owners)))
        ];
    }

    public async Task<(bool Ok, string? Error)> UpdateTeamAsync(
        Guid actingUserId,
        Guid teamId,
        string name,
        string? description,
        TeamJoinPolicy policy,
        CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 128) return (false, "Names run 1 to 128 characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        if (await db.Teams.AnyAsync(t => t.Name == name && t.Id != teamId, ct))
        {
            return (false, $"There's already a team called \"{name}\".");
        }

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null) return (false, "That team is gone.");

        team.Name = name;
        team.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        team.JoinPolicy = policy;

        await db.SaveChangesAsync(ct);
        log.LogInformation("Admin {Admin} edited team {Team}", actingUserId, name);
        return (true, null);
    }

    /// <summary>Straight in, no invite and no application. That's the point of it.</summary>
    public async Task<(bool Ok, string? Error)> ForceAddMemberAsync(
        Guid actingUserId, Guid teamId, Guid targetUserId, TeamRole role = TeamRole.Member, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        if (!await db.Teams.AnyAsync(t => t.Id == teamId, ct)) return (false, "That team is gone.");
        if (!await db.Users.AnyAsync(u => u.Id == targetUserId, ct)) return (false, "No such account.");

        if (await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == targetUserId, ct))
        {
            return (false, "They're already in the team.");
        }

        db.TeamMembers.Add(new TeamMember
        {
            TeamId = teamId,
            UserId = targetUserId,
            Role = role,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        // Any invite or application they had is now moot.
        var pending = await db.TeamMembershipRequests
            .Where(r => r.TeamId == teamId && r.UserId == targetUserId)
            .ToListAsync(ct);
        db.TeamMembershipRequests.RemoveRange(pending);

        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} force-added {User} to team {Team}", actingUserId, targetUserId, teamId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> ForceRemoveMemberAsync(
        Guid actingUserId, Guid teamId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        var membership = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == targetUserId, ct);

        if (membership is null) return (true, null);

        if (membership.Role == TeamRole.Owner
            && !await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.Role == TeamRole.Owner && m.UserId != targetUserId, ct))
        {
            return (false, "That's the team's last owner. Promote somebody, or delete the team.");
        }

        db.TeamMembers.Remove(membership);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SetTeamRoleAsync(
        Guid actingUserId, Guid teamId, Guid targetUserId, TeamRole role, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        var membership = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == targetUserId, ct);

        if (membership is null) return (false, "They're not in that team.");

        if (role == TeamRole.Member
            && !await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.Role == TeamRole.Owner && m.UserId != targetUserId, ct))
        {
            return (false, "That's the team's last owner.");
        }

        membership.Role = role;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteTeamAsync(
        Guid actingUserId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null) return (true, null);

        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} deleted team {Team} and its playlists", actingUserId, team.Name);
        return (true, null);
    }

    // ---- playlists -------------------------------------------------------

    public async Task<List<AdminPlaylistRow>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Playlists
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new AdminPlaylistRow(
                p.Id,
                p.Name,
                p.Owner!.DisplayName,
                p.TeamId,
                p.Team!.Name,
                p.Items.Count,
                p.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<(bool Ok, string? Error)> RenamePlaylistAsync(
        Guid actingUserId, Guid playlistId, string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 256) return (false, "Names run 1 to 256 characters.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null) return (false, "That playlist is gone.");

        playlist.Name = name;
        playlist.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    /// <summary>Deletes any playlist regardless of who owns it. Tracks are untouched.</summary>
    public async Task<(bool Ok, string? Error)> DeletePlaylistAsync(
        Guid actingUserId, Guid playlistId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (false, "Admins only.");

        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null) return (true, null);

        db.Playlists.Remove(playlist);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Admin {Admin} deleted playlist {Playlist}", actingUserId, playlist.Name);
        return (true, null);
    }

    /// <summary>
    /// Drops playlist entries whose track has vanished from disk. The everyday mess that
    /// accumulates when files are reorganised on disk.
    /// </summary>
    public async Task<(int Removed, string? Error)> PruneMissingTracksAsync(
        Guid actingUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsAdminAsync(db, actingUserId, ct)) return (0, "Admins only.");

        var removed = await db.PlaylistItems
            .Where(i => !i.Track!.IsPresent)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
        {
            log.LogInformation("Admin {Admin} pruned {Count} playlist entries with missing files", actingUserId, removed);
        }

        return (removed, null);
    }

    // ---- users for pickers ----------------------------------------------

    public async Task<List<(Guid Id, string DisplayName)>> GetUserOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.Users
            .AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(ct);

        return [.. rows.Select(r => (r.Id, r.DisplayName))];
    }
}
