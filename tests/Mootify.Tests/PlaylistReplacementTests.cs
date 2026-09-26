using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Requests;
using Mootify.Services.Settings;
using Mootify.Services.Soulseek;
using Mootify.Services.Teams;

namespace Mootify.Tests;

public sealed class PlaylistReplacementTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly FakeSoulseek _handler = new();
    private RequestService _requests = null!;
    private RequestFulfiller _fulfiller = null!;
    private PlaylistService _playlists = null!;
    private Guid _user, _playlist;
    private List<Guid> _tracks = [];
    private PlaylistItem _item = null!;

    public async Task InitializeAsync()
    {
        _user = (await _db.AddUserAsync("listener")).Id;
        _tracks = await _db.AddTracksAsync(4);
        _playlists = new(_db, NullLogger<PlaylistService>.Instance);
        _playlist = (await _playlists.CreateAsync(_user, "Favorites"))!.Value;
        await _playlists.AddTracksAsync(_playlist, _user, _tracks.Take(3).ToArray());
        await using var db = _db.CreateDbContext();
        _item = await db.PlaylistItems.AsNoTracking().SingleAsync(i => i.TrackId == _tracks[1]);
        var client = new SoulseekClient(new HttpClient(_handler),
            new StaticOptionsMonitor<SoulseekOptions>(new() { BaseUrl = "http://slskd.invalid", ApiKey = "test" }),
            NullLogger<SoulseekClient>.Instance);
        var settings = new SettingsService(_db, new StaticOptionsMonitor<AuthOptions>(new()),
            new StaticOptionsMonitor<RequestOptions>(new() { MaxOpenPerUser = 100 }));
        _requests = new(_db, client, settings, NullLogger<RequestService>.Instance);
        var notifications = new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance);
        _fulfiller = new(_db, _playlists, new TeamService(_db, notifications, NullLogger<TeamService>.Instance),
            notifications, NullLogger<RequestFulfiller>.Instance);
    }
    public async Task DisposeAsync() => await _db.DisposeAsync();
    private Task<CreateRequestResult> RequestAsync(Guid? user = null, Guid? expected = null) => _requests.CreateAsync(
        user ?? _user, new(Guid.NewGuid(), "peer", @"Artist\Song.mp3", 1000, "mp3", 320, null, null, 180, true, 0, 1000),
        "Artist Song", null, replacementItemId: _item.Id, expectedTrackId: expected ?? _item.TrackId);

    [Fact]
    public void Download_selection_uses_the_chosen_file_not_an_old_file_in_the_request_folder()
    {
        var chosen = Guid.NewGuid();
        (Guid Id, string Path)[] files =
        [
            (Guid.NewGuid(), @"C:\download\01 - Song (Live).mp3"),
            (chosen, @"C:\download\01 - Song (Studio).mp3")
        ];
        Assert.Equal(chosen, RequestReconciler.SelectReplacementDownload(@"Music\01 - Song (Studio).flac", files));
        Assert.Null(RequestReconciler.SelectReplacementDownload(@"Music\Another Song.mp3", files));
        Assert.Null(RequestReconciler.SelectReplacementDownload(@"Music\01 - Song (Studio).mp3",
            files.Concat([(Guid.NewGuid(), @"C:\download\other\01 - Song (Studio).mp3")])));
    }

    [Fact]
    public async Task Request_keeps_original_until_completion_then_replaces_only_selected_slot()
    {
        var other = (await _playlists.CreateAsync(_user, "Other"))!.Value;
        await _playlists.AddTracksAsync(other, _user, [_item.TrackId]);
        var result = await RequestAsync();
        Assert.True(result.Ok);
        Assert.False((await RequestAsync()).Ok);
        await using var db = _db.CreateDbContext();
        Assert.Equal(_item.TrackId, (await db.PlaylistItems.FindAsync(_item.Id))!.TrackId);
        var request = await db.Requests.SingleAsync();
        Assert.Equal(_playlist, request.TargetPlaylistId);
        Assert.Equal(_item.TrackId, request.ReplacementTrackId);
        await _fulfiller.CompleteAsync(db, request, [_tracks[3]]);
        db.ChangeTracker.Clear();
        var item = await db.PlaylistItems.SingleAsync(i => i.Id == _item.Id);
        Assert.Equal(_tracks[3], item.TrackId);
        Assert.Equal(_item.SortKey, item.SortKey);
        Assert.Equal(_item.TrackId, (await db.PlaylistItems.SingleAsync(i => i.PlaylistId == other)).TrackId);
        Assert.Equal(4, await db.Tracks.CountAsync());
        Assert.Contains("replaced", (await db.Notifications.SingleAsync()).Body);
        request = await db.Requests.SingleAsync();
        await _fulfiller.CompleteAsync(db, request, [_tracks[3]]);
        Assert.Equal(1, await db.Notifications.CountAsync());
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("changed")]
    [InlineData("banned")]
    public async Task Changed_or_inaccessible_entry_is_not_overwritten(string change)
    {
        await RequestAsync();
        await using var db = _db.CreateDbContext();
        var item = await db.PlaylistItems.SingleAsync(i => i.Id == _item.Id);
        if (change == "deleted") db.PlaylistItems.Remove(item);
        if (change == "changed") item.TrackId = _tracks[0];
        if (change == "banned") (await db.Users.SingleAsync()).IsBanned = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var request = await db.Requests.SingleAsync();
        await _fulfiller.CompleteAsync(db, request, [_tracks[3]]);
        Assert.DoesNotContain(await db.PlaylistItems.ToListAsync(), i => i.TrackId == _tracks[3]);
        Assert.Contains("No replacement", request.FailureReason);
        Assert.Contains("No replacement", (await db.Notifications.SingleAsync()).Body);
    }

    [Fact]
    public async Task Existing_alternative_keeps_selected_slot_without_duplicate()
    {
        await RequestAsync();
        await using var db = _db.CreateDbContext();
        await _fulfiller.CompleteAsync(db, await db.Requests.SingleAsync(), [_tracks[0]]);
        var items = await db.PlaylistItems.OrderBy(i => i.SortKey).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal(_item.Id, items[0].Id);
        Assert.Equal(_tracks[0], items[0].TrackId);
        Assert.Equal(_item.SortKey, items[0].SortKey);
    }

    [Fact]
    public async Task Unauthorized_and_stale_requests_do_not_download()
    {
        var stranger = (await _db.AddUserAsync("stranger")).Id;
        Assert.Null(await _requests.GetReplacementTargetAsync(_item.Id, stranger));
        Assert.False((await RequestAsync(stranger)).Ok);
        Assert.False((await RequestAsync(expected: _tracks[0])).Ok);
        Assert.Equal(0, _handler.EnqueueCalls);
    }

    [Fact]
    public async Task Failed_download_and_unrelated_import_leave_original_in_place()
    {
        _handler.Fail = true;
        Assert.False((await RequestAsync()).Ok);
        await using var db = _db.CreateDbContext();
        var request = await db.Requests.SingleAsync();
        var track = await db.Tracks.Include(t => t.Artist).SingleAsync(t => t.Id == _tracks[3]);
        request.ArtistName = track.Artist!.Name;
        request.TrackTitle = track.Title;
        await db.SaveChangesAsync();
        var matcher = new ImportRequestMatcher(_db, _fulfiller, NullLogger<ImportRequestMatcher>.Instance);
        Assert.Equal(0, (await matcher.MatchAsync([track.Path])).Requests);
        Assert.Equal(_item.TrackId, (await db.PlaylistItems.SingleAsync(i => i.Id == _item.Id)).TrackId);
    }
}
