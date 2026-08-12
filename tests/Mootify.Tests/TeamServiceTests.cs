using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Notifications;
using Mootify.Services.Teams;

namespace Mootify.Tests;

public sealed class TeamServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private TeamService _teams = null!;
    private NotificationDispatcher _notifications = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _notifications = new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance);
        _teams = new TeamService(_db, _notifications, NullLogger<TeamService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<Guid> NewTeamAsync(Guid ownerId, string name = "Kitchen")
    {
        var (id, error) = await _teams.CreateAsync(ownerId, name);
        Assert.Null(error);
        return id!.Value;
    }

    private async Task SetPolicyAsync(Guid ownerId, Guid teamId, TeamJoinPolicy policy)
    {
        var team = await _teams.GetAsync(teamId, ownerId);
        var (ok, error) = await _teams.UpdateAsync(ownerId, teamId, team!.Name, team.Description, policy);
        Assert.True(ok, error);
    }

    // ---- lifecycle -------------------------------------------------------

    [Fact]
    public async Task Creating_a_team_makes_you_its_owner()
    {
        // A team is never ownerless — otherwise nobody could ever rename or delete it.
        var user = await _db.AddUserAsync("devion");
        var teamId = await NewTeamAsync(user.Id);

        var member = Assert.Single(await _teams.GetMembersAsync(teamId));
        Assert.Equal(TeamRole.Owner, member.Role);
        Assert.Equal("devion", member.DisplayName);
    }

    [Fact]
    public async Task New_teams_ask_before_letting_people_in()
    {
        var user = await _db.AddUserAsync("devion");
        var teamId = await NewTeamAsync(user.Id);

        var team = await _teams.GetAsync(teamId, user.Id);

        Assert.Equal(TeamJoinPolicy.RequestToJoin, team!.JoinPolicy);
    }

    [Fact]
    public async Task Team_names_are_unique_on_create_and_on_rename()
    {
        var a = await _db.AddUserAsync("devion");
        var b = await _db.AddUserAsync("housemate");
        await NewTeamAsync(a.Id, "Kitchen");
        var second = await NewTeamAsync(b.Id, "Garage");

        var (id, createError) = await _teams.CreateAsync(b.Id, "Kitchen");
        Assert.Null(id);
        Assert.NotNull(createError);

        var (ok, renameError) = await _teams.UpdateAsync(b.Id, second, "Kitchen", null, TeamJoinPolicy.Open);
        Assert.False(ok);
        Assert.NotNull(renameError);
    }

    [Fact]
    public async Task Only_an_owner_can_change_the_team()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        var (ok, error) = await _teams.UpdateAsync(mate.Id, teamId, "Hijacked", null, TeamJoinPolicy.Open);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal("Kitchen", (await _teams.GetAsync(teamId, owner.Id))!.Name);
    }

    // ---- open policy -----------------------------------------------------

    [Fact]
    public async Task An_open_team_lets_you_in_immediately()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await SetPolicyAsync(owner.Id, teamId, TeamJoinPolicy.Open);

        var (joined, applied, error) = await _teams.RequestJoinAsync(mate.Id, teamId);

        Assert.True(joined);
        Assert.False(applied);
        Assert.Null(error);
        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
    }

    [Fact]
    public async Task Joining_twice_is_harmless()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await SetPolicyAsync(owner.Id, teamId, TeamJoinPolicy.Open);

        await _teams.RequestJoinAsync(mate.Id, teamId);
        await _teams.RequestJoinAsync(mate.Id, teamId);

        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
    }

    // ---- applications ----------------------------------------------------

    [Fact]
    public async Task Asking_to_join_waits_for_an_owner_and_tells_them()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);

        var (joined, applied, _) = await _teams.RequestJoinAsync(mate.Id, teamId, "let me in");

        Assert.False(joined);
        Assert.True(applied);
        Assert.Single(await _teams.GetMembersAsync(teamId));

        // The owner has to find out somehow.
        Assert.Equal(1, await _notifications.GetUnreadCountAsync(owner.Id));

        var pending = Assert.Single(await _teams.GetPendingForTeamAsync(teamId));
        Assert.Equal(MembershipRequestKind.Application, pending.Kind);
        Assert.Equal("let me in", pending.Message);
    }

    [Fact]
    public async Task An_owner_accepting_an_application_lets_them_in_and_tells_them()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _teams.RequestJoinAsync(mate.Id, teamId);
        var pending = Assert.Single(await _teams.GetPendingForTeamAsync(teamId));

        var (ok, error) = await _teams.AcceptAsync(owner.Id, pending.Id);

        Assert.True(ok, error);
        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
        Assert.Empty(await _teams.GetPendingForTeamAsync(teamId));
        Assert.Equal(1, await _notifications.GetUnreadCountAsync(mate.Id));
    }

    [Fact]
    public async Task A_stranger_cannot_accept_somebody_elses_application()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var outsider = await _db.AddUserAsync("nosy");
        var teamId = await NewTeamAsync(owner.Id);
        await _teams.RequestJoinAsync(mate.Id, teamId);
        var pending = Assert.Single(await _teams.GetPendingForTeamAsync(teamId));

        // Not an owner, and not the applicant either.
        var (ok, _) = await _teams.AcceptAsync(outsider.Id, pending.Id);
        Assert.False(ok);

        // Nor can the applicant wave themselves through.
        var (selfOk, _) = await _teams.AcceptAsync(mate.Id, pending.Id);
        Assert.False(selfOk);

        Assert.Single(await _teams.GetMembersAsync(teamId));
    }

    [Fact]
    public async Task Declining_an_application_removes_it_and_lets_them_ask_again()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _teams.RequestJoinAsync(mate.Id, teamId);
        var pending = Assert.Single(await _teams.GetPendingForTeamAsync(teamId));

        await _teams.DeclineAsync(owner.Id, pending.Id);

        Assert.Empty(await _teams.GetPendingForTeamAsync(teamId));
        Assert.Single(await _teams.GetMembersAsync(teamId));

        // Resolved rows are deleted, so a second attempt isn't blocked by the first.
        var (_, applied, _) = await _teams.RequestJoinAsync(mate.Id, teamId);
        Assert.True(applied);
    }

    [Fact]
    public async Task Opening_a_team_accepts_everyone_already_waiting()
    {
        // Otherwise the applications sit there forever, invisible and pointless.
        var owner = await _db.AddUserAsync("devion");
        var a = await _db.AddUserAsync("a");
        var b = await _db.AddUserAsync("b");
        var teamId = await NewTeamAsync(owner.Id);
        await _teams.RequestJoinAsync(a.Id, teamId);
        await _teams.RequestJoinAsync(b.Id, teamId);

        await SetPolicyAsync(owner.Id, teamId, TeamJoinPolicy.Open);

        Assert.Equal(3, (await _teams.GetMembersAsync(teamId)).Count);
        Assert.Empty(await _teams.GetPendingForTeamAsync(teamId));
    }

    // ---- invites ---------------------------------------------------------

    [Fact]
    public async Task An_invite_waits_for_the_invitee_and_tells_them()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);

        var (ok, error) = await _teams.InviteAsync(owner.Id, teamId, mate.Id);

        Assert.True(ok, error);
        Assert.Single(await _teams.GetMembersAsync(teamId));
        Assert.Equal(1, await _notifications.GetUnreadCountAsync(mate.Id));

        var invite = Assert.Single(await _teams.GetInvitesForUserAsync(mate.Id));
        Assert.Equal("Kitchen", invite.TeamName);
    }

    [Fact]
    public async Task Accepting_an_invite_is_the_invitees_call_alone()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var outsider = await _db.AddUserAsync("nosy");
        var teamId = await NewTeamAsync(owner.Id);
        await _teams.InviteAsync(owner.Id, teamId, mate.Id);
        var invite = Assert.Single(await _teams.GetInvitesForUserAsync(mate.Id));

        Assert.False((await _teams.AcceptAsync(outsider.Id, invite.Id)).Ok);
        // Not even the owner who sent it can accept on their behalf.
        Assert.False((await _teams.AcceptAsync(owner.Id, invite.Id)).Ok);

        Assert.True((await _teams.AcceptAsync(mate.Id, invite.Id)).Ok);
        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
    }

    [Fact]
    public async Task An_invite_gets_you_into_an_invite_only_team()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await SetPolicyAsync(owner.Id, teamId, TeamJoinPolicy.InviteOnly);

        // The front door is shut.
        var (joined, applied, error) = await _teams.RequestJoinAsync(mate.Id, teamId);
        Assert.False(joined);
        Assert.False(applied);
        Assert.NotNull(error);

        await _teams.InviteAsync(owner.Id, teamId, mate.Id);
        var invite = Assert.Single(await _teams.GetInvitesForUserAsync(mate.Id));
        await _teams.AcceptAsync(mate.Id, invite.Id);

        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
    }

    [Fact]
    public async Task An_invite_and_an_application_crossing_resolve_to_membership()
    {
        // Both sides said yes; making either of them click again would be silly.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);

        await _teams.RequestJoinAsync(mate.Id, teamId);   // user asks
        await _teams.InviteAsync(owner.Id, teamId, mate.Id); // owner independently invites

        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
        Assert.Empty(await _teams.GetPendingForTeamAsync(teamId));
    }

    [Fact]
    public async Task Joining_when_already_invited_just_accepts_the_invite()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _teams.InviteAsync(owner.Id, teamId, mate.Id);

        var (joined, applied, _) = await _teams.RequestJoinAsync(mate.Id, teamId);

        Assert.True(joined);
        Assert.False(applied);
        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
    }

    [Fact]
    public async Task Only_an_owner_can_invite_and_not_twice()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var outsider = await _db.AddUserAsync("nosy");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        Assert.False((await _teams.InviteAsync(mate.Id, teamId, outsider.Id)).Ok);
        Assert.True((await _teams.InviteAsync(owner.Id, teamId, outsider.Id)).Ok);
        Assert.False((await _teams.InviteAsync(owner.Id, teamId, outsider.Id)).Ok);
        Assert.False((await _teams.InviteAsync(owner.Id, teamId, mate.Id)).Ok);
    }

    [Fact]
    public async Task The_invite_list_excludes_members_and_people_already_asked()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var invited = await _db.AddUserAsync("invited");
        var free = await _db.AddUserAsync("free");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);
        await _teams.InviteAsync(owner.Id, teamId, invited.Id);

        var options = await _teams.GetInvitableUsersAsync(teamId);

        Assert.Equal([free.Id], options.Select(o => o.Id));
    }

    [Fact]
    public async Task An_owner_can_revoke_an_invite_they_sent()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _teams.InviteAsync(owner.Id, teamId, mate.Id);
        var invite = Assert.Single(await _teams.GetInvitesForUserAsync(mate.Id));

        Assert.True((await _teams.DeclineAsync(owner.Id, invite.Id)).Ok);
        Assert.Empty(await _teams.GetInvitesForUserAsync(mate.Id));
    }

    // ---- roles and removal ----------------------------------------------

    [Fact]
    public async Task The_last_owner_cannot_leave_or_be_demoted_or_removed()
    {
        // An ownerless team can never be deleted or renamed again, and its playlists become
        // undeletable. Refuse rather than create that state.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        Assert.False((await _teams.LeaveAsync(owner.Id, teamId)).Ok);
        Assert.False((await _teams.DemoteAsync(owner.Id, teamId, owner.Id)).Ok);
        Assert.False((await _teams.RemoveMemberAsync(owner.Id, teamId, owner.Id)).Ok);

        Assert.Equal(2, (await _teams.GetMembersAsync(teamId)).Count);
    }

    [Fact]
    public async Task An_owner_can_leave_once_somebody_else_is_promoted()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        Assert.True(await _teams.PromoteAsync(owner.Id, teamId, mate.Id));
        Assert.True((await _teams.LeaveAsync(owner.Id, teamId)).Ok);

        var remaining = Assert.Single(await _teams.GetMembersAsync(teamId));
        Assert.Equal(TeamRole.Owner, remaining.Role);
    }

    [Fact]
    public async Task An_owner_can_remove_a_member()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        Assert.True((await _teams.RemoveMemberAsync(owner.Id, teamId, mate.Id)).Ok);
        Assert.Single(await _teams.GetMembersAsync(teamId));
    }

    [Fact]
    public async Task A_plain_member_cannot_promote_remove_or_delete()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        Assert.False(await _teams.PromoteAsync(mate.Id, teamId, mate.Id));
        Assert.False((await _teams.RemoveMemberAsync(mate.Id, teamId, owner.Id)).Ok);
        Assert.False(await _teams.DeleteAsync(mate.Id, teamId));
        Assert.NotNull(await _teams.GetAsync(teamId, owner.Id));
    }

    [Fact]
    public async Task Membership_flags_are_reported_per_user()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var applicant = await _db.AddUserAsync("applicant");
        var invitee = await _db.AddUserAsync("invitee");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);
        await _teams.RequestJoinAsync(applicant.Id, teamId);
        await _teams.InviteAsync(owner.Id, teamId, invitee.Id);

        var asOwner = Assert.Single(await _teams.GetAllAsync(owner.Id));
        var asMate = Assert.Single(await _teams.GetAllAsync(mate.Id));
        var asApplicant = Assert.Single(await _teams.GetAllAsync(applicant.Id));
        var asInvitee = Assert.Single(await _teams.GetAllAsync(invitee.Id));

        Assert.True(asOwner is { IsMember: true, IsOwner: true, PendingApplicationCount: 1 });
        Assert.True(asMate is { IsMember: true, IsOwner: false });
        Assert.True(asApplicant is { IsMember: false, HasPendingApplicationFromMe: true });
        Assert.True(asInvitee is { IsMember: false, HasPendingInviteForMe: true });

        // Everyone can see a team exists — that's how you find one to ask about.
        Assert.Empty(await _teams.GetForUserAsync(applicant.Id));
        Assert.Single(await _teams.GetForUserAsync(mate.Id));
    }

    [Fact]
    public async Task Notifying_a_team_skips_the_person_who_asked()
    {
        // The requester gets "your request is ready"; everyone else gets "X added Y".
        // Sending both to the requester would double-cowbell them.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var teamId = await NewTeamAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        var others = await _teams.GetMemberIdsExceptAsync(teamId, owner.Id);

        Assert.Equal([mate.Id], others);
    }
}
