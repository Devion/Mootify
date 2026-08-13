using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mootify.Data;
using Mootify.Services.Settings;

namespace Mootify.Tests;

/// <summary>
/// A boot of the real app, walled off from the developer's install, driven over HTTP the way the
/// Android app will drive it.
///
/// The unit tests can prove a token validates and a query translates. They cannot prove the part
/// that is only true once everything is wired together: that <c>/api</c> refuses a request with no
/// token, that a bearer token gets through both the API and the audio stream, that turning
/// antiforgery off didn't also turn authentication off, and that a revoked device stops working at
/// the door rather than somewhere deeper. Every one of those is a single line in Program.cs that
/// would fail silently in the direction of "let it through".
///
/// The isolation is load-bearing and was learned the hard way: <c>mootify.json</c> is copied into
/// the test output, so a test host that doesn't override it will happily pick up the real
/// connection string, the real Lidarr key, and a music root pointing at somebody's NAS — then
/// start a scan of it. <c>UseSetting</c> is <b>not</b> enough, because it lands in host
/// configuration, which <c>Program.cs</c> layers <c>mootify.json</c> on top of. An in-memory
/// source added through <see cref="IWebHostBuilder.ConfigureAppConfiguration"/> is applied last
/// and wins, and <see cref="AssertIsolated"/> refuses to run if that ever stops being true.
/// </summary>
public abstract class IsolatedMootifyFixture : WebApplicationFactory<Program>
{
    protected readonly string Root = Path.Combine(
        Path.GetTempPath(), "mootify-api-tests", Guid.NewGuid().ToString("n"));

    public string MusicRoot => Path.Combine(Root, "music");

    protected string DatabasePath => Path.Combine(Root, "test.db");

    protected IsolatedMootifyFixture()
    {
        Directory.CreateDirectory(MusicRoot);

        // The connection string has to be set *here*, as an environment variable, and not with the
        // in-memory source below. Program.cs reads it eagerly while composing the container:
        //
        //     var connectionString = builder.Configuration.GetConnectionString("Default") ?? …
        //
        // Anything the test host layers on afterwards changes IConfiguration but not the value the
        // DbContext was registered with — which is how these tests were silently running against a
        // database under the test output folder while every override looked correct. Program.cs
        // re-adds the environment provider on top of mootify.json, so this beats the real file.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={DatabasePath}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Library:MusicRoot"] = MusicRoot,
            ["Library:Username"] = "",
            ["Library:Password"] = "",
            // No scanning and no watching: a test must not walk somebody's library, and it has
            // nothing to find in a temp folder anyway.
            ["Library:ScanOnStartup"] = "false",
            ["Library:WatchFileSystem"] = "false",
            // Blank disables every Lidarr-backed feature, which is also the state the request
            // endpoints are asserted against.
            ["Lidarr:BaseUrl"] = "",
            ["Lidarr:ApiKey"] = "",
            ["Transcode:CacheDirectory"] = Path.Combine(Root, "transcode"),
            ["Transcode:DeleteSourceAfterTranscode"] = "false",
            ["Api:ArtCacheDirectory"] = Path.Combine(Root, "art"),
        }));
    }

    /// <summary>
    /// Proves the overrides won before any test does anything. If configuration precedence ever
    /// changes, this fails with a readable message instead of the suite quietly running against a
    /// real install.
    /// </summary>
    protected void AssertIsolated()
    {
        var config = Services.GetRequiredService<IConfiguration>();

        Assert.Equal(MusicRoot, config["Library:MusicRoot"]);
        Assert.True(string.IsNullOrEmpty(config["Lidarr:ApiKey"]), "The test host picked up a real Lidarr key.");

        // The database the container actually holds, not the one configuration claims. Asking
        // IConfiguration is the check that already failed to notice a shared database once.
        using var db = Services.GetRequiredService<IDbContextFactory<MootifyDbContext>>().CreateDbContext();
        Assert.Contains(Root, db.Database.GetConnectionString()!);
    }

    protected async Task CleanUpAsync()
    {
        await base.DisposeAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A locked SQLite file on Windows is not a test failure.
        }
    }
}

public sealed class MootifyApiFixture : IsolatedMootifyFixture, IAsyncLifetime
{
    /// <summary>Known password for the seeded account, so the token endpoint can be exercised for real.</summary>
    public const string Password = "correct-horse-battery";

    public Guid UserId { get; private set; }
    public Guid AlbumId { get; private set; }
    public Guid TrackId { get; private set; }
    public string TrackFilePath { get; private set; } = "";

    public async Task InitializeAsync()
    {
        // Forces the host up, then seeds. Startup decided there were no accounts and put the
        // instance in first-run mode, so tell it otherwise — SetupIncompleteApiTests covers what a
        // client sees before that point.
        using var _ = CreateClient();
        AssertIsolated();

        var factory = Services.GetRequiredService<IDbContextFactory<MootifyDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        // A fixture that finds anything here is a fixture sharing a database with something else,
        // which is the failure this whole class of test is most likely to hide.
        Assert.Empty(await db.Artists.ToListAsync());

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            DisplayName = "devion",
            NormalizedName = "devion",
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, Password);
        db.Users.Add(user);
        UserId = user.Id;

        // A real file on disk with a real cover next to it: the art endpoint's adjacent-file path
        // and the streaming endpoint both read the filesystem.
        var folder = Path.Combine(MusicRoot, "The Cowbells", "Pasture Sounds");
        Directory.CreateDirectory(folder);

        TrackFilePath = Path.Combine(folder, "01 More Cowbell.mp3");
        await File.WriteAllBytesAsync(TrackFilePath, [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);

        var artist = new Artist { Id = Guid.NewGuid(), Name = "The Cowbells", SortName = "Cowbells, The" };
        var album = new Album { Id = Guid.NewGuid(), Title = "Pasture Sounds", ArtistId = artist.Id, Year = 2001 };
        var track = new Track
        {
            Id = Guid.NewGuid(),
            Path = TrackFilePath,
            Title = "More Cowbell",
            ArtistId = artist.Id,
            AlbumId = album.Id,
            TrackNumber = 1,
            Duration = TimeSpan.FromMinutes(3),
            Bitrate = 320,
            AddedAt = DateTimeOffset.UtcNow,
            FileModifiedAt = DateTimeOffset.UtcNow,
            FileSize = 8,
            IsPresent = true,
        };

        db.Artists.Add(artist);
        db.Albums.Add(album);
        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        AlbumId = album.Id;
        TrackId = track.Id;

        Services.GetRequiredService<SetupState>().MarkComplete();
    }

    /// <summary>A client carrying a freshly issued device token, as the app would.</summary>
    public async Task<HttpClient> SignedInClientAsync(string device = "Test phone")
    {
        var client = CreateClient();
        var token = await IssueTokenAsync(client, device);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<string> IssueTokenAsync(HttpClient client, string device = "Test phone")
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            username = "devion",
            password = Password,
            deviceName = device,
        });

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("token").GetString()!;
    }

    public new Task DisposeAsync() => CleanUpAsync();
}

public sealed class ApiIntegrationTests(MootifyApiFixture fixture) : IClassFixture<MootifyApiFixture>
{
    // ---- the door ---------------------------------------------------------

    [Fact]
    public async Task Without_a_token_the_api_says_401_and_not_a_login_page()
    {
        // A phone handed a 302 to /login reads a 200 full of HTML and has nothing to tell the
        // user. The API scheme exists to answer this case honestly.
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/v1/library/artists");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_made_up_token_is_not_a_token()
    {
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "moo_nope");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task The_wrong_password_gets_an_error_worth_showing()
    {
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            username = "devion",
            password = "not it",
            deviceName = "Test phone",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.False(string.IsNullOrWhiteSpace(body!.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Signing_in_returns_the_user_and_what_the_server_can_do()
    {
        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            username = "devion",
            password = MootifyApiFixture.Password,
            deviceName = "Pixel 8",
        });

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;

        Assert.StartsWith("moo_", body.GetProperty("token").GetString());
        Assert.Equal("devion", body.GetProperty("user").GetProperty("displayName").GetString());

        // Lidarr is unconfigured in this fixture, and the app uses this to hide the request UI
        // rather than discovering it through a 503.
        Assert.False(body.GetProperty("server").GetProperty("lidarrConfigured").GetBoolean());
        Assert.Equal(1, body.GetProperty("server").GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task Revoking_a_device_locks_it_out_immediately()
    {
        using var client = fixture.CreateClient();
        var token = await fixture.IssueTokenAsync(client, "Doomed phone");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var listed = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/auth/tokens"))!.RootElement;
        var id = listed.EnumerateArray()
            .First(t => t.GetProperty("deviceName").GetString() == "Doomed phone")
            .GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/auth/tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    // ---- library ----------------------------------------------------------

    [Fact]
    public async Task A_signed_in_device_can_read_the_library()
    {
        using var client = await fixture.SignedInClientAsync();

        var albums = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/library/albums"))!.RootElement;
        var album = albums.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("Pasture Sounds", album.GetProperty("title").GetString());
        Assert.Equal("The Cowbells", album.GetProperty("artistName").GetString());
        Assert.Equal($"/art/album/{fixture.AlbumId}", album.GetProperty("artUrl").GetString());
    }

    [Fact]
    public async Task A_track_never_carries_its_path_on_disk()
    {
        // Track.Path is an absolute path on the server's share. Serializing the entity instead of
        // a projection would hand the phone a map of the NAS.
        using var client = await fixture.SignedInClientAsync();

        var raw = await client.GetStringAsync($"/api/v1/library/albums/{fixture.AlbumId}");

        Assert.Contains("More Cowbell", raw);
        Assert.DoesNotContain("Pasture Sounds\\\\", raw);
        Assert.DoesNotContain(".mp3", raw);
    }

    [Fact]
    public async Task Search_answers_with_all_three_kinds_at_once()
    {
        using var client = await fixture.SignedInClientAsync();

        var results = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/library/search?q=cowbell"))!.RootElement;

        Assert.Single(results.GetProperty("artists").EnumerateArray());
        Assert.Single(results.GetProperty("albums").EnumerateArray());
        Assert.Single(results.GetProperty("tracks").EnumerateArray());
    }

    // ---- streaming and art ------------------------------------------------

    [Fact]
    public async Task Audio_streams_with_a_bearer_token()
    {
        // The point of the whole exercise: ExoPlayer fetching /media with an Authorization header,
        // through the same endpoint the website's <audio> element uses with a cookie.
        using var client = await fixture.SignedInClientAsync();

        var response = await client.GetAsync($"/media/{fixture.TrackId}");

        response.EnsureSuccessStatusCode();
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);

        // Range processing has to stay on, or seeking silently breaks.
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public async Task Audio_does_not_stream_without_one()
    {
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync($"/media/{fixture.TrackId}");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Album_art_is_reachable_without_a_token()
    {
        // Deliberate, and the reasoning is in ArtEndpoints: the car head unit fetches artwork from
        // its own process, with none of our headers. The album GUID is the capability.
        using var client = fixture.CreateClient();

        var response = await client.GetAsync($"/art/album/{fixture.AlbumId}");

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Art_for_an_album_that_does_not_exist_is_a_404()
    {
        using var client = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/art/album/{Guid.NewGuid()}")).StatusCode);
    }

    // ---- writes -----------------------------------------------------------

    [Fact]
    public async Task A_device_can_build_a_playlist_without_an_antiforgery_token()
    {
        // Antiforgery is off for /api because a token client has no way to mint a token — which is
        // safe only because the API policy refuses cookies. If this test starts failing with a
        // 400, somebody has re-enabled antiforgery; if the API ever accepts a cookie again, this
        // test passing is no longer good news.
        using var client = await fixture.SignedInClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/playlists", new { name = "Car songs" });
        created.EnsureSuccessStatusCode();

        var playlistId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("id").GetGuid();

        var added = await client.PostAsJsonAsync(
            $"/api/v1/playlists/{playlistId}/tracks", new { trackIds = new[] { fixture.TrackId } });
        added.EnsureSuccessStatusCode();

        Assert.Equal(1, (await added.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("added").GetInt32());

        var detail = (await client.GetFromJsonAsync<JsonDocument>($"/api/v1/playlists/{playlistId}"))!.RootElement;
        var item = detail.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("Car songs", detail.GetProperty("name").GetString());
        Assert.Equal("More Cowbell", item.GetProperty("track").GetProperty("title").GetString());
        Assert.True(detail.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task Somebody_elses_playlist_is_not_found_rather_than_forbidden()
    {
        using var client = await fixture.SignedInClientAsync();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/playlists/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Playback_state_round_trips_and_enums_are_names()
    {
        // "repeat": "All" survives a client built against an older enum; "repeat": 1 quietly
        // becomes a different mode the day a member is inserted.
        using var client = await fixture.SignedInClientAsync();

        var saved = await client.PutAsJsonAsync("/api/v1/playback", new
        {
            currentTrackId = fixture.TrackId,
            positionSeconds = 42.5,
            queue = new[] { fixture.TrackId },
            queueIndex = 0,
            shuffleEnabled = true,
            repeat = "All",
        });

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var raw = await client.GetStringAsync("/api/v1/playback");
        Assert.Contains("\"repeat\":\"All\"", raw);

        var state = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/playback"))!.RootElement;
        Assert.Equal(fixture.TrackId, state.GetProperty("currentTrackId").GetGuid());
        Assert.Equal(42.5, state.GetProperty("positionSeconds").GetDouble());
        Assert.True(state.GetProperty("shuffleEnabled").GetBoolean());

        var queue = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/playback/queue"))!.RootElement;
        Assert.Equal("More Cowbell", queue.EnumerateArray().Single().GetProperty("title").GetString());
    }

    [Fact]
    public async Task Requests_say_so_when_there_is_no_lidarr()
    {
        // Rather than a 500 or an empty list. The app hides the request UI on the server info flag;
        // this is the answer for a client that asks anyway.
        using var client = await fixture.SignedInClientAsync();

        var response = await client.GetAsync("/api/v1/requests/search?q=nirvana");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
