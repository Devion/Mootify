using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Notifications;

namespace Mootify.Services.Teams;

public sealed record TeamSummary(
    Guid Id,
    string Name,
    string? Description,
    TeamJoinPolicy JoinPolicy,
    int MemberCount,
    int PlaylistCount,
    bool IsMember,
    bool IsOwner,
    int PendingApplicationCount,
    bool HasPendingInviteForMe,
    bool HasPendingApplicationFromMe);

public sealed record TeamMemberInfo(Guid UserId, string DisplayName, TeamRole Role, DateTimeOffset JoinedAt);

public sealed record PendingRequestInfo(
    Guid Id,
    Guid TeamId,
    string TeamName,
    Guid UserId,
    string DisplayName,
    MembershipRequestKind Kind,
    string? Message,
    DateTimeOffset CreatedAt);

public sealed record UserOption(Guid Id, string DisplayName);

/// <summary>
/// Teams, membership, and the two ways in: an owner invites you, or you apply and an owner
/// accepts. Which of those is available is the team's <see cref="TeamJoinPolicy"/>.
/// </summary>
public sealed class TeamService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    NotificationDispatcher notifications,
    ILogger<TeamService> log)
{
    /// <summary>
    /// The team ids a user belongs to. Loaded once per operation and handed to
    /// <see cref="Playlists.PlaylistAccess"/> so the authorization rules stay pure.
    /// </summary>
    public async Task<HashSet<Guid>> GetTeamIdsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await GetTeamIdsAsync(db, userId, ct);
    }

    public static async Task<HashSet<Guid>> GetTeamIdsAsync(
        MootifyDbContext db, Guid userId, CancellationToken ct = default) =>
        [.. await db.TeamMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.TeamId)
            .ToListAsync(ct)];

    public static async Task<HashSet<Guid>> GetOwnedTeamIdsAsync(
        MootifyDbContext db, Guid userId, CancellationToken ct = default) =>
        [.. await db.TeamMembers
            .Where(m => m.UserId == userId && m.Role == TeamRole.Owner)
            .Select(m => m.TeamId)
            .ToListAsync(ct)];

    // ---- reads -----------------------------------------------------------

    private static IQueryable<TeamSummary> Project(IQueryable<Team> teams, Guid userId) =>
        teams.Select(t => new TeamSummary(
            t.Id,
            t.Name,
            t.Description,
            t.JoinPolicy,
            t.Members.Count,
            t.Playlists.Count,
            t.Members.Any(m => m.UserId == userId),
            t.Members.Any(m => m.UserId == userId && m.Role == TeamRole.Owner),
            t.PendingRequests.Count(r => r.Kind == MembershipRequestKind.Application),
            t.PendingRequests.Any(r => r.UserId == userId && r.Kind == MembershipRequestKind.Invite),
            t.PendingRequests.Any(r => r.UserId == userId && r.Kind == MembershipRequestKind.Application)));

    public async Task<List<TeamSummary>> GetAllAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Project(db.Teams.AsNoTracking().OrderBy(t => t.Name), userId).ToListAsync(ct);
    }

    public async Task<List<TeamSummary>> GetForUserAsync(Guid userId, CancellationToken ct = default) =>
        [.. (await GetAllAsync(userId, ct)).Where(t => t.IsMember)];

    public async Task<TeamSummary?> GetAsync(Guid teamId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Project(db.Teams.AsNoTracking().Where(t => t.Id == teamId), userId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<TeamMemberInfo>> GetMembersAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TeamMembers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .OrderByDescending(m => m.Role)
            .ThenBy(m => m.User!.DisplayName)
            .Select(m => new TeamMemberInfo(m.UserId, m.User!.DisplayName, m.Role, m.JoinedAt))
            .ToListAsync(ct);
    }

    /// <summary>Everything waiting on this team's owners, plus invites they've sent.</summary>
    public async Task<List<PendingRequestInfo>> GetPendingForTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TeamMembershipRequests
            .AsNoTracking()
            .Where(r => r.TeamId == teamId)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new PendingRequestInfo(
                r.Id, r.TeamId, r.Team!.Name, r.UserId, r.User!.DisplayName, r.Kind, r.Message, r.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Invites waiting on this user to accept.</summary>
    public async Task<List<PendingRequestInfo>> GetInvitesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TeamMembershipRequests
            .AsNoTracking()
            .Where(r => r.UserId == userId && r.Kind == MembershipRequestKind.Invite)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new PendingRequestInfo(
                r.Id, r.TeamId, r.Team!.Name, r.UserId, r.User!.DisplayName, r.Kind, r.Message, r.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Signed-up users who are neither members nor already spoken for.</summary>
    public async Task<List<UserOption>> GetInvitableUsersAsync(Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Users
            .AsNoTracking()
            .Where(u => !db.TeamMembers.Any(m => m.TeamId == teamId && m.UserId == u.Id)
                     && !db.TeamMembershipRequests.Any(r => r.TeamId == teamId && r.UserId == u.Id))
            .OrderBy(u => u.DisplayName)
            .Select(u => new UserOption(u.Id, u.DisplayName))
            .ToListAsync(ct);
    }

    // ---- team lifecycle --------------------------------------------------

    public async Task<(Guid? TeamId, string? Error)> CreateAsync(
        Guid userId, string name, string? description = null, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 128)
        {
            return (null, "Give the team a name between 1 and 128 characters.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.Teams.AnyAsync(t => t.Name == name, ct))
        {
            return (null, $"There's already a team called \"{name}\".");
        }

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Teams.Add(team);

        // Whoever creates it is its first owner, so a team is never ownerless.
        db.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = userId,
            Role = TeamRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        log.LogInformation("{User} created team {Team}", userId, name);

        return (team.Id, null);
    }

    public async Task<(bool Ok, string? Error)> UpdateAsync(
        Guid actingUserId,
        Guid teamId,
        string name,
        string? description,
        TeamJoinPolicy joinPolicy,
        CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 128)
        {
            return (false, "Give the team a name between 1 and 128 characters.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsOwnerAsync(db, actingUserId, teamId, ct))
        {
            return (false, "Only a team owner can change this.");
        }

        if (await db.Teams.AnyAsync(t => t.Name == name && t.Id != teamId, ct))
        {
            return (false, $"There's already a team called \"{name}\".");
        }

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null) return (false, "That team is gone.");

        var wasClosed = team.JoinPolicy != TeamJoinPolicy.Open;

        team.Name = name;
        team.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        team.JoinPolicy = joinPolicy;

        // Opening the doors makes every queued application moot — accept them rather than
        // leaving a stack of pending rows nobody will ever look at again.
        if (wasClosed && joinPolicy == TeamJoinPolicy.Open)
        {
            var applications = await db.TeamMembershipRequests
                .Where(r => r.TeamId == teamId && r.Kind == MembershipRequestKind.Application)
                .ToListAsync(ct);

            foreach (var application in applications)
            {
                db.TeamMembers.Add(new TeamMember
                {
                    TeamId = teamId,
                    UserId = application.UserId,
                    Role = TeamRole.Member,
                    JoinedAt = DateTimeOffset.UtcNow,
                });
                db.TeamMembershipRequests.Remove(application);
            }

            if (applications.Count > 0)
            {
                log.LogInformation(
                    "Team {Team} opened; auto-accepted {Count} waiting application(s)", team.Name, applications.Count);
            }
        }

        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsOwnerAsync(db, userId, teamId, ct))
        {
            log.LogWarning("{User} tried to delete team {Team} without owning it", userId, teamId);
            return false;
        }

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null) return false;

        db.Teams.Remove(team);
        await db.SaveChangesAsync(ct);
        log.LogInformation("{User} deleted team {Team}", userId, team.Name);
        return true;
    }

    // ---- getting in ------------------------------------------------------

    /// <summary>
    /// What pressing the join button does, which depends on the policy: joins outright,
    /// files an application, or is refused because the team is invite-only.
    /// </summary>
    public async Task<(bool Joined, bool Applied, string? Error)> RequestJoinAsync(
        Guid userId, Guid teamId, string? message = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null) return (false, false, "That team is gone.");

        if (await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct))
        {
            return (true, false, null);
        }

        // An outstanding invite means the owner already said yes — accept it instead of
        // making them approve the same person twice.
        var existing = await db.TeamMembershipRequests
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == userId, ct);

        if (existing is { Kind: MembershipRequestKind.Invite })
        {
            await AcceptCoreAsync(db, existing, ct);
            return (true, false, null);
        }

        if (existing is { Kind: MembershipRequestKind.Application })
        {
            return (false, true, null);
        }

        switch (team.JoinPolicy)
        {
            case TeamJoinPolicy.Open:
                db.TeamMembers.Add(new TeamMember
                {
                    TeamId = teamId,
                    UserId = userId,
                    Role = TeamRole.Member,
                    JoinedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                return (true, false, null);

            case TeamJoinPolicy.RequestToJoin:
                db.TeamMembershipRequests.Add(new TeamMembershipRequest
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    UserId = userId,
                    Kind = MembershipRequestKind.Application,
                    CreatedByUserId = userId,
                    Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);

                await NotifyOwnersAsync(db, teamId, userId, team.Name, ct);
                return (false, true, null);

            default:
                return (false, false, "That team is invite only.");
        }
    }

    public async Task<(bool Ok, string? Error)> InviteAsync(
        Guid actingUserId, Guid teamId, Guid inviteeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsOwnerAsync(db, actingUserId, teamId, ct))
        {
            return (false, "Only a team owner can invite people.");
        }

        var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team is null) return (false, "That team is gone.");

        if (!await db.Users.AnyAsync(u => u.Id == inviteeId, ct))
        {
            return (false, "That person doesn't have an account yet.");
        }

        if (await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == inviteeId, ct))
        {
            return (false, "They're already in the team.");
        }

        var existing = await db.TeamMembershipRequests
            .FirstOrDefaultAsync(r => r.TeamId == teamId && r.UserId == inviteeId, ct);

        // They already asked to join — inviting them is the owner saying yes, so just do it.
        if (existing is { Kind: MembershipRequestKind.Application })
        {
            await AcceptCoreAsync(db, existing, ct);
            await NotifyAcceptedAsync(inviteeId, teamId, team.Name, ct);
            return (true, null);
        }

        if (existing is not null)
        {
            return (false, "They've already been invited.");
        }

        db.TeamMembershipRequests.Add(new TeamMembershipRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            UserId = inviteeId,
            Kind = MembershipRequestKind.Invite,
            CreatedByUserId = actingUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);

        await notifications.PublishAsync(
            inviteeId,
            NotificationType.TeamInvite,
            $"You've been invited to {team.Name}",
            "Open Teams to accept or decline.",
            "/teams",
            ct: ct);

        return (true, null);
    }

    /// <summary>
    /// Accepts a pending row. An invite is the invitee's to accept; an application is an
    /// owner's. Anyone else gets refused.
    /// </summary>
    public async Task<(bool Ok, string? Error)> AcceptAsync(
        Guid actingUserId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var request = await db.TeamMembershipRequests
            .Include(r => r.Team)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        if (request is null) return (false, "That's already been dealt with.");

        var allowed = request.Kind == MembershipRequestKind.Invite
            ? request.UserId == actingUserId
            : await IsOwnerAsync(db, actingUserId, request.TeamId, ct);

        if (!allowed)
        {
            log.LogWarning("{User} tried to accept membership request {Request}", actingUserId, requestId);
            return (false, "That isn't yours to accept.");
        }

        var teamName = request.Team?.Name ?? "the team";
        var joinedUserId = request.UserId;
        var teamId = request.TeamId;

        await AcceptCoreAsync(db, request, ct);

        // Tell the applicant they're in. An invitee already knows — they just clicked accept.
        if (request.Kind == MembershipRequestKind.Application)
        {
            await NotifyAcceptedAsync(joinedUserId, teamId, teamName, ct);
        }

        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeclineAsync(
        Guid actingUserId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var request = await db.TeamMembershipRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null) return (true, null);

        // Either side may withdraw: the recipient declines, the sender cancels.
        var allowed = request.UserId == actingUserId
                   || request.CreatedByUserId == actingUserId
                   || await IsOwnerAsync(db, actingUserId, request.TeamId, ct);

        if (!allowed) return (false, "That isn't yours to decline.");

        db.TeamMembershipRequests.Remove(request);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    private static async Task AcceptCoreAsync(
        MootifyDbContext db, TeamMembershipRequest request, CancellationToken ct)
    {
        db.TeamMembers.Add(new TeamMember
        {
            TeamId = request.TeamId,
            UserId = request.UserId,
            Role = TeamRole.Member,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        db.TeamMembershipRequests.Remove(request);
        await db.SaveChangesAsync(ct);
    }

    // ---- membership management -------------------------------------------

    public async Task<(bool Ok, string? Error)> LeaveAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await RemoveMemberCoreAsync(db, userId, teamId, ct);
    }

    public async Task<(bool Ok, string? Error)> RemoveMemberAsync(
        Guid actingUserId, Guid teamId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsOwnerAsync(db, actingUserId, teamId, ct))
        {
            return (false, "Only a team owner can remove people.");
        }

        return await RemoveMemberCoreAsync(db, targetUserId, teamId, ct);
    }

    private static async Task<(bool Ok, string? Error)> RemoveMemberCoreAsync(
        MootifyDbContext db, Guid userId, Guid teamId, CancellationToken ct)
    {
        var membership = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == userId, ct);

        if (membership is null) return (true, null);

        if (membership.Role == TeamRole.Owner)
        {
            var otherOwners = await db.TeamMembers
                .CountAsync(m => m.TeamId == teamId && m.Role == TeamRole.Owner && m.UserId != userId, ct);

            if (otherOwners == 0)
            {
                // An ownerless team can never be renamed or deleted again, and its playlists
                // would be undeletable. Promote someone or delete the team instead.
                return (false, "That's the last owner. Promote somebody else first, or delete the team.");
            }
        }

        db.TeamMembers.Remove(membership);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<bool> PromoteAsync(
        Guid actingUserId, Guid teamId, Guid targetUserId, CancellationToken ct = default) =>
        await SetRoleAsync(actingUserId, teamId, targetUserId, TeamRole.Owner, ct);

    public async Task<(bool Ok, string? Error)> DemoteAsync(
        Guid actingUserId, Guid teamId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsOwnerAsync(db, actingUserId, teamId, ct))
        {
            return (false, "Only a team owner can change roles.");
        }

        var otherOwners = await db.TeamMembers
            .CountAsync(m => m.TeamId == teamId && m.Role == TeamRole.Owner && m.UserId != targetUserId, ct);

        if (otherOwners == 0)
        {
            return (false, "That's the last owner — promote somebody else first.");
        }

        var target = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == targetUserId, ct);

        if (target is null) return (false, "They're not in this team.");

        target.Role = TeamRole.Member;
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    private async Task<bool> SetRoleAsync(
        Guid actingUserId, Guid teamId, Guid targetUserId, TeamRole role, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await IsOwnerAsync(db, actingUserId, teamId, ct)) return false;

        var target = await db.TeamMembers
            .FirstOrDefaultAsync(m => m.TeamId == teamId && m.UserId == targetUserId, ct);

        if (target is null) return false;

        target.Role = role;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> IsMemberAsync(Guid userId, Guid teamId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);
    }

    /// <summary>Everyone in the team except one person — used to notify the rest on a completed request.</summary>
    public async Task<List<Guid>> GetMemberIdsExceptAsync(
        Guid teamId, Guid exceptUserId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TeamMembers
            .Where(m => m.TeamId == teamId && m.UserId != exceptUserId)
            .Select(m => m.UserId)
            .ToListAsync(ct);
    }

    // ---- helpers ---------------------------------------------------------

    private async Task NotifyOwnersAsync(
        MootifyDbContext db, Guid teamId, Guid applicantId, string teamName, CancellationToken ct)
    {
        var applicant = await db.Users
            .Where(u => u.Id == applicantId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "Somebody";

        var owners = await db.TeamMembers
            .Where(m => m.TeamId == teamId && m.Role == TeamRole.Owner)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        foreach (var ownerId in owners)
        {
            await notifications.PublishAsync(
                ownerId,
                NotificationType.TeamJoinRequest,
                $"{applicant} wants to join {teamName}",
                "Accept or decline on the team's management page.",
                $"/team/{teamId}/manage",
                ct: ct);
        }
    }

    private Task NotifyAcceptedAsync(Guid userId, Guid teamId, string teamName, CancellationToken ct) =>
        notifications.PublishAsync(
            userId,
            NotificationType.TeamAccepted,
            $"You're in {teamName}",
            "Its playlists are in your sidebar now.",
            $"/team/{teamId}",
            ct: ct);

    private static Task<bool> IsOwnerAsync(MootifyDbContext db, Guid userId, Guid teamId, CancellationToken ct) =>
        db.TeamMembers.AnyAsync(m => m.TeamId == teamId && m.UserId == userId && m.Role == TeamRole.Owner, ct);
}
