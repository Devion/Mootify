using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Playback;
using Mootify.Services.Playlists;
using Mootify.Services.Recommendations;
using Mootify.Services.Settings;

namespace Mootify.Tests;

/// <summary>
/// The two things the player does that aren't playing audio: writing down what was listened to,
/// and topping the queue up when it runs out.
///
/// Both were previously untestable — they sit next to an <c>&lt;audio&gt;</c> element and needed a
/// browser with a sound device. <see cref="FakeJsRuntime"/> is what makes them reachable, and they
/// are worth reaching: the play history is the foundation everything else is built on, and a queue
/// that grows when it shouldn't is music somebody didn't ask for.
/// </summary>
public sealed class PlayerServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private FakeJsRuntime _js = null!;
    private AppUser _user = null!;

    public async Task InitializeAsync()
    {
        _db = new TestDatabase();
        _js = new FakeJsRuntime();
        _user = await _db.AddUserAsync("devion");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private PlayerService CreatePlayer()
    {
        var playlists = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);
        var listening = new ListeningService(_db, playlists, NullLogger<ListeningService>.Instance);
        var taste = new TasteService(_db, NullLogger<TasteService>.Instance);
        var preferences = new PreferenceService(_db);
        var currentUser = new CurrentUser(new FakeAuthStateProvider(_user.Id));

        return new PlayerService(
            _js, _db, listening, taste, preferences, currentUser, NullLogger<PlayerService>.Instance);
    }

    /// <summary>Tracks long enough that a "listen" and a "skip" are different numbers.</summary>
    private async Task<List<Guid>> AddTracksAsync(
        int count, string artist = "The Cowbells", string? genre = "Grunge")
    {
        await using var db = _db.CreateDbContext();

        var artistRow = await db.Artists.FirstOrDefaultAsync(a => a.Name == artist);
        if (artistRow is null)
        {
            artistRow = new Artist { Id = Guid.NewGuid(), Name = artist, SortName = artist };
            db.Artists.Add(artistRow);
        }

        var album = new Album { Id = Guid.NewGuid(), Title = $"{artist} Album", ArtistId = artistRow.Id };
        db.Albums.Add(album);

        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Path = $"C:/music/{artist}/{album.Title}/{Guid.NewGuid():n}.mp3",
                Title = $"{artist} {i}",
                ArtistId = artistRow.Id,
                AlbumId = album.Id,
                Genre = genre,
                Duration = TimeSpan.FromMinutes(4),
                AddedAt = DateTimeOffset.UtcNow,
                IsPresent = true,
            };

            db.Tracks.Add(track);
            ids.Add(track.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>Enough listening history that the suggester will speak at all.</summary>
    private async Task SeedHistoryAsync(IEnumerable<Guid> trackIds)
    {
        await using var db = _db.CreateDbContext();

        foreach (var id in trackIds)
        {
            db.PlayEvents.Add(new PlayEvent
            {
                UserId = _user.Id,
                TrackId = id,
                // Long enough ago to be outside the repeat cooldown, so these can still be
                // suggested back — the point here is the profile, not the exclusions.
                PlayedAt = DateTimeOffset.UtcNow - TasteService.RepeatCooldown - TimeSpan.FromDays(1),
                SecondsPlayed = TimeSpan.FromMinutes(4).TotalSeconds,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<PlayEvent>> PlayEventsAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.PlayEvents.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
    }

    // ---- writing down what was played ------------------------------------

    [Fact]
    public async Task Moving_to_the_next_track_records_the_one_just_left()
    {
        // The website never did this — PlayEvent was written only by the Android app — so anybody
        // listening on the website had no history and everything built on history had nothing.
        var tracks = await AddTracksAsync(2);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0);
        await player.OnTimeUpdate(position: 100, duration: 240);
        await player.NextAsync();

        var recorded = Assert.Single(await PlayEventsAsync());

        Assert.Equal(tracks[0], recorded.TrackId);
        Assert.Equal(_user.Id, recorded.UserId);
        Assert.Equal(100, recorded.SecondsPlayed);
    }

    [Fact]
    public async Task A_track_barely_started_is_not_recorded()
    {
        // Below the threshold it was a skip, and the history is better off without it.
        var tracks = await AddTracksAsync(2);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0);
        await player.OnTimeUpdate(position: 2, duration: 240);
        await player.NextAsync();

        Assert.Empty(await PlayEventsAsync());
    }

    [Fact]
    public async Task Every_track_of_a_session_is_recorded_once()
    {
        var tracks = await AddTracksAsync(3);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0);

        foreach (var _ in tracks)
        {
            await player.OnTimeUpdate(position: 200, duration: 240);
            await player.NextAsync();
        }

        var recorded = await PlayEventsAsync();

        // Three tracks, three events, in order, and no duplicates from the queue ending.
        Assert.Equal(3, recorded.Count);
        Assert.Equal(tracks, recorded.Select(p => p.TrackId));
    }

    [Fact]
    public async Task Starting_a_different_queue_records_what_was_interrupted()
    {
        var first = await AddTracksAsync(1, "Band A");
        var second = await AddTracksAsync(1, "Band B");
        var player = CreatePlayer();

        await player.PlayQueueAsync(first, 0);
        await player.OnTimeUpdate(position: 120, duration: 240);

        // Walking away mid-song to play something else still listened to half of it.
        await player.PlayQueueAsync(second, 0);

        var recorded = Assert.Single(await PlayEventsAsync());
        Assert.Equal(first[0], recorded.TrackId);
        Assert.Equal(120, recorded.SecondsPlayed);
    }

    [Fact]
    public async Task The_last_track_of_a_session_is_recorded_on_teardown()
    {
        // Nothing follows it, so this is the only chance to write it down.
        var tracks = await AddTracksAsync(1);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0);
        await player.OnTimeUpdate(position: 180, duration: 240);

        await player.DisposeAsync();

        var recorded = Assert.Single(await PlayEventsAsync());
        Assert.Equal(tracks[0], recorded.TrackId);
        Assert.Equal(180, recorded.SecondsPlayed);
    }

    [Fact]
    public async Task Position_from_a_track_already_left_is_not_charged_to_the_next_one()
    {
        // By the time a transition is handled the position belongs to the new song, which is why
        // the player keeps its own count rather than reading Position at flush time.
        var tracks = await AddTracksAsync(2);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0);
        await player.OnTimeUpdate(position: 200, duration: 240);
        await player.NextAsync();

        // A tick for the *second* track, then end the session.
        await player.OnTimeUpdate(position: 10, duration: 240);
        await player.DisposeAsync();

        var recorded = await PlayEventsAsync();

        Assert.Equal(200, recorded[0].SecondsPlayed);
        // Ten seconds is above the reporting floor, and belongs to track two.
        Assert.Equal(tracks[1], recorded[1].TrackId);
        Assert.Equal(10, recorded[1].SecondsPlayed);
    }

    // ---- running out -----------------------------------------------------

    [Fact]
    public async Task A_finished_queue_keeps_going_when_there_is_history_to_go_on()
    {
        var heard = await AddTracksAsync(TasteService.MinimumTracksHeard);
        await SeedHistoryAsync(heard);
        await AddTracksAsync(20, "Another Band");

        var player = CreatePlayer();
        var queue = await AddTracksAsync(1, "Starter Band");

        await player.PlayQueueAsync(queue, 0);
        await player.NextAsync();

        Assert.True(player.Queue.Count > 1, "the queue should have been topped up");
        Assert.NotNull(player.Current);
        Assert.True(player.IsPlaying);
    }

    [Fact]
    public async Task A_finished_queue_stops_when_there_is_not_enough_history()
    {
        // The cold start again, and the reason auto-continue can default to on: with nothing to
        // go on it does nothing, and the music stops the way it always did.
        await AddTracksAsync(40, "Never Played");

        var player = CreatePlayer();
        var queue = await AddTracksAsync(1, "Starter Band");

        await player.PlayQueueAsync(queue, 0);
        await player.NextAsync();

        Assert.Single(player.Queue);
        Assert.False(player.IsPlaying);
        Assert.Contains("pause", _js.Names);
    }

    [Fact]
    public async Task Turning_keep_playing_off_stops_the_queue_growing()
    {
        var heard = await AddTracksAsync(TasteService.MinimumTracksHeard);
        await SeedHistoryAsync(heard);
        await AddTracksAsync(20, "Another Band");

        await new PreferenceService(_db).SetAutoContinueAsync(_user.Id, false);

        var player = CreatePlayer();
        var queue = await AddTracksAsync(1, "Starter Band");

        await player.PlayQueueAsync(queue, 0);
        await player.NextAsync();

        Assert.Single(player.Queue);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task A_surprise_queue_grows_even_with_keep_playing_off()
    {
        // "Surprise me" is asked for as an endless thing, so it tops itself up whatever the
        // preference says — the preference is about playlists ending, not about this.
        var heard = await AddTracksAsync(TasteService.MinimumTracksHeard);
        await SeedHistoryAsync(heard);
        await AddTracksAsync(20, "Another Band");

        await new PreferenceService(_db).SetAutoContinueAsync(_user.Id, false);

        var player = CreatePlayer();
        var queue = await AddTracksAsync(1, "Starter Band");

        await player.PlayQueueAsync(queue, 0, alwaysGrow: true);
        await player.NextAsync();

        Assert.True(player.Queue.Count > 1, "a surprise queue should keep going");
    }

    [Fact]
    public async Task Repeating_a_playlist_never_reaches_for_suggestions()
    {
        // Somebody looping a playlist has said what they want to hear.
        var heard = await AddTracksAsync(TasteService.MinimumTracksHeard);
        await SeedHistoryAsync(heard);
        await AddTracksAsync(20, "Another Band");

        var player = CreatePlayer();
        var queue = await AddTracksAsync(2, "Starter Band");

        await player.PlayQueueAsync(queue, 0);
        await player.CycleRepeatAsync();   // Off -> All

        await player.NextAsync();
        await player.NextAsync();

        Assert.Equal(2, player.Queue.Count);
        Assert.Equal(queue[0], player.Current!.Id);
    }

    [Fact]
    public async Task A_grown_queue_does_not_repeat_what_is_already_in_it()
    {
        var heard = await AddTracksAsync(TasteService.MinimumTracksHeard);
        await SeedHistoryAsync(heard);
        await AddTracksAsync(30, "Another Band");

        var player = CreatePlayer();
        var queue = await AddTracksAsync(1, "Starter Band");

        await player.PlayQueueAsync(queue, 0);
        await player.NextAsync();

        var ids = player.Queue.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ---- the source of the queue -----------------------------------------

    [Fact]
    public async Task A_playlist_queue_remembers_where_it_came_from()
    {
        // What "listening along" broadcasts against.
        var tracks = await AddTracksAsync(2);
        var playlistId = Guid.NewGuid();
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0, playlistId);

        Assert.Equal(playlistId, player.SourcePlaylistId);
    }

    [Fact]
    public async Task An_album_queue_has_no_source_playlist()
    {
        var tracks = await AddTracksAsync(2);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0);

        Assert.Null(player.SourcePlaylistId);
    }

    // ---- pressing a row --------------------------------------------------

    [Fact]
    public async Task Pressing_the_row_that_is_playing_pauses_rather_than_restarting()
    {
        var tracks = await AddTracksAsync(3);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 1);
        await player.OnTimeUpdate(position: 90, duration: 240);

        Assert.True(player.IsCurrent(tracks[1]));

        await player.PressRowAsync(tracks, 1);

        Assert.False(player.IsPlaying);
        Assert.Contains("pause", _js.Names);

        // Still on the same track, and the position was never reset.
        Assert.Equal(tracks[1], player.Current!.Id);
        Assert.Equal(90, player.Position);
    }

    [Fact]
    public async Task Pressing_a_different_row_starts_the_list_from_there()
    {
        var tracks = await AddTracksAsync(3);
        var player = CreatePlayer();

        await player.PlayQueueAsync(tracks, 0);
        await player.PressRowAsync(tracks, 2);

        Assert.Equal(tracks[2], player.Current!.Id);
        Assert.True(player.IsPlaying);
    }

    [Fact]
    public async Task Pressing_a_row_of_a_surprise_run_keeps_the_endlessness()
    {
        // Picking the fourth suggestion is the same request as pressing Play above it, started
        // later — so the row button has to carry alwaysGrow the way the Play button does.
        var heard = await AddTracksAsync(TasteService.MinimumTracksHeard);
        await SeedHistoryAsync(heard);
        await AddTracksAsync(20, "Another Band");

        await new PreferenceService(_db).SetAutoContinueAsync(_user.Id, false);

        var player = CreatePlayer();
        var queue = await AddTracksAsync(1, "Starter Band");

        await player.PressRowAsync(queue, 0, alwaysGrow: true);
        await player.NextAsync();

        Assert.True(player.Queue.Count > 1, "a surprise queue should keep going");
    }
}
