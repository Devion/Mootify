using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Notifications;
using Mootify.Services.Playback;
using Mootify.Services.Playlists;
using Mootify.Services.Recommendations;
using Mootify.Services.Settings;
using Mootify.Services.Teams;

namespace Mootify.Tests;

public sealed class SharedQueueTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private SharedQueueService _queues = null!;
    private PlaylistService _playlists = null!;
    private ListeningService _listening = null!;
    private AppUser _host = null!, _guest = null!;
    private Guid _team, _playlist;
    private List<Guid> _tracks = [];

    public async Task InitializeAsync()
    {
        _queues = new SharedQueueService(_db);
        _playlists = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);
        _listening = new ListeningService(_db, _playlists, NullLogger<ListeningService>.Instance);
        _host = await _db.AddUserAsync("host");
        _guest = await _db.AddUserAsync("guest");
        var teams = new TeamService(_db, new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance), NullLogger<TeamService>.Instance);
        var (id, _) = await teams.CreateAsync(_host.Id, "Office");
        _team = id!.Value;
        await _db.AddTeamMemberAsync(_team, _guest.Id);
        _playlist = (await _playlists.CreateAsync(_host.Id, "Office music", _team))!.Value;
        _tracks = await _db.AddTracksAsync(5);
        await _playlists.AddTracksAsync(_playlist, _host.Id, _tracks);
    }

    private PlayerService Player(AppUser user, FakeJsRuntime? js = null) => new(
        js ?? new FakeJsRuntime(), _db, _listening, new TasteService(_db, NullLogger<TasteService>.Instance),
        new PreferenceService(_db), new CurrentUser(new FakeAuthStateProvider(user.Id)),
        NullLogger<PlayerService>.Instance, _queues) { DispatchSharedQueue = action => action() };

    private async Task<Guid> Share(PlayerService host)
    {
        await host.PlayQueueAsync(_tracks.Take(3).ToList(), 0, _playlist);
        await host.SetSharingListeningAsync(true);
        await host.SetSharingQueueAsync(true);
        return (await _queues.FindAsync(_host.Id, _playlist, _guest.Id))!.Id;
    }

    [Fact]
    public async Task Joining_and_contributing_does_not_play_locally_and_updates_host_order()
    {
        await using var host = Player(_host);
        var js = new FakeJsRuntime();
        await using var guest = Player(_guest, js);
        var id = await Share(host);
        await host.ToggleShuffleAsync();
        var original = host.QueueEntries.Select(e => e.Track.Id).ToList();
        await guest.JoinSharedQueueAsync(id);
        await guest.EnqueueAsync(_tracks[3], next: true);
        await guest.EnqueueAsync(_tracks[4]);
        Assert.Equal(new[] { original[0], _tracks[3], original[1], original[2], _tracks[4] }, host.QueueEntries.Select(e => e.Track.Id));
        Assert.Equal(host.QueueEntries, guest.JoinedQueue!.Entries);
        Assert.Null(guest.Current);
        Assert.Empty(js.Calls);
        Assert.Equal(_tracks[0], host.Current!.Id);
        await host.NextAsync();
        await guest.RefreshSharedQueueAsync();
        Assert.Equal(_tracks[3], host.Current.Id);
        Assert.Equal(host.CurrentOrderIndex, guest.JoinedQueue!.CurrentIndex);
    }

    [Fact]
    public async Task Outsiders_and_revoked_members_cannot_read_or_contribute()
    {
        await using var host = Player(_host);
        var id = await Share(host);
        var outsider = await _db.AddUserAsync("outsider");
        Assert.Null(await _queues.GetAsync(id, outsider.Id));
        Assert.False(await _queues.AddAsync(id, outsider.Id, [_tracks[3]], false));
        await using var guest = Player(_guest);
        await guest.JoinSharedQueueAsync(id);
        await using (var db = _db.CreateDbContext())
        {
            db.TeamMembers.Remove(await db.TeamMembers.SingleAsync(m => m.TeamId == _team && m.UserId == _guest.Id));
            await db.SaveChangesAsync();
        }
        await guest.EnqueueAsync(_tracks[3]);
        Assert.Null(guest.JoinedQueue);
        Assert.Equal(3, host.Queue.Count);
        Assert.Empty(guest.Queue);
    }

    [Fact]
    public async Task Sharing_requires_opt_in_and_stops_when_listening_sharing_is_disabled()
    {
        await using var host = Player(_host);
        await host.PlayQueueAsync(_tracks, 0, _playlist);
        await host.SetSharingQueueAsync(true);
        Assert.False(host.IsSharingQueue);
        var id = await Share(host);
        await host.SetSharingListeningAsync(false);
        Assert.Null(await _queues.GetAsync(id, _guest.Id));
        Assert.False(await _queues.AddAsync(id, _guest.Id, [_tracks[3]], false));
    }

    [Fact]
    public async Task Paused_host_accepts_additions_and_replacing_queue_ends_session()
    {
        await using var host = Player(_host);
        var id = await Share(host);
        await host.TogglePlayPauseAsync();
        Assert.True(await _queues.AddAsync(id, _guest.Id, [_tracks[3], _tracks[4]], false));
        Assert.False(host.IsPlaying);
        Assert.Equal(_tracks, host.QueueEntries.Select(e => e.Track.Id));
        await host.PlayTrackAsync(_tracks[4]);
        Assert.Null(await _queues.GetAsync(id, _guest.Id));
    }

    [Fact]
    public async Task Concurrent_contributions_are_not_lost_and_leaving_restores_local_queue_actions()
    {
        await using var host = Player(_host);
        var id = await Share(host);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _queues.AddAsync(id, _guest.Id, [_tracks[3]], false)));
        Assert.All(results, Assert.True);
        Assert.Equal(13, host.Queue.Count);
        await using var guest = Player(_guest);
        await guest.JoinSharedQueueAsync(id);
        await guest.LeaveSharedQueueAsync();
        await guest.EnqueueAsync(_tracks[4]);
        Assert.Single(guest.Queue);
        Assert.Equal(13, host.Queue.Count);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();
}
