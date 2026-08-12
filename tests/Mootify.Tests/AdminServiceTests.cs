using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Admin;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Teams;

namespace Mootify.Tests;

public sealed class AdminServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private AdminService _admin = null!;
    private TeamService _teams = null!;
    private PlaylistService _playlists = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _admin = new AdminService(_db, NullLogger<AdminService>.Instance);
        _teams = new TeamService(
            _db,
            new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance),
            NullLogger<TeamService>.Instance);
        _playlists = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<AppUser> AddAdminAsync(string name = "mooadmin")
    {
        var user = await _db.AddUserAsync(name);
        await using var db = _db.CreateDbContext();
        var tracked = await db.Users.SingleAsync(u => u.Id == user.Id);
        tracked.IsAdmin = true;
        await db.SaveChangesAsync();
        return user;
    }

    // ---- authorization ---------------------------------------------------

    [Fact]
    public async Task Every_admin_operation_refuses_a_normal_user()
    {
        // The [Authorize] attribute on the page is for the UI. This is the boundary.
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");
        var victim = await _db.AddUserAsync("victim");
        var (teamId, _) = await _teams.CreateAsync(admin.Id, "Kitchen");
        var playlistId = await _playlists.CreateAsync(admin.Id, "Mine");

        Assert.False((await _admin.SetBannedAsync(user.Id, victim.Id, true)).Ok);
        Assert.False((await _admin.SetAdminAsync(user.Id, victim.Id, true)).Ok);
        Assert.False((await _admin.DeleteUserAsync(user.Id, victim.Id)).Ok);
        Assert.False((await _admin.UpdateTeamAsync(user.Id, teamId!.Value, "Hijacked", null, TeamJoinPolicy.Open)).Ok);
        Assert.False((await _admin.ForceAddMemberAsync(user.Id, teamId.Value, victim.Id)).Ok);
        Assert.False((await _admin.ForceRemoveMemberAsync(user.Id, teamId.Value, admin.Id)).Ok);
        Assert.False((await _admin.DeleteTeamAsync(user.Id, teamId.Value)).Ok);
        Assert.False((await _admin.RenamePlaylistAsync(user.Id, playlistId!.Value, "Theirs")).Ok);
        Assert.False((await _admin.DeletePlaylistAsync(user.Id, playlistId.Value)).Ok);
        Assert.NotNull((await _admin.PruneMissingTracksAsync(user.Id)).Error);
    }

    [Fact]
    public async Task A_banned_admin_stops_being_an_admin()
    {
        var first = await AddAdminAsync();
        var second = await AddAdminAsync("second");

        await _admin.SetBannedAsync(first.Id, second.Id, true, "misbehaving");

        Assert.False(await _admin.IsAdminAsync(second.Id));
        Assert.False((await _admin.SetBannedAsync(second.Id, first.Id, true)).Ok);
    }

    // ---- users -----------------------------------------------------------

    [Fact]
    public async Task Banning_records_the_reason_and_unbanning_clears_it()
    {
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");

        await _admin.SetBannedAsync(admin.Id, user.Id, true, "kept adding polka");

        var banned = (await _admin.GetUsersAsync()).Single(u => u.Id == user.Id);
        Assert.True(banned.IsBanned);
        Assert.Equal("kept adding polka", banned.BanReason);

        await _admin.SetBannedAsync(admin.Id, user.Id, false);

        var restored = (await _admin.GetUsersAsync()).Single(u => u.Id == user.Id);
        Assert.False(restored.IsBanned);
        Assert.Null(restored.BanReason);
    }

    [Fact]
    public async Task Banning_leaves_their_playlists_alone()
    {
        // Banning isn't deleting — unban has to give them everything back.
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");
        await _playlists.CreateAsync(user.Id, "Theirs");

        await _admin.SetBannedAsync(admin.Id, user.Id, true);

        await using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Playlists.CountAsync(p => p.OwnerUserId == user.Id));
    }

    [Fact]
    public async Task You_cannot_ban_or_delete_yourself()
    {
        var admin = await AddAdminAsync();

        Assert.False((await _admin.SetBannedAsync(admin.Id, admin.Id, true)).Ok);
        Assert.False((await _admin.DeleteUserAsync(admin.Id, admin.Id)).Ok);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_banned_demoted_or_deleted()
    {
        // Any of these would leave the panel locked with nobody able to unlock it.
        var first = await AddAdminAsync();
        var second = await AddAdminAsync("second");

        Assert.True((await _admin.SetAdminAsync(first.Id, second.Id, false)).Ok);

        Assert.False((await _admin.SetAdminAsync(first.Id, first.Id, false)).Ok);
        Assert.False((await _admin.SetBannedAsync(second.Id, first.Id, true)).Ok);
        Assert.False((await _admin.DeleteUserAsync(second.Id, first.Id)).Ok);
    }

    [Fact]
    public async Task Deleting_a_user_takes_their_playlists_but_not_the_teams_they_were_in()
    {
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");
        var (teamId, _) = await _teams.CreateAsync(admin.Id, "Kitchen");
        await _db.AddTeamMemberAsync(teamId!.Value, user.Id);
        await _playlists.CreateAsync(user.Id, "Theirs");
        await _playlists.CreateAsync(admin.Id, "Dinner", teamId.Value);

        Assert.True((await _admin.DeleteUserAsync(admin.Id, user.Id)).Ok);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Playlists.CountAsync(p => p.OwnerUserId == user.Id));
        Assert.Equal(1, await db.Teams.CountAsync());
        Assert.Equal(1, await db.Playlists.CountAsync(p => p.TeamId == teamId));
    }

    [Fact]
    public async Task Deleting_the_only_owner_of_a_team_is_refused_with_the_team_named()
    {
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");
        await _teams.CreateAsync(user.Id, "Their Team");

        var (ok, error) = await _admin.DeleteUserAsync(admin.Id, user.Id);

        Assert.False(ok);
        Assert.Contains("Their Team", error);
    }

    // ---- teams -----------------------------------------------------------

    [Fact]
    public async Task An_admin_can_rename_a_team_they_are_not_in()
    {
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");
        var (teamId, _) = await _teams.CreateAsync(user.Id, "Kitchen");

        var (ok, error) = await _admin.UpdateTeamAsync(admin.Id, teamId!.Value, "Scullery", "for washing up", TeamJoinPolicy.Open);

        Assert.True(ok, error);
        var team = (await _admin.GetTeamsAsync()).Single();
        Assert.Equal("Scullery", team.Name);
        Assert.Equal(TeamJoinPolicy.Open, team.JoinPolicy);
        Assert.Equal("housemate", team.Owners);
    }

    [Fact]
    public async Task Renaming_a_team_onto_an_existing_name_is_refused()
    {
        var admin = await AddAdminAsync();
        await _teams.CreateAsync(admin.Id, "Kitchen");
        var (second, _) = await _teams.CreateAsync(admin.Id, "Garage");

        Assert.False((await _admin.UpdateTeamAsync(admin.Id, second!.Value, "Kitchen", null, TeamJoinPolicy.Open)).Ok);
    }

    [Fact]
    public async Task Force_add_skips_the_invite_and_clears_anything_pending()
    {
        var admin = await AddAdminAsync();
        var owner = await _db.AddUserAsync("owner");
        var user = await _db.AddUserAsync("housemate");
        var (teamId, _) = await _teams.CreateAsync(owner.Id, "Kitchen");

        // They'd already applied; force-adding shouldn't leave that row behind.
        await _teams.RequestJoinAsync(user.Id, teamId!.Value);

        var (ok, error) = await _admin.ForceAddMemberAsync(admin.Id, teamId.Value, user.Id);

        Assert.True(ok, error);
        Assert.Equal(2, (await _teams.GetMembersAsync(teamId.Value)).Count);
        Assert.Empty(await _teams.GetPendingForTeamAsync(teamId.Value));
    }

    [Fact]
    public async Task Force_add_works_on_an_invite_only_team()
    {
        var admin = await AddAdminAsync();
        var owner = await _db.AddUserAsync("owner");
        var user = await _db.AddUserAsync("housemate");
        var (teamId, _) = await _teams.CreateAsync(owner.Id, "Kitchen");
        await _teams.UpdateAsync(owner.Id, teamId!.Value, "Kitchen", null, TeamJoinPolicy.InviteOnly);

        Assert.True((await _admin.ForceAddMemberAsync(admin.Id, teamId.Value, user.Id)).Ok);
        Assert.Equal(2, (await _teams.GetMembersAsync(teamId.Value)).Count);
    }

    [Fact]
    public async Task Force_remove_still_refuses_to_orphan_a_team()
    {
        var admin = await AddAdminAsync();
        var owner = await _db.AddUserAsync("owner");
        var (teamId, _) = await _teams.CreateAsync(owner.Id, "Kitchen");

        var (ok, error) = await _admin.ForceRemoveMemberAsync(admin.Id, teamId!.Value, owner.Id);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Deleting_a_team_takes_its_playlists_and_leaves_the_tracks()
    {
        var admin = await AddAdminAsync();
        var (teamId, _) = await _teams.CreateAsync(admin.Id, "Kitchen");
        var tracks = await _db.AddTracksAsync(3);
        var playlistId = await _playlists.CreateAsync(admin.Id, "Dinner", teamId!.Value);
        await _playlists.AddTracksAsync(playlistId!.Value, admin.Id, tracks);

        Assert.True((await _admin.DeleteTeamAsync(admin.Id, teamId.Value)).Ok);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Playlists.CountAsync());
        Assert.Equal(3, await db.Tracks.CountAsync());
    }

    // ---- playlists -------------------------------------------------------

    [Fact]
    public async Task An_admin_can_rename_and_delete_a_playlist_they_do_not_own()
    {
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");
        var playlistId = await _playlists.CreateAsync(user.Id, "Theirs");

        Assert.True((await _admin.RenamePlaylistAsync(admin.Id, playlistId!.Value, "Renamed")).Ok);
        Assert.Equal("Renamed", (await _admin.GetPlaylistsAsync()).Single().Name);

        Assert.True((await _admin.DeletePlaylistAsync(admin.Id, playlistId.Value)).Ok);
        Assert.Empty(await _admin.GetPlaylistsAsync());
    }

    [Fact]
    public async Task Pruning_drops_entries_whose_file_has_gone_and_leaves_the_rest()
    {
        var admin = await AddAdminAsync();
        var tracks = await _db.AddTracksAsync(3);
        var playlistId = await _playlists.CreateAsync(admin.Id, "Mixed");
        await _playlists.AddTracksAsync(playlistId!.Value, admin.Id, tracks);

        await using (var db = _db.CreateDbContext())
        {
            var gone = await db.Tracks.FirstAsync(t => t.Id == tracks[0]);
            gone.IsPresent = false;
            await db.SaveChangesAsync();
        }

        var (removed, error) = await _admin.PruneMissingTracksAsync(admin.Id);

        Assert.Null(error);
        Assert.Equal(1, removed);

        await using var check = _db.CreateDbContext();
        Assert.Equal(2, await check.PlaylistItems.CountAsync());
    }

    [Fact]
    public async Task Stats_count_what_the_overview_claims()
    {
        var admin = await AddAdminAsync();
        var user = await _db.AddUserAsync("housemate");
        await _admin.SetBannedAsync(admin.Id, user.Id, true);
        await _teams.CreateAsync(admin.Id, "Kitchen");
        await _playlists.CreateAsync(admin.Id, "Mine");
        await _db.AddTracksAsync(4);

        var stats = await _admin.GetStatsAsync();

        Assert.Equal(2, stats.Users);
        Assert.Equal(1, stats.BannedUsers);
        Assert.Equal(1, stats.Teams);
        Assert.Equal(1, stats.Playlists);
        Assert.Equal(4, stats.Tracks);
    }
}
