using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Playlists;
using Mootify.Services.Notifications;
using Mootify.Services.Teams;

namespace Mootify.Tests;

public sealed class PlaylistServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private PlaylistService _service = null!;
    private TeamService _teams = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _service = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);
        _teams = new TeamService(
            _db,
            new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance),
            NullLogger<TeamService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>Create-and-unwrap. Creation only returns null when it was refused, which
    /// these tests assert explicitly where it matters.</summary>
    private async Task<Guid> NewPlaylistAsync(Guid userId, string name, Guid? teamId = null)
    {
        var id = await _service.CreateAsync(userId, name, teamId);
        Assert.NotNull(id);
        return id.Value;
    }

    private async Task<Guid> NewTeamAsync(Guid ownerId, string name)
    {
        var (id, error) = await _teams.CreateAsync(ownerId, name);
        Assert.Null(error);
        return id!.Value;
    }

    // ---- personal playlists ---------------------------------------------

    [Fact]
    public async Task Summary_aggregates_duration_in_sql()
    {
        // Regression: SQLite stores TimeSpan as TEXT, so SUM() over a TimeSpan column can't
        // be translated. Durations are persisted as ticks precisely so this query works.
        var user = await _db.AddUserAsync("devion");
        var tracks = await _db.AddTracksAsync(3);
        var playlist = await NewPlaylistAsync(user.Id, "Barn Bangers");
        await _service.AddTracksAsync(playlist, user.Id, tracks);

        var summary = Assert.Single(await _service.GetForUserAsync(user.Id));

        Assert.Equal("Barn Bangers", summary.Name);
        Assert.Equal(3, summary.TrackCount);
        Assert.Equal(TimeSpan.FromMinutes(9), summary.TotalDuration);
        Assert.False(summary.IsTeamPlaylist);
    }

    [Fact]
    public async Task Adding_the_same_request_twice_appends_once()
    {
        // The webhook and the reconciliation poller will both fire for the same request.
        // Without this the user's playlist quietly gains the album twice.
        var user = await _db.AddUserAsync("devion");
        var tracks = await _db.AddTracksAsync(4);
        var playlist = await NewPlaylistAsync(user.Id, "Auto");
        var requestId = Guid.NewGuid();

        var first = await _service.AddTracksAsync(playlist, user.Id, tracks, requestId);
        var second = await _service.AddTracksAsync(playlist, user.Id, tracks, requestId);

        Assert.Equal(4, first);
        Assert.Equal(0, second);

        await using var db = _db.CreateDbContext();
        Assert.Equal(4, await db.PlaylistItems.CountAsync(i => i.PlaylistId == playlist));
    }

    [Fact]
    public async Task Adding_by_hand_twice_is_allowed()
    {
        // Deliberate asymmetry with the test above: people do want the same song twice in
        // a playlist. Only the automatic path deduplicates.
        var user = await _db.AddUserAsync("devion");
        var tracks = await _db.AddTracksAsync(1);
        var playlist = await NewPlaylistAsync(user.Id, "On Repeat");

        await _service.AddTracksAsync(playlist, user.Id, tracks);
        await _service.AddTracksAsync(playlist, user.Id, tracks);

        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.PlaylistItems.CountAsync(i => i.PlaylistId == playlist));
    }

    [Fact]
    public async Task Another_user_cannot_write_to_your_playlist()
    {
        var owner = await _db.AddUserAsync("devion");
        var stranger = await _db.AddUserAsync("someone-else");
        var tracks = await _db.AddTracksAsync(2);
        var playlist = await NewPlaylistAsync(owner.Id, "Private");

        Assert.Equal(0, await _service.AddTracksAsync(playlist, stranger.Id, tracks));
    }

    [Fact]
    public async Task Another_user_cannot_read_or_delete_your_playlist()
    {
        var owner = await _db.AddUserAsync("devion");
        var stranger = await _db.AddUserAsync("someone-else");
        var playlist = await NewPlaylistAsync(owner.Id, "Private");

        Assert.Null(await _service.GetAsync(playlist, stranger.Id));
        Assert.False(await _service.DeleteAsync(playlist, stranger.Id));
        Assert.False(await _service.RenameAsync(playlist, stranger.Id, "Hijacked"));
        Assert.NotNull(await _service.GetAsync(playlist, owner.Id));
    }

    [Fact]
    public async Task Move_places_an_item_between_its_new_neighbours()
    {
        var user = await _db.AddUserAsync("devion");
        var tracks = await _db.AddTracksAsync(3);
        var playlist = await NewPlaylistAsync(user.Id, "Ordered");
        await _service.AddTracksAsync(playlist, user.Id, tracks);

        var items = (await _service.GetAsync(playlist, user.Id))!.Items.OrderBy(i => i.SortKey).ToList();

        // Move the last item between the first two.
        await _service.MoveAsync(items[2].Id, user.Id, items[0].SortKey, items[1].SortKey);

        var order = (await _service.GetAsync(playlist, user.Id))!
            .Items.OrderBy(i => i.SortKey).Select(i => i.TrackId).ToList();

        Assert.Equal([tracks[0], tracks[2], tracks[1]], order);
    }

    [Fact]
    public async Task Deleting_a_playlist_leaves_the_request_that_targeted_it()
    {
        // The music still arrives even if the playlist it was headed for is gone.
        var user = await _db.AddUserAsync("devion");
        var playlist = await NewPlaylistAsync(user.Id, "Doomed");

        await using (var db = _db.CreateDbContext())
        {
            db.Requests.Add(new Request
            {
                Id = Guid.NewGuid(),
                RequesterId = user.Id,
                TargetPlaylistId = playlist,
                ArtistName = "The Cowbells",
                AlbumTitle = "Pasture Sounds",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await _service.DeleteAsync(playlist, user.Id);

        await using var check = _db.CreateDbContext();
        Assert.Null((await check.Requests.SingleAsync()).TargetPlaylistId);
    }

    // ---- team playlists --------------------------------------------------

    [Fact]
    public async Task Team_members_all_see_the_team_playlist()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var team = await NewTeamAsync(owner.Id, "Kitchen");
        await _db.AddTeamMemberAsync(team, mate.Id);

        var playlist = await NewPlaylistAsync(owner.Id, "Dinner", team);

        var seen = Assert.Single(await _service.GetForUserAsync(mate.Id));
        Assert.Equal("Dinner", seen.Name);
        Assert.True(seen.IsTeamPlaylist);
        Assert.Equal("Kitchen", seen.TeamName);
        Assert.NotNull(await _service.GetAsync(playlist, mate.Id));
    }

    [Fact]
    public async Task Any_member_can_add_to_a_team_playlist()
    {
        // The whole point of a shared playlist: it isn't the creator's private list.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var team = await NewTeamAsync(owner.Id, "Kitchen");
        await _db.AddTeamMemberAsync(team, mate.Id);
        var playlist = await NewPlaylistAsync(owner.Id, "Dinner", team);
        var tracks = await _db.AddTracksAsync(2);

        Assert.Equal(2, await _service.AddTracksAsync(playlist, mate.Id, tracks));
    }

    [Fact]
    public async Task Non_members_cannot_see_or_touch_a_team_playlist()
    {
        var owner = await _db.AddUserAsync("devion");
        var outsider = await _db.AddUserAsync("nosy");
        var team = await NewTeamAsync(owner.Id, "Kitchen");
        var playlist = await NewPlaylistAsync(owner.Id, "Dinner", team);
        var tracks = await _db.AddTracksAsync(2);

        Assert.Null(await _service.GetAsync(playlist, outsider.Id));
        Assert.Empty(await _service.GetForUserAsync(outsider.Id));
        Assert.Equal(0, await _service.AddTracksAsync(playlist, outsider.Id, tracks));
        Assert.False(await _service.RenameAsync(playlist, outsider.Id, "Mine now"));
        Assert.False(await _service.DeleteAsync(playlist, outsider.Id));
    }

    [Fact]
    public async Task You_cannot_plant_a_playlist_in_a_team_you_are_not_in()
    {
        var owner = await _db.AddUserAsync("devion");
        var outsider = await _db.AddUserAsync("nosy");
        var team = await NewTeamAsync(owner.Id, "Kitchen");

        Assert.Null(await _service.CreateAsync(outsider.Id, "Sneaky", team));
    }

    [Fact]
    public async Task A_member_can_add_but_only_an_owner_can_delete()
    {
        // Adding a track is reversible. Deleting the list throws away everyone's work.
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var team = await NewTeamAsync(owner.Id, "Kitchen");
        await _db.AddTeamMemberAsync(team, mate.Id);
        var playlist = await NewPlaylistAsync(owner.Id, "Dinner", team);

        Assert.True(await _service.RenameAsync(playlist, mate.Id, "Supper"));
        Assert.False(await _service.DeleteAsync(playlist, mate.Id));
        Assert.True(await _service.DeleteAsync(playlist, owner.Id));
    }

    [Fact]
    public async Task Leaving_a_team_takes_its_playlists_out_of_your_sidebar()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var team = await NewTeamAsync(owner.Id, "Kitchen");
        await _db.AddTeamMemberAsync(team, mate.Id);
        var playlist = await NewPlaylistAsync(owner.Id, "Dinner", team);

        Assert.Single(await _service.GetForUserAsync(mate.Id));

        await _teams.LeaveAsync(mate.Id, team);

        Assert.Empty(await _service.GetForUserAsync(mate.Id));
        Assert.Null(await _service.GetAsync(playlist, mate.Id));
    }

    [Fact]
    public async Task Deleting_a_team_takes_its_playlists_with_it()
    {
        var owner = await _db.AddUserAsync("devion");
        var team = await NewTeamAsync(owner.Id, "Kitchen");
        await NewPlaylistAsync(owner.Id, "Dinner", team);

        await _teams.DeleteAsync(owner.Id, team);

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Playlists.CountAsync());
    }
}
