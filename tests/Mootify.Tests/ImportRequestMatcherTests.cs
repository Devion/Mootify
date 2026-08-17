using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Data;
using Mootify.Services.Notifications;
using Mootify.Services.Playlists;
using Mootify.Services.Requests;
using Mootify.Services.Teams;

namespace Mootify.Tests;

/// <summary>
/// Music somebody fetched by hand and dropped into the import folder, matched back to the
/// request that asked for it.
///
/// The failure that matters here is a false positive: closing somebody's request with the
/// wrong recording is worse than leaving it open, because nothing ever asks again.
/// </summary>
public sealed class ImportRequestMatcherTests : IAsyncLifetime
{
    private const string Root = @"C:\music";

    private TestDatabase _db = null!;
    private PlaylistService _playlists = null!;
    private ImportRequestMatcher _matcher = null!;
    private AppUser _user = null!;

    public async Task InitializeAsync()
    {
        _db = new TestDatabase();
        _playlists = new PlaylistService(_db, NullLogger<PlaylistService>.Instance);

        var notifications = new NotificationDispatcher(_db, NullLogger<NotificationDispatcher>.Instance);
        var teams = new TeamService(_db, notifications, NullLogger<TeamService>.Instance);

        _matcher = new ImportRequestMatcher(
            _db,
            new RequestFulfiller(_db, _playlists, teams, notifications, NullLogger<RequestFulfiller>.Instance),
            NullLogger<ImportRequestMatcher>.Instance);

        _user = await _db.AddUserAsync("devion");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---- fixtures --------------------------------------------------------

    /// <summary>A Track row at a real-looking path. Nothing here touches the disk — the matcher
    /// works off what the scanner already wrote down.</summary>
    private async Task<string> AddTrackAsync(
        string artist,
        string album,
        string title,
        string? recordingMbid = null,
        string? albumMbid = null)
    {
        await using var db = _db.CreateDbContext();

        var artistRow = await db.Artists.FirstOrDefaultAsync(a => a.Name == artist);
        if (artistRow is null)
        {
            artistRow = new Artist { Id = Guid.NewGuid(), Name = artist, SortName = artist };
            db.Artists.Add(artistRow);
        }

        var albumRow = await db.Albums.FirstOrDefaultAsync(a => a.Title == album && a.ArtistId == artistRow.Id);
        if (albumRow is null)
        {
            albumRow = new Album
            {
                Id = Guid.NewGuid(),
                Title = album,
                ArtistId = artistRow.Id,
                MusicBrainzId = albumMbid,
            };
            db.Albums.Add(albumRow);
        }

        var path = Path.Combine(Root, artist, album, $"{title}.mp3");

        db.Tracks.Add(new Track
        {
            Id = Guid.NewGuid(),
            Path = path,
            Title = title,
            ArtistId = artistRow.Id,
            AlbumId = albumRow.Id,
            Duration = TimeSpan.FromMinutes(3),
            AddedAt = DateTimeOffset.UtcNow,
            IsPresent = true,
            RecordingMusicBrainzId = recordingMbid,
        });

        await db.SaveChangesAsync();
        return path;
    }

    private async Task<Request> AddRequestAsync(
        RequestKind kind,
        string artist,
        string? album = null,
        string? track = null,
        RequestStatus status = RequestStatus.Searching,
        string? recordingMbid = null,
        string? albumMbid = null,
        Guid? playlistId = null)
    {
        await using var db = _db.CreateDbContext();

        var request = new Request
        {
            Id = Guid.NewGuid(),
            RequesterId = _user.Id,
            Kind = kind,
            Status = status,
            Query = track ?? album ?? artist,
            ArtistName = artist,
            AlbumTitle = album,
            TrackTitle = track,
            RecordingMusicBrainzId = recordingMbid,
            AlbumMusicBrainzId = albumMbid,
            TargetPlaylistId = playlistId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private async Task<RequestStatus> StatusOf(Request request)
    {
        await using var db = _db.CreateDbContext();
        return (await db.Requests.SingleAsync(r => r.Id == request.Id)).Status;
    }

    // ---- matching --------------------------------------------------------

    [Fact]
    public async Task A_dropped_track_closes_the_request_that_asked_for_it()
    {
        var path = await AddTrackAsync("Radiohead", "OK Computer", "Airbag");
        var request = await AddRequestAsync(RequestKind.Track, "Radiohead", track: "Airbag");

        var summary = await _matcher.MatchAsync([path]);

        Assert.Equal(1, summary.Requests);
        Assert.Equal(1, summary.Tracks);
        Assert.Equal(RequestStatus.Available, await StatusOf(request));
    }

    [Fact]
    public async Task A_request_Lidarr_gave_up_on_is_exactly_the_one_to_revive()
    {
        // NotFound is what most hand-fetched files are answering: Lidarr couldn't get it, so
        // somebody went and got it. Skipping those would miss the main case.
        var path = await AddTrackAsync("Radiohead", "OK Computer", "Airbag");
        var request = await AddRequestAsync(
            RequestKind.Track, "Radiohead", track: "Airbag", status: RequestStatus.NotFound);

        await _matcher.MatchAsync([path]);

        Assert.Equal(RequestStatus.Available, await StatusOf(request));

        await using var db = _db.CreateDbContext();
        var row = await db.Requests.SingleAsync(r => r.Id == request.Id);

        // "Nothing found after a week of searching" next to music that is right there.
        Assert.Null(row.FailureReason);
        Assert.NotNull(row.CompletedAt);
    }

    [Fact]
    public async Task Decoration_on_either_side_does_not_stop_the_match()
    {
        // The request stores the title as MusicBrainz spells it; the file carries whatever the
        // tagger felt like. Same normaliser as the CSV import, for the same reason.
        var path = await AddTrackAsync("Linkin Park", "Meteora", "Numb");
        var request = await AddRequestAsync(
            RequestKind.Track, "Linkin Park", track: "Numb (2011 Remaster)");

        var summary = await _matcher.MatchAsync([path]);

        Assert.Equal(1, summary.Requests);
    }

    [Fact]
    public async Task A_cover_by_somebody_else_is_not_the_song_that_was_asked_for()
    {
        var path = await AddTrackAsync("Pub Covers Band", "Tribute", "Bohemian Rhapsody");
        var request = await AddRequestAsync(RequestKind.Track, "Queen", track: "Bohemian Rhapsody");

        var summary = await _matcher.MatchAsync([path]);

        Assert.Equal(0, summary.Requests);
        Assert.Equal(RequestStatus.Searching, await StatusOf(request));
    }

    [Fact]
    public async Task The_recording_id_wins_over_the_title()
    {
        // Two tracks with the same title in one drop. The MBID is the only thing that knows
        // which one was asked for.
        await AddTrackAsync("Queen", "A Night at the Opera", "Bohemian Rhapsody", recordingMbid: "wrong-one");
        var right = await AddTrackAsync("Queen", "Live Killers", "Bohemian Rhapsody", recordingMbid: "right-one");

        var request = await AddRequestAsync(
            RequestKind.Track, "Queen", track: "Bohemian Rhapsody", recordingMbid: "right-one");

        var playlist = await _playlists.CreateAsync(_user.Id, "Mine");
        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Requests.SingleAsync(r => r.Id == request.Id);
            row.TargetPlaylistId = playlist;
            await db.SaveChangesAsync();
        }

        await _matcher.MatchAsync([right]);

        await using var check = _db.CreateDbContext();
        var queued = await check.PlaylistItems
            .Where(i => i.PlaylistId == playlist)
            .Select(i => i.Track!.Album!.Title)
            .ToListAsync();

        Assert.Equal(["Live Killers"], queued);
    }

    [Fact]
    public async Task An_album_request_takes_the_whole_album()
    {
        var first = await AddTrackAsync("Radiohead", "OK Computer", "Airbag");
        await AddTrackAsync("Radiohead", "OK Computer", "Karma Police");
        await AddTrackAsync("Radiohead", "OK Computer", "No Surprises");

        var request = await AddRequestAsync(RequestKind.Album, "Radiohead", album: "OK Computer");

        var summary = await _matcher.MatchAsync([first]);

        Assert.Equal(1, summary.Requests);

        // Half an album still completes the request — the music arrived, and the rest is a gap
        // in the library rather than an unanswered request.
        Assert.Equal(3, summary.Tracks);
    }

    [Fact]
    public async Task Music_that_was_already_there_completes_nothing()
    {
        // The candidate query works by folder, so dropping one file into an existing artist
        // folder pulls that artist's whole catalogue back. A match has to be seeded by a file
        // that actually just arrived, or an old album silently closes a new request.
        await AddTrackAsync("Radiohead", "OK Computer", "Airbag");
        var dropped = await AddTrackAsync("Radiohead", "OK Computer", "Karma Police");

        var request = await AddRequestAsync(RequestKind.Track, "Radiohead", track: "Airbag");

        var summary = await _matcher.MatchAsync([dropped]);

        Assert.Equal(0, summary.Requests);
        Assert.Equal(RequestStatus.Searching, await StatusOf(request));
    }

    [Fact]
    public async Task An_already_delivered_request_is_left_alone()
    {
        var path = await AddTrackAsync("Radiohead", "OK Computer", "Airbag");
        var request = await AddRequestAsync(
            RequestKind.Track, "Radiohead", track: "Airbag", status: RequestStatus.Available);

        var summary = await _matcher.MatchAsync([path]);

        Assert.Equal(0, summary.Requests);
    }

    // ---- what completing actually does -----------------------------------

    [Fact]
    public async Task The_track_lands_in_the_playlist_the_request_was_made_against()
    {
        var playlist = await _playlists.CreateAsync(_user.Id, "Road trip");
        var path = await AddTrackAsync("Radiohead", "OK Computer", "Airbag");
        await AddRequestAsync(RequestKind.Track, "Radiohead", track: "Airbag", playlistId: playlist);

        await _matcher.MatchAsync([path]);

        await using var db = _db.CreateDbContext();
        var titles = await db.PlaylistItems
            .Where(i => i.PlaylistId == playlist)
            .Select(i => i.Track!.Title)
            .ToListAsync();

        Assert.Equal(["Airbag"], titles);
    }

    [Fact]
    public async Task The_requester_is_told_it_arrived()
    {
        // Same notification as a Lidarr completion — from where the requester is standing
        // nothing about this was different.
        var path = await AddTrackAsync("Radiohead", "OK Computer", "Airbag");
        await AddRequestAsync(RequestKind.Track, "Radiohead", track: "Airbag");

        await _matcher.MatchAsync([path]);

        await using var db = _db.CreateDbContext();
        var notification = await db.Notifications.SingleAsync(n => n.UserId == _user.Id);

        Assert.Equal(NotificationType.RequestAvailable, notification.Type);
        Assert.Contains("Airbag", notification.Title);
    }

    [Fact]
    public async Task Nothing_imported_means_no_work_and_no_queries()
    {
        Assert.Equal(ImportMatchSummary.Nothing, await _matcher.MatchAsync([]));
    }
}
