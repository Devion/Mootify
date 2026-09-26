using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Teams;

namespace Mootify.Tests;

/// <summary>
/// Reading a playlist a page at a time.
///
/// This exists because a 200-track playlist was being loaded whole — entity graph and all — every
/// time anybody looked at one, including once per browse page in the car. The tests that matter
/// are the ones about the <i>numbers</i>: a page has to know how long the whole playlist is, or a
/// pager can't say what it isn't showing you, and "play all" has to reach past the page.
/// </summary>
public sealed class PlaylistPagingTests : IAsyncLifetime
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

    private async Task<(AppUser User, Guid PlaylistId, List<Guid> Tracks)> ListAsync(int trackCount)
    {
        var user = await _db.AddUserAsync("devion");
        var tracks = await _db.AddTracksAsync(trackCount);
        var playlistId = await _service.CreateAsync(user.Id, "Barn Bangers");

        Assert.NotNull(playlistId);
        await _service.AddTracksAsync(playlistId.Value, user.Id, tracks);

        return (user, playlistId.Value, tracks);
    }

    // ---- pages -----------------------------------------------------------

    [Fact]
    public async Task A_page_carries_the_playlist_s_totals_not_the_page_s()
    {
        // The header says "200 tracks, 10h" while showing ten of them. Computing that from the
        // page would say "10 tracks", which is the bug the whole record shape exists to avoid.
        var (user, playlistId, _) = await ListAsync(25);

        var page = await _service.GetPageAsync(playlistId, user.Id, skip: 0, take: 10);

        Assert.NotNull(page);
        Assert.Equal(10, page.Rows.Count);
        Assert.Equal(25, page.Total);
        Assert.Equal(TimeSpan.FromMinutes(75), page.TotalDuration);
    }

    [Fact]
    public async Task Pages_walk_the_playlist_in_order_and_do_not_overlap()
    {
        var (user, playlistId, tracks) = await ListAsync(25);

        var seen = new List<Guid>();
        for (var skip = 0; skip < 25; skip += 10)
        {
            var page = await _service.GetPageAsync(playlistId, user.Id, skip, 10);
            seen.AddRange(page!.Rows.Select(r => r.TrackId));
        }

        Assert.Equal(tracks, seen);
    }

    [Fact]
    public async Task Paging_metadata_answers_where_you_are()
    {
        var (user, playlistId, _) = await ListAsync(25);

        var first = await _service.GetPageAsync(playlistId, user.Id, 0, 10);
        var middle = await _service.GetPageAsync(playlistId, user.Id, 10, 10);
        var last = await _service.GetPageAsync(playlistId, user.Id, 20, 10);

        Assert.Equal((1, 3, false, true), (first!.PageNumber, first.PageCount, first.HasPrevious, first.HasNext));
        Assert.Equal((2, 3, true, true), (middle!.PageNumber, middle.PageCount, middle.HasPrevious, middle.HasNext));
        Assert.Equal((3, 3, true, false), (last!.PageNumber, last.PageCount, last.HasPrevious, last.HasNext));
        Assert.Equal(5, last.Rows.Count);
    }

    [Fact]
    public async Task A_skip_past_the_end_shows_the_last_page()
    {
        // The usual way to get here is removing the rows that were on the page you were on.
        var (user, playlistId, _) = await ListAsync(25);

        var page = await _service.GetPageAsync(playlistId, user.Id, skip: 900, take: 10);

        Assert.Equal(20, page!.Skip);
        Assert.Equal(5, page.Rows.Count);
    }

    [Fact]
    public async Task An_empty_playlist_is_one_empty_page()
    {
        var user = await _db.AddUserAsync("devion");
        var playlistId = await _service.CreateAsync(user.Id, "Nothing Yet");

        var page = await _service.GetPageAsync(playlistId!.Value, user.Id);

        Assert.Empty(page!.Rows);
        Assert.Equal(0, page.Total);
        Assert.Equal(TimeSpan.Zero, page.TotalDuration);
        Assert.Equal(1, page.PageCount);
    }

    [Fact]
    public async Task The_page_size_is_clamped()
    {
        var (user, playlistId, _) = await ListAsync(5);

        var huge = await _service.GetPageAsync(playlistId, user.Id, 0, take: 100_000);
        var zero = await _service.GetPageAsync(playlistId, user.Id, 0, take: 0);

        Assert.Equal(PlaylistService.MaxPageSize, huge!.Take);
        Assert.Equal(PlaylistService.DefaultPageSize, zero!.Take);
    }

    [Fact]
    public async Task Search_filters_the_whole_playlist_before_paging()
    {
        var user = await _db.AddUserAsync("devion");
        await _db.AddAlbumAsync("The Cowbells", "Pasture Sounds", ["Ordinary"]);
        await _db.AddAlbumAsync("Other Artist", "Hidden Album", ["Needle One", "Needle Two"]);
        var playlistId = await _service.CreateAsync(user.Id, "Barn Bangers");

        List<Guid> ids;
        await using (var db = _db.CreateDbContext())
        {
            ids = await db.Tracks.OrderBy(t => t.Title).Select(t => t.Id).ToListAsync();
        }
        await _service.AddTracksAsync(playlistId!.Value, user.Id, ids);

        var firstMatch = await _service.GetPageAsync(playlistId!.Value, user.Id, 0, 1, "needle");
        var secondMatch = await _service.GetPageAsync(playlistId.Value, user.Id, 1, 1, "needle");

        Assert.Equal(3, firstMatch!.PlaylistTotal);
        Assert.Equal(2, firstMatch.Total);
        Assert.Single(firstMatch.Rows);
        Assert.Single(secondMatch!.Rows);
        Assert.All(firstMatch.Rows.Concat(secondMatch.Rows), row => Assert.Contains("Needle", row.Title));
    }

    // ---- what a row carries ----------------------------------------------

    [Fact]
    public async Task A_row_carries_everything_both_clients_need()
    {
        // The union of what the website wants and what an ApiTrack wants, projected once — the
        // API used to re-query the same tracks to build its own shape.
        var user = await _db.AddUserAsync("devion");
        var album = await _db.AddAlbumAsync("The Cowbells", "Pasture Sounds", ["Moo Anthem"], year: 1994);
        var playlistId = await _service.CreateAsync(user.Id, "Barn Bangers");

        await using var db = _db.CreateDbContext();
        var trackId = await db.Tracks.Where(t => t.AlbumId == album.Id).Select(t => t.Id).FirstAsync();
        await _service.AddTracksAsync(playlistId!.Value, user.Id, [trackId]);

        var row = Assert.Single((await _service.GetPageAsync(playlistId.Value, user.Id))!.Rows);

        Assert.Equal("Moo Anthem", row.Title);
        Assert.Equal("The Cowbells", row.ArtistName);
        Assert.Equal("Pasture Sounds", row.AlbumTitle);
        Assert.Equal(1994, row.Year);
        Assert.Equal(album.Id, row.AlbumId);
        Assert.Equal(TimeSpan.FromMinutes(3), row.Duration);
    }

    [Fact]
    public async Task The_item_id_is_the_row_s_own()
    {
        // Legacy playlists can contain duplicates; each item still has its own identity.
        var (user, playlistId, tracks) = await ListAsync(1);
        await using (var db = _db.CreateDbContext())
        {
            db.PlaylistItems.Add(new Mootify.Data.PlaylistItem { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackId = tracks[0], SortKey = 9999, AddedByUserId = user.Id, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var rows = (await _service.GetPageAsync(playlistId, user.Id))!.Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].TrackId, rows[1].TrackId);
        Assert.NotEqual(rows[0].ItemId, rows[1].ItemId);
    }

    [Fact]
    public async Task An_absent_track_leaves_the_rows_and_the_total()
    {
        // A playlist that says 25 and shows 24 is a bug report waiting to happen.
        var (user, playlistId, tracks) = await ListAsync(25);

        await using (var db = _db.CreateDbContext())
        {
            var track = await db.Tracks.FirstAsync(t => t.Id == tracks[0]);
            track.IsPresent = false;
            await db.SaveChangesAsync();
        }

        var page = await _service.GetPageAsync(playlistId, user.Id, 0, 100);

        Assert.Equal(24, page!.Total);
        Assert.Equal(24, page.Rows.Count);
        Assert.Equal(TimeSpan.FromMinutes(72), page.TotalDuration);
    }

    // ---- access -----------------------------------------------------------

    [Fact]
    public async Task Somebody_else_s_playlist_is_indistinguishable_from_one_that_is_gone()
    {
        var (_, playlistId, _) = await ListAsync(3);
        var stranger = await _db.AddUserAsync("nosey");

        Assert.Null(await _service.GetPageAsync(playlistId, stranger.Id));
        Assert.Null(await _service.GetPageAsync(Guid.NewGuid(), stranger.Id));
    }

    [Fact]
    public async Task A_team_member_can_read_a_team_playlist_but_not_delete_it()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");

        var (teamId, error) = await _teams.CreateAsync(owner.Id, "The Barn");
        Assert.Null(error);
        await _db.AddTeamMemberAsync(teamId!.Value, mate.Id);

        var playlistId = await _service.CreateAsync(owner.Id, "Shared", teamId.Value);
        var tracks = await _db.AddTracksAsync(3);
        await _service.AddTracksAsync(playlistId!.Value, owner.Id, tracks);

        var asMember = await _service.GetPageAsync(playlistId.Value, mate.Id);
        var asOwner = await _service.GetPageAsync(playlistId.Value, owner.Id);

        Assert.Equal(3, asMember!.Total);
        Assert.Equal("The Barn", asMember.TeamName);
        Assert.False(asMember.CanDelete);
        Assert.True(asOwner!.CanDelete);
    }

    // ---- play all ---------------------------------------------------------

    [Fact]
    public async Task Track_ids_are_the_whole_playlist_in_order()
    {
        // "Play" queues the playlist, not the page — and it stays one column of GUIDs however
        // long the list is.
        var (user, playlistId, tracks) = await ListAsync(25);

        Assert.Equal(tracks, await _service.GetTrackIdsAsync(playlistId, user.Id));
    }

    [Fact]
    public async Task Track_ids_skip_what_is_no_longer_on_disk()
    {
        var (user, playlistId, tracks) = await ListAsync(5);

        await using (var db = _db.CreateDbContext())
        {
            var track = await db.Tracks.FirstAsync(t => t.Id == tracks[2]);
            track.IsPresent = false;
            await db.SaveChangesAsync();
        }

        var ids = await _service.GetTrackIdsAsync(playlistId, user.Id);

        Assert.Equal(4, ids!.Count);
        Assert.DoesNotContain(tracks[2], ids);
    }

    [Fact]
    public async Task Track_ids_are_refused_for_a_playlist_that_is_not_yours()
    {
        var (_, playlistId, _) = await ListAsync(3);
        var stranger = await _db.AddUserAsync("nosey");

        Assert.Null(await _service.GetTrackIdsAsync(playlistId, stranger.Id));
    }

    // ---- CanRead ----------------------------------------------------------

    [Fact]
    public async Task CanRead_answers_for_all_four_cases()
    {
        var owner = await _db.AddUserAsync("devion");
        var mate = await _db.AddUserAsync("robin");
        var stranger = await _db.AddUserAsync("nosey");

        var (teamId, _) = await _teams.CreateAsync(owner.Id, "The Barn");
        await _db.AddTeamMemberAsync(teamId!.Value, mate.Id);

        var personal = await _service.CreateAsync(owner.Id, "Just Mine");
        var shared = await _service.CreateAsync(owner.Id, "Shared", teamId.Value);

        Assert.True(await _service.CanReadAsync(personal!.Value, owner.Id));
        Assert.False(await _service.CanReadAsync(personal.Value, mate.Id));
        Assert.True(await _service.CanReadAsync(shared!.Value, mate.Id));
        Assert.False(await _service.CanReadAsync(shared.Value, stranger.Id));
        Assert.False(await _service.CanReadAsync(Guid.NewGuid(), owner.Id));
    }
}
