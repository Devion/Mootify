using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Import;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Teams;

namespace Mootify.Tests;

/// <summary>
/// Landing an import somewhere: a new playlist, or one that already exists. The matching
/// itself is covered by <see cref="ImportMatchingTests"/> — what's under test here is where
/// the matched songs end up and who's allowed to put them there.
/// </summary>
public sealed class PlaylistImportServiceTests : IAsyncLifetime
{
    private const string Header =
        "Track URI,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms),Popularity,Explicit,Added By,Added At,Genres,Record Label";

    private TestDatabase _db = null!;
    private PlaylistService _playlists = null!;
    private PlaylistImportService _importer = null!;
    private TeamService _teams = null!;

    public Task InitializeAsync()
    {
        _db = new TestDatabase();
        _playlists = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);
        _importer = new PlaylistImportService(_db, _playlists, NullLogger<PlaylistImportService>.Instance);
        _teams = new TeamService(
            _db,
            new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance),
            NullLogger<TeamService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>An export of the tracks <see cref="TestDatabase.AddTracksAsync"/> puts on disk.</summary>
    private static string Export(int count) =>
        Header + "\n" + string.Join("\n", Enumerable.Range(0, count).Select(i =>
            $"uri,\"Track {i}\",\"Pasture Sounds\",\"The Cowbells\",2020,180000,50,false,user,2025-01-01T00:00:00Z,\"pop\",\"Label\""));

    private async Task<int> ItemCountAsync(Guid playlistId)
    {
        await using var db = _db.CreateDbContext();
        return await db.PlaylistItems.CountAsync(i => i.PlaylistId == playlistId);
    }

    [Fact]
    public async Task Appending_adds_the_matches_to_a_playlist_that_already_exists()
    {
        var user = await _db.AddUserAsync("devion");
        await _db.AddTracksAsync(3);
        var playlist = await _playlists.CreateAsync(user.Id, "Barn Bangers");

        var preview = await _importer.PreviewAsync("More_Bangers.csv", Export(3));
        var result = await _importer.AppendAsync(user.Id, preview, playlist!.Value);

        Assert.Equal(3, preview.Matched.Count);
        Assert.True(result.Ok);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.AlreadyThere);
        Assert.Equal(3, await ItemCountAsync(playlist.Value));
    }

    [Fact]
    public async Task Appending_leaves_songs_the_playlist_already_has_alone()
    {
        // Adding a song twice by hand is allowed on purpose. An import is a bulk action nobody
        // reviews row by row, so re-importing an export you've already merged must not double it.
        var user = await _db.AddUserAsync("devion");
        var tracks = await _db.AddTracksAsync(3);
        var playlist = await _playlists.CreateAsync(user.Id, "Barn Bangers");
        await _playlists.AddTracksAsync(playlist!.Value, user.Id, [tracks[0]]);

        var preview = await _importer.PreviewAsync("More_Bangers.csv", Export(3));
        var result = await _importer.AppendAsync(user.Id, preview, playlist.Value);

        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.AlreadyThere);
        Assert.Equal(3, await ItemCountAsync(playlist.Value));
    }

    [Fact]
    public async Task Appending_the_same_export_twice_changes_nothing_the_second_time()
    {
        var user = await _db.AddUserAsync("devion");
        await _db.AddTracksAsync(3);
        var playlist = await _playlists.CreateAsync(user.Id, "Barn Bangers");
        var preview = await _importer.PreviewAsync("More_Bangers.csv", Export(3));

        await _importer.AppendAsync(user.Id, preview, playlist!.Value);
        var second = await _importer.AppendAsync(user.Id, preview, playlist.Value);

        Assert.True(second.Ok);
        Assert.Equal(0, second.Added);
        Assert.Equal(3, second.AlreadyThere);
        Assert.Equal(3, await ItemCountAsync(playlist.Value));
    }

    [Fact]
    public async Task Any_member_can_append_to_a_team_playlist()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("housemate");
        var (team, error) = await _teams.CreateAsync(owner.Id, "Kitchen");
        Assert.Null(error);
        await _db.AddTeamMemberAsync(team!.Value, mate.Id);

        await _db.AddTracksAsync(2);
        var playlist = await _playlists.CreateAsync(owner.Id, "Dinner", team.Value);

        var preview = await _importer.PreviewAsync("Dinner_Extras.csv", Export(2));
        var result = await _importer.AppendAsync(mate.Id, preview, playlist!.Value);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Added);
    }

    [Fact]
    public async Task Appending_to_a_playlist_you_cannot_see_is_refused()
    {
        // The page only offers playlists you can write to, but the membership behind that list
        // can change while the file is being read.
        var owner = await _db.AddUserAsync("devion");
        var stranger = await _db.AddUserAsync("someone-else");
        var outsider = await _db.AddUserAsync("nosy");

        await _db.AddTracksAsync(2);
        var personal = await _playlists.CreateAsync(owner.Id, "Private");

        var (team, _) = await _teams.CreateAsync(owner.Id, "Kitchen");
        var shared = await _playlists.CreateAsync(owner.Id, "Dinner", team!.Value);

        var preview = await _importer.PreviewAsync("Anything.csv", Export(2));

        var intoPersonal = await _importer.AppendAsync(stranger.Id, preview, personal!.Value);
        var intoTeam = await _importer.AppendAsync(outsider.Id, preview, shared!.Value);

        Assert.False(intoPersonal.Ok);
        Assert.False(intoTeam.Ok);
        Assert.Equal(0, await ItemCountAsync(personal.Value));
        Assert.Equal(0, await ItemCountAsync(shared.Value));
    }

    [Fact]
    public async Task Appending_to_a_playlist_that_is_gone_is_refused_rather_than_throwing()
    {
        var user = await _db.AddUserAsync("devion");
        await _db.AddTracksAsync(1);

        var preview = await _importer.PreviewAsync("Anything.csv", Export(1));

        Assert.False((await _importer.AppendAsync(user.Id, preview, Guid.NewGuid())).Ok);
    }

    [Fact]
    public async Task Creating_still_makes_the_playlist_and_fills_it()
    {
        var user = await _db.AddUserAsync("devion");
        await _db.AddTracksAsync(2);

        var preview = await _importer.PreviewAsync("Barn_Bangers.csv", Export(2));
        var created = await _importer.CreateAsync(user.Id, preview, teamId: null);

        Assert.NotNull(created);
        Assert.Equal("Barn Bangers", preview.PlaylistName);
        Assert.Equal(2, await ItemCountAsync(created.Value));
    }
}
