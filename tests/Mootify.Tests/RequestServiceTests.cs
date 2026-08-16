using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Lidarr;
using Mootify.Services.Requests;
using Mootify.Services.Settings;

namespace Mootify.Tests;

/// <summary>
/// Asking for music: what counts as asking twice, what the list looks like when there are
/// hundreds of them, and taking one back off.
///
/// The Lidarr behind these is a stub handler that counts calls, because the expensive half of
/// a five-hundred-song import isn't the database — it's telling Lidarr about the same album
/// three times over.
/// </summary>
public sealed class RequestServiceTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly FakeLidarr _lidarr = new();

    private RequestService _service = null!;
    private LidarrOptions _options = null!;
    private AppUser _user = null!;

    public async Task InitializeAsync()
    {
        _options = new LidarrOptions
        {
            BaseUrl = "http://lidarr.invalid",
            ApiKey = "key",
            RootFolderPath = @"C:\music",
        };

        var client = new LidarrClient(
            new HttpClient(_lidarr),
            new StaticOptionsMonitor<LidarrOptions>(_options),
            NullLogger<LidarrClient>.Instance);

        var settings = new SettingsService(
            _db,
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions()),
            new StaticOptionsMonitor<RequestOptions>(new RequestOptions { MaxOpenPerUser = 100 }));

        _service = new RequestService(_db, client, settings, NullLogger<RequestService>.Instance);
        _user = await _db.AddUserAsync("Dev");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static LidarrAlbum Album(string mbid = "album-mbid", string title = "Meteora") => new()
    {
        Title = title,
        MusicBrainzId = mbid,
        Artist = new LidarrArtist { ArtistName = "Linkin Park", MusicBrainzId = $"artist-of-{mbid}" },
    };

    private Task<CreateRequestResult> RequestTrack(
        string title, string? recordingMbid = null, Guid? playlist = null, Guid? user = null,
        string albumMbid = "album-mbid") =>
        _service.CreateAsync(
            user ?? _user.Id, Album(albumMbid), RequestKind.Track, title, recordingMbid, playlist);

    // ---- what counts as the same request ---------------------------------

    [Fact]
    public void Two_songs_off_one_album_are_two_requests()
    {
        // The whole reason the key isn't just the album: Lidarr fetches Meteora once, but
        // "Numb" and "Faint" are two things somebody wants in their playlist.
        var numb = RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", null, null, _user.Id);
        var faint = RequestService.DuplicateKey(RequestKind.Track, "album", "Faint", null, null, _user.Id);

        Assert.NotEqual(numb, faint);
    }

    [Fact]
    public void A_recording_id_beats_the_title()
    {
        // Spotify's "Numb (2011 Remaster)" and MusicBrainz's "Numb" are the same recording.
        var a = RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", "rec-1", null, _user.Id);
        var b = RequestService.DuplicateKey(RequestKind.Track, "album", "Numb (2011 Remaster)", "rec-1", null, _user.Id);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Titles_match_regardless_of_case_and_padding()
    {
        Assert.Equal(
            RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", null, null, _user.Id),
            RequestService.DuplicateKey(RequestKind.Track, "album", "  NUMB ", null, null, _user.Id));
    }

    [Fact]
    public void The_same_song_for_two_playlists_is_two_requests()
    {
        // Each playlist needs its own append, so each needs its own request.
        var a = RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", "rec-1", Guid.NewGuid(), _user.Id);
        var b = RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", "rec-1", Guid.NewGuid(), _user.Id);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void For_one_playlist_it_does_not_matter_who_asked()
    {
        // Two people asking for the same song for the same shared playlist is one request:
        // granting the second would put the song in there twice.
        var playlist = Guid.NewGuid();

        Assert.Equal(
            RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", "rec-1", playlist, Guid.NewGuid()),
            RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", "rec-1", playlist, Guid.NewGuid()));
    }

    [Fact]
    public void With_no_playlist_it_is_only_a_duplicate_of_your_own()
    {
        // Nothing shared to collide with, so blocking somebody because a stranger asked first
        // would just be refusing them their own library.
        Assert.NotEqual(
            RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", "rec-1", null, Guid.NewGuid()),
            RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", "rec-1", null, Guid.NewGuid()));
    }

    [Fact]
    public void Asking_for_the_album_is_not_the_same_as_asking_for_a_song_on_it()
    {
        Assert.NotEqual(
            RequestService.DuplicateKey(RequestKind.Album, "album", null, null, null, _user.Id),
            RequestService.DuplicateKey(RequestKind.Track, "album", "Numb", null, null, _user.Id));
    }

    // ---- refusing duplicates ---------------------------------------------

    [Fact]
    public async Task The_same_song_twice_is_refused()
    {
        Assert.True((await RequestTrack("Numb", "rec-1")).Ok);

        var again = await RequestTrack("Numb", "rec-1");

        Assert.False(again.Ok);
        Assert.Equal("That one's already been requested.", again.Error);
    }

    [Fact]
    public async Task A_different_song_off_the_same_album_goes_through()
    {
        Assert.True((await RequestTrack("Numb", "rec-1")).Ok);
        Assert.True((await RequestTrack("Faint", "rec-2")).Ok);
    }

    [Fact]
    public async Task Lidarr_is_told_about_an_album_once_however_many_songs_are_wanted()
    {
        // The import path's real cost. Every CreateAsync used to re-add the artist — a full
        // artist list off Lidarr — and fire another AlbumSearch, so three songs off one album
        // was three searches queued behind each other for one download.
        await RequestTrack("Numb", "rec-1");
        await RequestTrack("Faint", "rec-2");
        await RequestTrack("Somewhere I Belong", "rec-3");

        Assert.Equal(1, _lidarr.ArtistListCalls);
        Assert.Equal(1, _lidarr.SearchCommands);
    }

    [Fact]
    public async Task A_song_that_rode_in_on_another_add_is_left_for_the_reconciler_to_search()
    {
        await RequestTrack("Numb", "rec-1");
        await RequestTrack("Faint", "rec-2");

        await using var db = _db.CreateDbContext();
        var faint = db.Requests.Single(r => r.TrackTitle == "Faint");

        // Null, not "now" — nobody searched on its behalf, so the next pass should.
        Assert.Null(faint.LastSearchAt);
        Assert.Equal(0, faint.SearchAttempts);
    }

    [Fact]
    public async Task The_song_that_did_the_adding_records_its_search()
    {
        await RequestTrack("Numb", "rec-1");

        await using var db = _db.CreateDbContext();
        var numb = db.Requests.Single();

        Assert.NotNull(numb.LastSearchAt);
        Assert.Equal(1, numb.SearchAttempts);
    }

    [Fact]
    public async Task A_request_that_gave_up_does_not_block_asking_again()
    {
        Assert.True((await RequestTrack("Numb", "rec-1")).Ok);

        await SetStatusAsync(RequestStatus.NotFound);

        Assert.True((await RequestTrack("Numb", "rec-1")).Ok);
    }

    [Fact]
    public async Task A_song_already_in_the_library_still_blocks_a_second_ask()
    {
        // Available is not "finished with" — the song is there, and asking again would fetch
        // the album a second time to add a track the playlist already has.
        Assert.True((await RequestTrack("Numb", "rec-1")).Ok);

        await SetStatusAsync(RequestStatus.Available);

        Assert.False((await RequestTrack("Numb", "rec-1")).Ok);
    }

    [Fact]
    public async Task The_quota_still_bites()
    {
        var settings = new SettingsService(
            _db,
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions()),
            new StaticOptionsMonitor<RequestOptions>(new RequestOptions { MaxOpenPerUser = 2 }));

        var service = new RequestService(
            _db,
            new LidarrClient(new HttpClient(_lidarr),
                new StaticOptionsMonitor<LidarrOptions>(_options), NullLogger<LidarrClient>.Instance),
            settings,
            NullLogger<RequestService>.Instance);

        Assert.True((await service.CreateAsync(_user.Id, Album(), RequestKind.Track, "A", "rec-a", null)).Ok);
        Assert.True((await service.CreateAsync(_user.Id, Album(), RequestKind.Track, "B", "rec-b", null)).Ok);

        var third = await service.CreateAsync(_user.Id, Album(), RequestKind.Track, "C", "rec-c", null);

        Assert.False(third.Ok);
        Assert.Contains("limit 2", third.Error);
    }

    [Fact]
    public async Task An_import_is_not_held_to_the_quota()
    {
        var settings = new SettingsService(
            _db,
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions()),
            new StaticOptionsMonitor<RequestOptions>(new RequestOptions { MaxOpenPerUser = 1 }));

        var service = new RequestService(
            _db,
            new LidarrClient(new HttpClient(_lidarr),
                new StaticOptionsMonitor<LidarrOptions>(_options), NullLogger<LidarrClient>.Instance),
            settings,
            NullLogger<RequestService>.Instance);

        Assert.True((await service.CreateAsync(
            _user.Id, Album(), RequestKind.Track, "A", "rec-a", null, enforceQuota: false)).Ok);
        Assert.True((await service.CreateAsync(
            _user.Id, Album(), RequestKind.Track, "B", "rec-b", null, enforceQuota: false)).Ok);
    }

    // ---- the search page's view of it ------------------------------------

    [Fact]
    public async Task What_you_asked_for_comes_back_as_a_key_the_search_page_can_match()
    {
        await RequestTrack("Numb", "rec-1");

        var keys = await _service.GetActiveKeysAsync(_user.Id);

        Assert.Contains(
            RequestService.DuplicateKey(RequestKind.Track, "album-mbid", "Numb", "rec-1", null, _user.Id),
            keys);
    }

    [Fact]
    public async Task Somebody_elses_private_request_is_not_in_your_keys()
    {
        var other = await _db.AddUserAsync("Someone");
        await RequestTrack("Numb", "rec-1", user: other.Id);

        Assert.Empty(await _service.GetActiveKeysAsync(_user.Id));
    }

    // ---- the list --------------------------------------------------------

    [Fact]
    public async Task The_total_is_the_whole_list_not_the_page()
    {
        // The complaint that started this: a hard Take(50) made 504 requests look like 50.
        await SeedAsync(120);

        var page = await _service.GetForUserAsync(_user.Id, RequestFilter.All, skip: 0, take: 25);

        Assert.Equal(25, page.Rows.Count);
        Assert.Equal(120, page.Total);
        Assert.Equal(120, page.Counts.All);
        Assert.Equal(5, page.PageCount);
        Assert.True(page.HasNext);
        Assert.False(page.HasPrevious);
    }

    [Fact]
    public async Task Counts_split_by_what_state_things_are_in()
    {
        await SeedAsync(10, RequestStatus.Searching);
        await SeedAsync(4, RequestStatus.Available, from: 10);
        await SeedAsync(3, RequestStatus.NotFound, from: 14);
        await SeedAsync(2, RequestStatus.Failed, from: 17);

        var page = await _service.GetForUserAsync(_user.Id);

        Assert.Equal(19, page.Counts.All);
        Assert.Equal(10, page.Counts.Open);
        Assert.Equal(4, page.Counts.Ready);
        Assert.Equal(5, page.Counts.Problem);
    }

    [Fact]
    public async Task A_filter_pages_over_only_what_it_matches()
    {
        await SeedAsync(10, RequestStatus.Searching);
        await SeedAsync(4, RequestStatus.Available, from: 10);

        var page = await _service.GetForUserAsync(_user.Id, RequestFilter.Ready, skip: 0, take: 25);

        Assert.Equal(4, page.Total);
        Assert.All(page.Rows, r => Assert.Equal(RequestStatus.Available, r.Status));
    }

    [Fact]
    public async Task Asking_past_the_end_lands_on_the_last_page_not_an_empty_one()
    {
        // How you get here: you were on page nine and cancelled your way down to three pages.
        await SeedAsync(30);

        var page = await _service.GetForUserAsync(_user.Id, RequestFilter.All, skip: 500, take: 10);

        Assert.Equal(20, page.Skip);
        Assert.Equal(10, page.Rows.Count);
        Assert.Equal(3, page.PageNumber);
        Assert.False(page.HasNext);
    }

    [Fact]
    public async Task A_take_nobody_should_get_is_clamped()
    {
        await SeedAsync(5);

        var page = await _service.GetForUserAsync(_user.Id, RequestFilter.All, skip: 0, take: 100_000);

        Assert.Equal(RequestService.MaxPageSize, page.Take);
    }

    [Fact]
    public async Task You_only_see_your_own()
    {
        var other = await _db.AddUserAsync("Someone");
        await SeedAsync(3);
        await SeedAsync(7, from: 3, owner: other.Id);

        Assert.Equal(3, (await _service.GetForUserAsync(_user.Id)).Total);
        Assert.Equal(10, (await _service.GetAllAsync()).Total);
    }

    // ---- taking one back off ---------------------------------------------

    [Fact]
    public async Task You_can_take_your_own_request_off_the_list()
    {
        var created = await RequestTrack("Numb", "rec-1");

        var (ok, error) = await _service.CancelAsync(_user.Id, created.RequestId!.Value);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(0, (await _service.GetForUserAsync(_user.Id)).Total);
    }

    [Fact]
    public async Task You_cannot_take_off_somebody_elses()
    {
        var other = await _db.AddUserAsync("Someone");
        var created = await RequestTrack("Numb", "rec-1", user: other.Id);

        var (ok, _) = await _service.CancelAsync(_user.Id, created.RequestId!.Value);

        Assert.False(ok);
        Assert.Equal(1, (await _service.GetAllAsync()).Total);
    }

    [Fact]
    public async Task An_admin_can()
    {
        var other = await _db.AddUserAsync("Someone");
        var created = await RequestTrack("Numb", "rec-1", user: other.Id);

        Assert.True((await _service.CancelAsync(_user.Id, created.RequestId!.Value, asAdmin: true)).Ok);
    }

    [Fact]
    public async Task Cancelling_the_last_song_wanted_off_an_album_stops_Lidarr_chasing_it()
    {
        var created = await RequestTrack("Numb", "rec-1");

        await _service.CancelAsync(_user.Id, created.RequestId!.Value);

        Assert.Equal(1, _lidarr.Unmonitors);
    }

    [Fact]
    public async Task Cancelling_one_of_two_songs_off_an_album_leaves_it_monitored()
    {
        // Otherwise dropping one song abandons the download the other one is still waiting for.
        var numb = await RequestTrack("Numb", "rec-1");
        await RequestTrack("Faint", "rec-2");

        await _service.CancelAsync(_user.Id, numb.RequestId!.Value);

        Assert.Equal(0, _lidarr.Unmonitors);
    }

    [Fact]
    public async Task Removing_a_finished_request_does_not_touch_Lidarr()
    {
        var created = await RequestTrack("Numb", "rec-1");
        await SetStatusAsync(RequestStatus.Available);

        await _service.CancelAsync(_user.Id, created.RequestId!.Value);

        Assert.Equal(0, _lidarr.Unmonitors);
    }

    [Fact]
    public async Task Cancelling_something_already_gone_says_so_rather_than_throwing()
    {
        var (ok, error) = await _service.CancelAsync(_user.Id, Guid.NewGuid());

        Assert.False(ok);
        Assert.Equal("That request is already gone.", error);
    }

    [Fact]
    public async Task Clearing_finished_leaves_everything_still_in_flight()
    {
        await SeedAsync(4, RequestStatus.Searching);
        await SeedAsync(3, RequestStatus.Available, from: 4);
        await SeedAsync(2, RequestStatus.NotFound, from: 7);

        Assert.Equal(5, await _service.ClearFinishedAsync(_user.Id));

        var page = await _service.GetForUserAsync(_user.Id);
        Assert.Equal(4, page.Total);
        Assert.All(page.Rows, r => Assert.True(r.IsOpen));
    }

    [Fact]
    public async Task Clearing_finished_leaves_other_people_alone()
    {
        var other = await _db.AddUserAsync("Someone");
        await SeedAsync(3, RequestStatus.Available);
        await SeedAsync(3, RequestStatus.Available, from: 3, owner: other.Id);

        await _service.ClearFinishedAsync(_user.Id);

        Assert.Equal(3, (await _service.GetAllAsync()).Total);
    }

    // ---- helpers ---------------------------------------------------------

    private async Task SetStatusAsync(RequestStatus status)
    {
        await using var db = _db.CreateDbContext();
        foreach (var request in db.Requests) request.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>Rows straight into the table — the list tests are about counting, not about Lidarr.</summary>
    private async Task SeedAsync(
        int count, RequestStatus status = RequestStatus.Searching, int from = 0, Guid? owner = null)
    {
        await using var db = _db.CreateDbContext();

        for (var i = from; i < from + count; i++)
        {
            db.Requests.Add(new Request
            {
                Id = Guid.NewGuid(),
                RequesterId = owner ?? _user.Id,
                Kind = RequestKind.Track,
                Status = status,
                Query = $"Song {i}",
                ArtistName = "Linkin Park",
                AlbumTitle = "Meteora",
                TrackTitle = $"Song {i}",
                AlbumMusicBrainzId = $"album-{i}",
                LidarrAlbumId = 1000 + i,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            });
        }

        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Just enough Lidarr to add an album, and a count of how often it was asked. The counts are
/// the assertion in the import tests above — the bug wasn't a wrong answer, it was asking three
/// hundred times for the same one.
/// </summary>
internal sealed class FakeLidarr : HttpMessageHandler
{
    public int ArtistListCalls { get; private set; }
    public int SearchCommands { get; private set; }
    public int Unmonitors { get; private set; }

    private int _nextAlbumId = 100;
    private readonly Dictionary<string, int> _albums = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;

        var body = (request.Method.Method, path) switch
        {
            ("GET", "/api/v1/artist") => Artists(),
            ("POST", "/api/v1/artist") => """{"id":1,"artistName":"Linkin Park","foreignArtistId":"artist-of-album-mbid"}""",
            ("GET", "/api/v1/album") => AlbumsFor(query),
            ("PUT", "/api/v1/album/monitor") => Monitor(request),
            ("POST", "/api/v1/command") => Command(request),
            _ => "{}",
        };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    private string Artists()
    {
        ArtistListCalls++;
        return """[{"id":1,"artistName":"Linkin Park","foreignArtistId":"artist-of-album-mbid"}]""";
    }

    /// <summary>Every album MBID asked about exists, with a stable id per MBID.</summary>
    private string AlbumsFor(string _)
    {
        // The client matches on foreignAlbumId, so hand back every album this fake has ever
        // been asked to mint plus the default one a test uses without saying.
        Id("album-mbid");

        var rows = _albums.Select(kv =>
            $$"""{"id":{{kv.Value}},"title":"Meteora","foreignAlbumId":"{{kv.Key}}"}""");

        return $"[{string.Join(",", rows)}]";
    }

    private int Id(string mbid)
    {
        if (_albums.TryGetValue(mbid, out var id)) return id;

        id = _nextAlbumId++;
        _albums[mbid] = id;
        return id;
    }

    private string Monitor(HttpRequestMessage request)
    {
        var json = request.Content!.ReadAsStringAsync().Result;
        if (json.Contains("\"monitored\":false", StringComparison.OrdinalIgnoreCase)) Unmonitors++;

        return "{}";
    }

    private string Command(HttpRequestMessage request)
    {
        var json = request.Content!.ReadAsStringAsync().Result;
        if (json.Contains("AlbumSearch", StringComparison.Ordinal)) SearchCommands++;

        return """{"id":1,"name":"AlbumSearch"}""";
    }
}
