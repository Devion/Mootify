using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Teams;

namespace Mootify.Tests;

/// <summary>
/// "Listening now": who is playing this playlist, and who is allowed to know.
///
/// Almost every test here is a privacy test rather than a feature test, because that is where the
/// ways to get this wrong are. Broadcasting is opt-in, it is scoped to a playlist rather than to a
/// person, and both ends of it are re-checked on every read — a row written while somebody was in
/// a team must stop being visible the moment they aren't.
/// </summary>
public sealed class ListeningServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private ListeningService _service = null!;
    private PlaylistService _playlists = null!;
    private TeamService _teams = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _playlists = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);
        _teams = new TeamService(
            _db,
            new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance),
            NullLogger<TeamService>.Instance);
        _service = new ListeningService(_db, _playlists, NullLogger<ListeningService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>A team, a team playlist in it, and some tracks — the setup for nearly every test here.</summary>
    private async Task<(Guid TeamId, Guid PlaylistId, List<Guid> Tracks)> BandAsync(Guid ownerId)
    {
        var (teamId, error) = await _teams.CreateAsync(ownerId, "The Barn");
        Assert.Null(error);

        var playlistId = await _playlists.CreateAsync(ownerId, "Barn Bangers", teamId!.Value);
        Assert.NotNull(playlistId);

        var tracks = await _db.AddTracksAsync(3);
        await _playlists.AddTracksAsync(playlistId.Value, ownerId, tracks);

        return (teamId.Value, playlistId.Value, tracks);
    }

    /// <summary>Makes a session look older than it is, which is the only way "stale" is reachable.</summary>
    private async Task AgeAsync(Guid userId, TimeSpan by)
    {
        await using var db = _db.CreateDbContext();
        var session = await db.ListeningSessions.FirstAsync(s => s.UserId == userId);
        session.UpdatedAt -= by;
        await db.SaveChangesAsync();
    }

    // ---- the switch ------------------------------------------------------

    [Fact]
    public async Task Sharing_is_off_until_somebody_turns_it_on()
    {
        // An upgrade that started broadcasting everybody's playback would be a privacy bug, so
        // the default matters more than most defaults do.
        var user = await _db.AddUserAsync("devion");
        Assert.False(await _service.IsSharingAsync(user.Id));
    }

    [Fact]
    public async Task Nothing_is_published_while_the_switch_is_off()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        Assert.False(await _service.PublishAsync(owner.Id, playlistId, tracks[0], 12, true));
        Assert.Empty(await _service.GetForPlaylistAsync(playlistId, mate.Id));
    }

    [Fact]
    public async Task Turning_it_on_makes_the_next_heartbeat_visible_to_the_team()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(owner.Id, true);
        Assert.True(await _service.PublishAsync(owner.Id, playlistId, tracks[0], 42.5, true));

        var listener = Assert.Single(await _service.GetForPlaylistAsync(playlistId, mate.Id));

        Assert.Equal(owner.Id, listener.UserId);
        Assert.Equal("devion", listener.DisplayName);
        Assert.Equal(tracks[0], listener.TrackId);
        Assert.Equal("Track 0", listener.TrackTitle);
        Assert.Equal(42.5, listener.PositionSeconds);
        Assert.True(listener.IsPlaying);
    }

    [Fact]
    public async Task Turning_it_off_takes_down_what_is_already_on_screen()
    {
        // "Stop sharing" that leaves the last song visible for two minutes is not what anybody
        // pressing it means.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);
        Assert.Single(await _service.GetForPlaylistAsync(playlistId, mate.Id));

        await _service.SetSharingAsync(owner.Id, false);

        Assert.Empty(await _service.GetForPlaylistAsync(playlistId, mate.Id));
        Assert.False(await _service.IsSharingAsync(owner.Id));
    }

    // ---- what is broadcast -----------------------------------------------

    [Fact]
    public async Task Playing_something_that_is_not_a_playlist_clears_the_broadcast()
    {
        // An album, an artist or a search has no set of people it belongs to. Leaving the last
        // playlist up while somebody listens to something else would be a lie.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);

        Assert.False(await _service.PublishAsync(owner.Id, null, tracks[1], 0, true));
        Assert.Empty(await _service.GetForPlaylistAsync(playlistId, mate.Id));
    }

    [Fact]
    public async Task Stopping_ends_the_broadcast_without_changing_the_switch()
    {
        var owner = await _db.AddUserAsync("devion");
        var (_, playlistId, tracks) = await BandAsync(owner.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);

        await _service.StopAsync(owner.Id);

        Assert.True(await _service.IsSharingAsync(owner.Id));

        await using var db = _db.CreateDbContext();
        Assert.False(await db.ListeningSessions.AnyAsync(s => s.UserId == owner.Id));
    }

    [Fact]
    public async Task One_person_broadcasts_one_thing()
    {
        // Keyed by user, not by (user, playlist): browsing four playlists in a minute must not
        // leave three ghosts behind.
        var owner = await _db.AddUserAsync("devion");
        var (teamId, first, tracks) = await BandAsync(owner.Id);

        var second = await _playlists.CreateAsync(owner.Id, "Other List", teamId);
        await _playlists.AddTracksAsync(second!.Value, owner.Id, tracks);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, first, tracks[0], 0, true);
        await _service.PublishAsync(owner.Id, second.Value, tracks[1], 0, true);

        await using var db = _db.CreateDbContext();
        var session = Assert.Single(await db.ListeningSessions.ToListAsync());
        Assert.Equal(second.Value, session.PlaylistId);
    }

    [Fact]
    public async Task Moving_to_another_playlist_starts_a_new_sitting()
    {
        var owner = await _db.AddUserAsync("devion");
        var (teamId, first, tracks) = await BandAsync(owner.Id);
        var second = await _playlists.CreateAsync(owner.Id, "Other List", teamId);
        await _playlists.AddTracksAsync(second!.Value, owner.Id, tracks);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, first, tracks[0], 0, true);

        await using (var db = _db.CreateDbContext())
        {
            var session = await db.ListeningSessions.FirstAsync();
            session.StartedAt -= TimeSpan.FromHours(1);
            await db.SaveChangesAsync();
        }

        await _service.PublishAsync(owner.Id, second.Value, tracks[1], 0, true);

        await using var check = _db.CreateDbContext();
        var moved = await check.ListeningSessions.FirstAsync();
        Assert.True(moved.StartedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Staying_on_the_same_playlist_keeps_the_start_time()
    {
        var owner = await _db.AddUserAsync("devion");
        var (_, playlistId, tracks) = await BandAsync(owner.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);

        DateTimeOffset startedAt;
        await using (var db = _db.CreateDbContext())
        {
            startedAt = (await db.ListeningSessions.FirstAsync()).StartedAt;
        }

        await _service.PublishAsync(owner.Id, playlistId, tracks[1], 5, true);

        await using var check = _db.CreateDbContext();
        Assert.Equal(startedAt, (await check.ListeningSessions.FirstAsync()).StartedAt);
    }

    // ---- who can see it --------------------------------------------------

    [Fact]
    public async Task Somebody_outside_the_team_sees_nothing()
    {
        var owner = await _db.AddUserAsync("devion");
        var stranger = await _db.AddUserAsync("nosey");
        var (_, playlistId, tracks) = await BandAsync(owner.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);

        // The same empty answer as a playlist that has nobody on it — this must not be a way to
        // find out that somebody else's list exists.
        Assert.Empty(await _service.GetForPlaylistAsync(playlistId, stranger.Id));
    }

    [Fact]
    public async Task Leaving_the_team_stops_the_broadcast_being_visible()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(mate.Id, true);
        await _service.PublishAsync(mate.Id, playlistId, tracks[0], 0, true);
        Assert.Single(await _service.GetForPlaylistAsync(playlistId, owner.Id));

        await using (var db = _db.CreateDbContext())
        {
            db.TeamMembers.Remove(
                await db.TeamMembers.FirstAsync(m => m.TeamId == teamId && m.UserId == mate.Id));
            await db.SaveChangesAsync();
        }

        // Membership is re-checked on read, not trusted from when the row was written.
        Assert.Empty(await _service.GetForPlaylistAsync(playlistId, owner.Id));
    }

    [Fact]
    public async Task A_heartbeat_from_somebody_who_left_removes_the_row()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(mate.Id, true);
        await _service.PublishAsync(mate.Id, playlistId, tracks[0], 0, true);

        await using (var db = _db.CreateDbContext())
        {
            db.TeamMembers.Remove(
                await db.TeamMembers.FirstAsync(m => m.TeamId == teamId && m.UserId == mate.Id));
            await db.SaveChangesAsync();
        }

        Assert.False(await _service.PublishAsync(mate.Id, playlistId, tracks[1], 0, true));

        await using var check = _db.CreateDbContext();
        Assert.False(await check.ListeningSessions.AnyAsync());
    }

    [Fact]
    public async Task You_are_not_in_your_own_list()
    {
        // The toggle already says whether you're sharing. A row saying you're listening to what
        // you're listening to makes the feature look broken.
        var owner = await _db.AddUserAsync("devion");
        var (_, playlistId, tracks) = await BandAsync(owner.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);

        Assert.Empty(await _service.GetForPlaylistAsync(playlistId, owner.Id));
    }

    [Fact]
    public async Task A_personal_playlist_never_has_listeners()
    {
        var owner = await _db.AddUserAsync("devion");
        var tracks = await _db.AddTracksAsync(2);
        var playlistId = await _playlists.CreateAsync(owner.Id, "Just Mine");
        await _playlists.AddTracksAsync(playlistId!.Value, owner.Id, tracks);

        await _service.SetSharingAsync(owner.Id, true);
        Assert.True(await _service.PublishAsync(owner.Id, playlistId.Value, tracks[0], 0, true));

        // It is published — the row exists — but a personal list has exactly one reader and they
        // are excluded from their own list, so there is nobody it can reach.
        Assert.Empty(await _service.GetForPlaylistAsync(playlistId.Value, owner.Id));
    }

    [Fact]
    public async Task A_playlist_that_is_gone_has_no_listeners()
    {
        var owner = await _db.AddUserAsync("devion");
        Assert.Empty(await _service.GetForPlaylistAsync(Guid.NewGuid(), owner.Id));
    }

    // ---- liveness ---------------------------------------------------------

    [Fact]
    public async Task A_session_older_than_the_staleness_window_is_not_shown()
    {
        // A closed tab, a phone in a tunnel and a killed process all fail to say goodbye, so
        // liveness is a timestamp rather than a message that has to arrive.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);

        await AgeAsync(owner.Id, ListeningService.StaleAfter + TimeSpan.FromSeconds(30));

        Assert.Empty(await _service.GetForPlaylistAsync(playlistId, mate.Id));
    }

    [Fact]
    public async Task A_heartbeat_brings_a_stale_session_back()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);
        await AgeAsync(owner.Id, ListeningService.StaleAfter + TimeSpan.FromSeconds(30));

        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 90, true);

        Assert.Single(await _service.GetForPlaylistAsync(playlistId, mate.Id));
    }

    [Fact]
    public async Task Paused_is_still_worth_showing()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, mate.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 30, isPlaying: false);

        Assert.False(Assert.Single(await _service.GetForPlaylistAsync(playlistId, mate.Id)).IsPlaying);
    }

    [Fact]
    public async Task Deleting_the_playlist_takes_the_session_with_it()
    {
        var owner = await _db.AddUserAsync("devion");
        var (_, playlistId, tracks) = await BandAsync(owner.Id);

        await _service.SetSharingAsync(owner.Id, true);
        await _service.PublishAsync(owner.Id, playlistId, tracks[0], 0, true);

        await using (var db = _db.CreateDbContext())
        {
            db.Playlists.Remove(await db.Playlists.FirstAsync(p => p.Id == playlistId));
            await db.SaveChangesAsync();
        }

        await using var check = _db.CreateDbContext();
        Assert.False(await check.ListeningSessions.AnyAsync());
    }

    [Fact]
    public async Task Everyone_on_the_playlist_is_listed()
    {
        var owner = await _db.AddUserAsync("devion");
        var one = await _db.AddUserAsync("robin");
        var two = await _db.AddUserAsync("sam");
        var (teamId, playlistId, tracks) = await BandAsync(owner.Id);
        await _db.AddTeamMemberAsync(teamId, one.Id);
        await _db.AddTeamMemberAsync(teamId, two.Id);

        foreach (var (user, track) in new[] { (one, tracks[0]), (two, tracks[1]) })
        {
            await _service.SetSharingAsync(user.Id, true);
            await _service.PublishAsync(user.Id, playlistId, track, 0, true);
        }

        var listeners = await _service.GetForPlaylistAsync(playlistId, owner.Id);

        Assert.Equal(2, listeners.Count);
        Assert.Equal(["robin", "sam"], listeners.Select(l => l.DisplayName).Order());
    }
}
