using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Recommendations;

namespace Mootify.Tests;

/// <summary>
/// Turning play history into suggestions.
///
/// Most of these are about the feature <b>declining</b> to do something: not suggesting anything
/// without enough history, not counting a skip as a listen, not offering the song that just
/// finished, not handing back one artist's discography. That is where a recommender on a
/// household library goes wrong — it will always find something to say, and most of what it can
/// say is music the person owns and actively doesn't play.
/// </summary>
public sealed class TasteServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private TasteService _service = null!;
    private AppUser _user = null!;

    public async Task InitializeAsync()
    {
        _db = new TestDatabase();
        _service = new TasteService(_db, NullLogger<TasteService>.Instance);
        _user = await _db.AddUserAsync("devion");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---- fixtures --------------------------------------------------------

    /// <summary>An album whose tracks all carry a genre, since that is half of what taste is built from.</summary>
    private async Task<List<Guid>> AddAlbumAsync(
        string artist, string album, string? genre, int trackCount, TimeSpan? length = null)
    {
        await using var db = _db.CreateDbContext();

        var artistRow = await db.Artists.FirstOrDefaultAsync(a => a.Name == artist);
        if (artistRow is null)
        {
            artistRow = new Artist { Id = Guid.NewGuid(), Name = artist, SortName = artist };
            db.Artists.Add(artistRow);
        }

        var albumRow = new Album { Id = Guid.NewGuid(), Title = album, ArtistId = artistRow.Id };
        db.Albums.Add(albumRow);

        var ids = new List<Guid>();
        for (var i = 0; i < trackCount; i++)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Path = $@"C:\music\{artist}\{album}\{i:00}.mp3",
                Title = $"{album} {i}",
                ArtistId = artistRow.Id,
                AlbumId = albumRow.Id,
                Genre = genre,
                Duration = length ?? TimeSpan.FromMinutes(4),
                AddedAt = DateTimeOffset.UtcNow,
                IsPresent = true,
            };

            db.Tracks.Add(track);
            ids.Add(track.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>
    /// A listen. <paramref name="completion"/> is the fraction of the track heard, which is the
    /// thing that separates a listen from a skip.
    /// </summary>
    private async Task PlayAsync(
        IEnumerable<Guid> trackIds, double completion = 1.0, TimeSpan? ago = null, Guid? userId = null)
    {
        await using var db = _db.CreateDbContext();

        foreach (var trackId in trackIds)
        {
            var ticks = await db.Tracks.Where(t => t.Id == trackId)
                .Select(t => t.DurationTicks).FirstAsync();

            db.PlayEvents.Add(new PlayEvent
            {
                UserId = userId ?? _user.Id,
                TrackId = trackId,
                PlayedAt = DateTimeOffset.UtcNow - (ago ?? TimeSpan.Zero),
                SecondsPlayed = TimeSpan.FromTicks(ticks).TotalSeconds * completion,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Enough distinct listens to get past the cold-start gate, by one artist.</summary>
    private async Task<List<Guid>> ListenedEnoughAsync(string artist = "Nirvana", string? genre = "Grunge")
    {
        var ids = await AddAlbumAsync(artist, $"{artist} Album", genre, TasteService.MinimumTracksHeard);
        await PlayAsync(ids);
        return ids;
    }

    // ---- the cold start --------------------------------------------------

    [Fact]
    public async Task An_account_that_has_played_nothing_has_no_profile()
    {
        var profile = await _service.GetProfileAsync(_user.Id);

        Assert.False(profile.IsReady);
        Assert.Equal(0, profile.TracksHeard);
        Assert.Equal(TasteService.MinimumTracksHeard, profile.TracksStillNeeded);
        Assert.Empty(profile.Artists);
    }

    [Fact]
    public async Task Nothing_is_suggested_before_the_threshold()
    {
        // The whole point. A library full of music nobody has played is exactly the situation
        // where a recommender that always answers answers with rubbish.
        var ids = await AddAlbumAsync("Nirvana", "Nevermind", "Grunge", 40);
        await PlayAsync(ids.Take(TasteService.MinimumTracksHeard - 1));

        Assert.False((await _service.GetProfileAsync(_user.Id)).IsReady);
        Assert.Empty(await _service.SuggestAsync(_user.Id, 10));
    }

    [Fact]
    public async Task One_more_listen_is_what_turns_it_on()
    {
        var ids = await AddAlbumAsync("Nirvana", "Nevermind", "Grunge", 40);
        await PlayAsync(ids.Take(TasteService.MinimumTracksHeard - 1));

        Assert.Equal(1, (await _service.GetProfileAsync(_user.Id)).TracksStillNeeded);

        await PlayAsync(ids.Skip(TasteService.MinimumTracksHeard - 1).Take(1));

        var profile = await _service.GetProfileAsync(_user.Id);
        Assert.True(profile.IsReady);
        Assert.Equal(0, profile.TracksStillNeeded);
    }

    [Fact]
    public async Task Playing_the_same_song_over_and_over_is_not_a_profile()
    {
        // The gate counts distinct songs, not plays — otherwise one track on repeat looks like a
        // week of listening and the whole library gets recommended off the back of it.
        var ids = await AddAlbumAsync("Nirvana", "Nevermind", "Grunge", 40);

        for (var i = 0; i < 50; i++) await PlayAsync(ids.Take(1));

        var profile = await _service.GetProfileAsync(_user.Id);

        Assert.Equal(1, profile.TracksHeard);
        Assert.Equal(50, profile.PlaysCounted);
        Assert.False(profile.IsReady);
    }

    // ---- what counts as a listen -----------------------------------------

    [Fact]
    public async Task A_skip_does_not_count()
    {
        // Skipping through an album must not teach the profile that you love it.
        var ids = await AddAlbumAsync("Nickelback", "Skipped", "Rock", 30);
        await PlayAsync(ids, completion: 0.05);

        var profile = await _service.GetProfileAsync(_user.Id);

        Assert.Equal(0, profile.TracksHeard);
        Assert.False(profile.IsReady);
    }

    [Fact]
    public async Task A_half_listen_counts_for_less_than_a_full_one()
    {
        var loved = await AddAlbumAsync("Nirvana", "Loved", "Grunge", 10);
        var tolerated = await AddAlbumAsync("Creed", "Tolerated", "Rock", 10);

        await PlayAsync(loved, completion: 1.0);
        await PlayAsync(tolerated, completion: 0.3);

        var profile = await _service.GetProfileAsync(_user.Id);

        Assert.Equal("Nirvana", profile.Artists[0].Name);
        Assert.True(profile.Artists[0].Weight > profile.Artists[1].Weight * 2);
    }

    [Fact]
    public void Completion_is_a_fraction_and_a_missing_duration_counts_as_a_full_listen()
    {
        Assert.Equal(0.5, TasteService.Completion(120, TimeSpan.FromMinutes(4).Ticks), 3);
        Assert.Equal(1, TasteService.Completion(600, TimeSpan.FromMinutes(4).Ticks));

        // Untagged music must not be silently ignored just because we can't measure it.
        Assert.Equal(1, TasteService.Completion(30, 0));
    }

    // ---- recency ----------------------------------------------------------

    [Fact]
    public void A_play_decays_by_half_over_the_half_life()
    {
        Assert.Equal(1, TasteService.Decay(TimeSpan.Zero));
        Assert.Equal(0.5, TasteService.Decay(TasteService.HalfLife), 3);
        Assert.Equal(0.25, TasteService.Decay(TasteService.HalfLife * 2), 3);
    }

    [Fact]
    public async Task This_month_outranks_last_year_without_erasing_it()
    {
        var recent = await AddAlbumAsync("Nirvana", "Recent", "Grunge", 10);
        var old = await AddAlbumAsync("Oasis", "Old", "Britpop", 10);

        await PlayAsync(recent);
        await PlayAsync(old, ago: TimeSpan.FromDays(300));

        var profile = await _service.GetProfileAsync(_user.Id);

        Assert.Equal("Nirvana", profile.Artists[0].Name);

        // Still there, just quieter — taste moves, it doesn't reset.
        Assert.Contains(profile.Artists, a => a.Name == "Oasis");
        Assert.True(profile.Artists.Single(a => a.Name == "Oasis").Weight > 0);
    }

    [Fact]
    public async Task Anything_past_the_history_window_is_not_read_at_all()
    {
        var ancient = await AddAlbumAsync("Oasis", "Ancient", "Britpop", 20);
        await PlayAsync(ancient, ago: TasteService.History + TimeSpan.FromDays(30));

        Assert.Equal(0, (await _service.GetProfileAsync(_user.Id)).TracksHeard);
    }

    // ---- genres -----------------------------------------------------------

    [Fact]
    public async Task Genres_are_counted_and_ranked()
    {
        await PlayAsync(await AddAlbumAsync("Nirvana", "A", "Grunge", 10));
        await PlayAsync(await AddAlbumAsync("Pearl Jam", "B", "Grunge", 8));
        await PlayAsync(await AddAlbumAsync("Oasis", "C", "Britpop", 3));

        var profile = await _service.GetProfileAsync(_user.Id);

        Assert.Equal("Grunge", profile.Genres[0].Name);
        Assert.Equal("Britpop", profile.Genres[1].Name);
    }

    [Fact]
    public async Task Genre_casing_and_spacing_is_one_genre()
    {
        // Real tags say "Rock", "rock" and " Rock ", and they are not three things.
        await PlayAsync(await AddAlbumAsync("A Band", "A", "Rock", 6));
        await PlayAsync(await AddAlbumAsync("B Band", "B", "rock", 6));
        await PlayAsync(await AddAlbumAsync("C Band", "C", " Rock ", 6));

        Assert.Single((await _service.GetProfileAsync(_user.Id)).Genres);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("(17)", null)]   // an ID3v1 numeric leftover
    [InlineData("17", null)]
    [InlineData(" Rock ", "Rock")]
    [InlineData("Drum & Bass", "Drum & Bass")]
    public void Genre_normalisation_drops_what_cannot_be_acted_on(string? raw, string? expected) =>
        Assert.Equal(expected, TasteService.NormaliseGenre(raw));

    [Fact]
    public async Task Music_with_no_genre_tag_still_builds_an_artist_profile()
    {
        // Most real libraries are patchily tagged; losing them would be losing the library.
        await PlayAsync(await AddAlbumAsync("Untagged Band", "A", null, 20));

        var profile = await _service.GetProfileAsync(_user.Id);

        Assert.True(profile.IsReady);
        Assert.Equal("Untagged Band", Assert.Single(profile.Artists).Name);
        Assert.Empty(profile.Genres);
    }

    // ---- suggestions ------------------------------------------------------

    [Fact]
    public async Task Suggestions_favour_what_the_account_listens_to()
    {
        await ListenedEnoughAsync("Nirvana", "Grunge");

        // Two pools of unheard music: one by the artist they play, one by somebody unrelated.
        await AddAlbumAsync("Nirvana", "Unheard Nirvana", "Grunge", 30);
        await AddAlbumAsync("Unrelated Band", "Unheard Other", "Polka", 30);

        var suggestions = await _service.SuggestAsync(_user.Id, 20, seed: 1);

        Assert.NotEmpty(suggestions);
        Assert.Contains(suggestions, s => s.ArtistName == "Nirvana");
    }

    [Fact]
    public async Task A_genre_reaches_artists_that_have_never_been_played()
    {
        // This is how somebody hears something new: the genre they play, by a band they don't.
        await ListenedEnoughAsync("Nirvana", "Grunge");
        await AddAlbumAsync("Mudhoney", "Never Played", "Grunge", 30);

        var suggestions = await _service.SuggestAsync(_user.Id, 25, seed: 3);

        Assert.Contains(suggestions, s => s.ArtistName == "Mudhoney");
    }

    [Fact]
    public async Task No_more_than_two_songs_by_one_artist()
    {
        // Top-N scoring would return one discography, which is a great recommendation and a
        // useless playlist.
        await ListenedEnoughAsync("Nirvana", "Grunge");
        await AddAlbumAsync("Nirvana", "Lots More", "Grunge", 60);

        var suggestions = await _service.SuggestAsync(_user.Id, 25, seed: 7);

        foreach (var group in suggestions.GroupBy(s => s.ArtistName))
        {
            Assert.True(
                group.Count() <= TasteService.MaxPerArtist,
                $"{group.Key} appeared {group.Count()} times");
        }
    }

    [Fact]
    public async Task What_was_just_played_is_not_suggested_back()
    {
        // "Play me something" never means the song that just finished.
        var heard = await ListenedEnoughAsync("Nirvana", "Grunge");
        await AddAlbumAsync("Nirvana", "Unheard", "Grunge", 40);

        var suggestions = await _service.SuggestAsync(_user.Id, 25, seed: 11);

        Assert.DoesNotContain(suggestions, s => heard.Contains(s.TrackId));
    }

    [Fact]
    public async Task Something_played_long_enough_ago_is_offered_again()
    {
        var ids = await AddAlbumAsync("Nirvana", "Nevermind", "Grunge", 30);

        // Enough history to be ready, but all of it outside the cooldown.
        await PlayAsync(ids, ago: TasteService.RepeatCooldown + TimeSpan.FromDays(7));

        var suggestions = await _service.SuggestAsync(_user.Id, 25, seed: 13);

        Assert.Contains(suggestions, s => ids.Contains(s.TrackId));
    }

    [Fact]
    public async Task What_the_caller_already_has_is_left_out()
    {
        await ListenedEnoughAsync("Nirvana", "Grunge");
        var unheard = await AddAlbumAsync("Nirvana", "Unheard", "Grunge", 40);

        var already = unheard.Take(20).ToList();
        var suggestions = await _service.SuggestAsync(_user.Id, 15, exclude: already, seed: 17);

        Assert.DoesNotContain(suggestions, s => already.Contains(s.TrackId));
    }

    [Fact]
    public async Task Absent_tracks_are_never_suggested()
    {
        await ListenedEnoughAsync("Nirvana", "Grunge");
        var gone = await AddAlbumAsync("Nirvana", "Deleted", "Grunge", 30);

        await using (var db = _db.CreateDbContext())
        {
            foreach (var track in await db.Tracks.Where(t => gone.Contains(t.Id)).ToListAsync())
            {
                track.IsPresent = false;
            }
            await db.SaveChangesAsync();
        }

        var suggestions = await _service.SuggestAsync(_user.Id, 25, seed: 19);

        Assert.DoesNotContain(suggestions, s => gone.Contains(s.TrackId));
    }

    [Fact]
    public async Task Every_suggestion_can_say_why_it_is_there()
    {
        await ListenedEnoughAsync("Nirvana", "Grunge");
        await AddAlbumAsync("Nirvana", "Unheard", "Grunge", 30);

        var suggestions = await _service.SuggestAsync(_user.Id, 10, seed: 23);

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        Assert.Contains(suggestions, s => s.Reason.Contains("Nirvana"));
    }

    [Fact]
    public async Task The_same_button_twice_gives_a_different_answer()
    {
        // Otherwise "Surprise me" is a static list with a misleading name.
        await ListenedEnoughAsync("Nirvana", "Grunge");
        await AddAlbumAsync("Nirvana", "Unheard", "Grunge", 60);
        await AddAlbumAsync("Mudhoney", "Also Unheard", "Grunge", 60);

        var first = await _service.SuggestAsync(_user.Id, 15, seed: 1);
        var second = await _service.SuggestAsync(_user.Id, 15, seed: 2);

        Assert.NotEqual(
            first.Select(s => s.TrackId).ToList(),
            second.Select(s => s.TrackId).ToList());
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_answer()
    {
        await ListenedEnoughAsync("Nirvana", "Grunge");
        await AddAlbumAsync("Nirvana", "Unheard", "Grunge", 40);

        var first = await _service.SuggestAsync(_user.Id, 10, seed: 42);
        var second = await _service.SuggestAsync(_user.Id, 10, seed: 42);

        Assert.Equal(first.Select(s => s.TrackId), second.Select(s => s.TrackId));
    }

    [Fact]
    public async Task Asking_for_nothing_returns_nothing()
    {
        await ListenedEnoughAsync();
        Assert.Empty(await _service.SuggestAsync(_user.Id, 0));
    }

    [Fact]
    public async Task A_library_with_nothing_left_to_offer_returns_nothing_rather_than_repeats()
    {
        // Everything present has been played inside the cooldown. Empty is the honest answer;
        // padding it with what they just heard is what makes people stop pressing the button.
        var ids = await AddAlbumAsync("Nirvana", "Only Album", "Grunge", 20);
        await PlayAsync(ids);

        Assert.Empty(await _service.SuggestAsync(_user.Id, 10));
    }

    // ---- one profile per person ------------------------------------------

    [Fact]
    public async Task Somebody_elses_listening_is_not_mine()
    {
        var other = await _db.AddUserAsync("robin");
        var ids = await AddAlbumAsync("Polka Kings", "Not Mine", "Polka", 30);

        await PlayAsync(ids, userId: other.Id);

        var mine = await _service.GetProfileAsync(_user.Id);

        Assert.Equal(0, mine.TracksHeard);
        Assert.True((await _service.GetProfileAsync(other.Id)).IsReady);
    }
}
