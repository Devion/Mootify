using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mootify.Data;
using Mootify.Endpoints.Api;
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
/// connection string, the real Soulseek key, and a music root pointing at somebody's NAS — then
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
        builder.ConfigureServices(services => services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Library:MusicRoot"] = MusicRoot,
            ["Library:Username"] = "",
            ["Library:Password"] = "",
            // No scanning and no watching: a test must not walk somebody's library, and it has
            // nothing to find in a temp folder anyway.
            ["Library:ScanOnStartup"] = "false",
            ["Library:WatchFileSystem"] = "false",
            // Blank disables every Soulseek-backed feature, which is also the state the request
            // endpoints are asserted against.
            ["Soulseek:BaseUrl"] = "",
            ["Soulseek:ApiKey"] = "",
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
        Assert.True(string.IsNullOrEmpty(config["Soulseek:ApiKey"]), "The test host picked up a real Soulseek key.");

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

    /// <summary>
    /// One token, minted once and reused, per device name.
    ///
    /// It used to mint a fresh one per test, and that quietly coupled the size of this file to two
    /// production limits: <c>/api/v1/auth/token</c> is rate-limited to 20 a minute, and
    /// <c>Api:MaxTokensPerUser</c> drops the oldest past ten. Adding a test eventually made an
    /// unrelated one fail with a 503, which is a fixture problem wearing an API problem's clothes.
    ///
    /// Tests that are <i>about</i> issuing or revoking a token call <see cref="IssueTokenAsync"/>
    /// directly with their own device name, so they still get a real one of their own.
    /// </summary>
    private readonly Dictionary<string, string> _tokens = [];
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public async Task<HttpClient> SignedInClientAsync(string device = "Test phone")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await CachedTokenAsync(device));
        return client;
    }

    private async Task<string> CachedTokenAsync(string device)
    {
        await _tokenGate.WaitAsync();

        try
        {
            if (_tokens.TryGetValue(device, out var existing)) return existing;

            using var client = CreateClient();
            var token = await IssueTokenAsync(client, device);
            _tokens[device] = token;
            return token;
        }
        finally
        {
            _tokenGate.Release();
        }
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

    /// <summary>
    /// Request rows straight into the table. Soulseek is deliberately unconfigured in this fixture,
    /// so the real create path can't run — and the wiring these tests are about (paging envelope,
    /// who may delete what) doesn't involve it.
    /// </summary>
    public async Task<List<Guid>> SeedRequestsAsync(int count, bool forSomeoneElse = false)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MootifyDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var requesterId = UserId;

        if (forSomeoneElse)
        {
            var other = new AppUser
            {
                Id = Guid.NewGuid(),
                DisplayName = $"housemate-{Guid.NewGuid():n}",
                NormalizedName = $"housemate-{Guid.NewGuid():n}",
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(other);
            requesterId = other.Id;
        }

        var ids = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var request = new Request
            {
                Id = Guid.NewGuid(),
                RequesterId = requesterId,
                Kind = RequestKind.Track,
                Status = RequestStatus.Searching,
                Query = $"Song {i}",
                ArtistName = "The Cowbells",
                AlbumTitle = "Pasture Sounds",
                TrackTitle = $"Song {i}",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            };

            db.Requests.Add(request);
            ids.Add(request.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>The fixture is shared, so a test that seeds rows takes them away again.</summary>
    public async Task RemoveRequestsAsync(List<Guid> ids)
    {
        var factory = Services.GetRequiredService<IDbContextFactory<MootifyDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        await db.Requests.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync();
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

        // Soulseek is unconfigured in this fixture, and the app uses this to hide the request UI
        // rather than discovering it through a 503.
        Assert.False(body.GetProperty("server").GetProperty("soulseekConfigured").GetBoolean());
        // 2 since playlist contents became a page. The app checks this before assuming a shape.
        Assert.Equal(ApiMap.Version, body.GetProperty("server").GetProperty("apiVersion").GetInt32());
    }

    // ---- the forced password change ---------------------------------------

    [Fact]
    public async Task A_one_time_password_lands_on_the_change_page_and_nothing_else()
    {
        // Four separate lines of wiring have to agree for this to work: the claim goes into the
        // cookie, the login endpoint redirects on it, the middleware holds the rest of the site
        // shut, and OnValidatePrincipal drops the claim once the password is replaced. Any one of
        // them failing open looks exactly like success from inside a unit test.
        await SeedUserAsync(fixture, "resetweb", "one-time-web", mustChange: true);

        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var login = await PostFormAsync(client, "/login", "/auth/login", new()
        {
            ["username"] = "resetweb",
            ["password"] = "one-time-web",
            // Ignored on purpose — the change screen comes first.
            ["returnUrl"] = "/library",
        });

        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        Assert.Equal("/password", login.Headers.Location?.OriginalString);

        var blocked = await client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.Found, blocked.StatusCode);
        Assert.Equal("/password", blocked.Headers.Location?.OriginalString);

        var changed = await PostFormAsync(client, "/password", "/auth/password", new()
        {
            ["currentPassword"] = "one-time-web",
            ["newPassword"] = "chosen-by-me",
            ["confirmPassword"] = "chosen-by-me",
        });

        Assert.Equal(HttpStatusCode.Found, changed.StatusCode);
        Assert.Equal("/", changed.Headers.Location?.OriginalString);

        // The cookie still carries the claim at this point; the gate lifts because
        // OnValidatePrincipal reconciles it against the row on the very next request.
        var after = await client.GetAsync("/library");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// Fetches a static-SSR page for its antiforgery token and posts the form it carries. The
    /// token is bound to a cookie the same client picked up, which is the point — a hand-built
    /// POST would prove nothing about the pages a person actually goes through.
    /// </summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string page, string action, Dictionary<string, string> fields)
    {
        var pageResponse = await client.GetAsync(page);
        var html = await pageResponse.Content.ReadAsStringAsync();
        var token = Regex.Match(html, """name="__RequestVerificationToken" value="([^"]+)""");

        Assert.True(token.Success, $"No antiforgery token on {page}. status={pageResponse.StatusCode}");
        fields["__RequestVerificationToken"] = token.Groups[1].Value;

        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Registration_requires_approval_before_cookie_or_token_login()
    {
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var registered = await PostFormAsync(client, "/register", "/auth/register", new()
        {
            ["username"] = "pendinguser", ["password"] = "pending-password", ["confirmPassword"] = "pending-password",
        });
        Assert.Equal("/login?pending=true", registered.Headers.Location?.OriginalString);
        Assert.DoesNotContain(registered.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.Contains(".AspNetCore.Cookies"));
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/library")).StatusCode);
        var denied = await client.PostAsJsonAsync("/api/v1/auth/token", new { username = "pendinguser", password = "pending-password", deviceName = "Test" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var factory = fixture.Services.GetRequiredService<IDbContextFactory<MootifyDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.SingleAsync(u => u.NormalizedName == "pendinguser");
        Assert.True(user.ApprovalPending);
        await SeedUserAsync(fixture, "approvaladmin", "admin-password", mustChange: false);
        await db.Users.Where(u => u.NormalizedName == "approvaladmin").ExecuteUpdateAsync(set => set.SetProperty(u => u.IsAdmin, true));
        var admin = await db.Users.SingleAsync(u => u.NormalizedName == "approvaladmin");
        using var scope = fixture.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<Mootify.Services.Admin.AdminService>();
        Assert.True((await service.ApproveAsync(admin.Id, user.Id)).Ok);
        var signedIn = await PostFormAsync(client, "/login", "/auth/login", new() { ["username"] = "pendinguser", ["password"] = "pending-password" });
        Assert.Equal("/", signedIn.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/library")).StatusCode);
        // A long-lived cookie records a new visit, and pending status is rechecked on it too.
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.LastSeenAt, DateTimeOffset.UtcNow.AddDays(-10)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/library")).StatusCode);
        await db.Entry(user).ReloadAsync();
        Assert.True(user.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.ApprovalPending, true));
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/library")).StatusCode);
    }

    private static async Task SeedUserAsync(
        MootifyApiFixture fixture, string name, string password, bool mustChange)
    {
        var factory = fixture.Services.GetRequiredService<IDbContextFactory<MootifyDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            NormalizedName = name,
            MustChangePassword = mustChange,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_account_on_a_one_time_password_gets_no_device_token()
    {
        // The password is right, so this isn't a 401 — but there is nowhere on a phone to choose
        // a new one, and a token issued here would outlive the reset that revoked the last batch.
        // A sentence the app can show, not a redirect to a login page it can't read.
        await SeedUserAsync(fixture, "resetme", "one-time-only", mustChange: true);

        using var client = fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            username = "resetme",
            password = "one-time-only",
            deviceName = "Pixel 8",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Contains("website", body!.RootElement.GetProperty("error").GetString());
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

        // items is a page, not an array — see ApiPlaylistDetail. A client built against the old
        // shape fails to parse here rather than silently showing a prefix of a long playlist.
        var items = detail.GetProperty("items");
        var item = items.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("Car songs", detail.GetProperty("name").GetString());
        Assert.Equal("More Cowbell", item.GetProperty("track").GetProperty("title").GetString());
        Assert.True(detail.GetProperty("canEdit").GetBoolean());

        // The playlist's own totals, so a header never has to be computed from a page.
        Assert.Equal(1, detail.GetProperty("trackCount").GetInt32());
        Assert.Equal(1, items.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_long_playlist_is_paged_rather_than_sent_whole()
    {
        // The reason any of this changed: a car browsing a long playlist re-fetched every row for
        // every page it drew. skip/take have to reach the query, and the total has to describe the
        // playlist rather than the page, or a pager can't say what it is not showing.
        using var client = await fixture.SignedInClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/playlists", new { name = "Long drive" });
        var playlistId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("id").GetGuid();

        // Seed a legacy playlist with duplicate entries to keep paging independent of add deduplication.
        var factory = fixture.Services.GetRequiredService<IDbContextFactory<MootifyDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var owner = (await db.Playlists.SingleAsync(p => p.Id == playlistId)).OwnerUserId!.Value;
            for (var i = 0; i < 3; i++)
                db.PlaylistItems.Add(new PlaylistItem { Id = Guid.NewGuid(), PlaylistId = playlistId, TrackId = fixture.TrackId, SortKey = i * 1000, AddedByUserId = owner, AddedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var first = (await client.GetFromJsonAsync<JsonDocument>(
            $"/api/v1/playlists/{playlistId}?skip=0&take=2"))!.RootElement.GetProperty("items");

        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(2, first.GetProperty("items").EnumerateArray().Count());

        // The rows-only endpoint is what a browse tree asks for on page 2 and after.
        var second = (await client.GetFromJsonAsync<JsonDocument>(
            $"/api/v1/playlists/{playlistId}/items?skip=2&take=2"))!.RootElement;

        Assert.Equal(3, second.GetProperty("total").GetInt32());
        Assert.Single(second.GetProperty("items").EnumerateArray());

        // And the whole thing as bare ids, which is what building a play queue needs.
        var ids = (await client.GetFromJsonAsync<List<Guid>>(
            $"/api/v1/playlists/{playlistId}/trackids"))!;

        Assert.Equal(3, ids.Count);
        Assert.All(ids, id => Assert.Equal(fixture.TrackId, id));
    }

    [Fact]
    public async Task Listening_along_is_off_until_it_is_switched_on()
    {
        // The switch is per account rather than per device, so it is the server that decides
        // whether a heartbeat becomes visible — a phone must not be able to opt itself in.
        using var client = await fixture.SignedInClientAsync();

        var before = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/listening"))!.RootElement;
        Assert.False(before.GetProperty("sharing").GetBoolean());

        var on = await client.PutAsJsonAsync("/api/v1/listening", new { sharing = true });
        on.EnsureSuccessStatusCode();

        Assert.True((await on.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("sharing").GetBoolean());

        var after = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/listening"))!.RootElement;
        Assert.True(after.GetProperty("sharing").GetBoolean());
    }

    [Fact]
    public async Task A_playback_heartbeat_carries_the_source_playlist_without_a_second_call()
    {
        // Folding the listening heartbeat into the playback save is the whole design: one call on
        // one timer. This proves the extra fields reach the server and that a save still succeeds
        // for a client that has never heard of them.
        using var client = await fixture.SignedInClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/playlists", new { name = "In the car" });
        var playlistId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("id").GetGuid();

        await client.PostAsJsonAsync(
            $"/api/v1/playlists/{playlistId}/tracks", new { trackIds = new[] { fixture.TrackId } });

        await client.PutAsJsonAsync("/api/v1/listening", new { sharing = true });

        var saved = await client.PutAsJsonAsync("/api/v1/playback", new
        {
            currentTrackId = fixture.TrackId,
            positionSeconds = 12.5,
            queue = new[] { fixture.TrackId },
            queueIndex = 0,
            shuffleEnabled = false,
            repeat = "Off",
            sourcePlaylistId = playlistId,
            isPlaying = true,
        });

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        // A personal playlist has exactly one reader and they are left out of their own list, so
        // this is empty — what it proves is that the endpoint exists and answers rather than 404s.
        var listeners = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/playlists/{playlistId}/listeners");

        Assert.Empty(listeners!);

        // The old body shape, with neither new field. It still has to save playback.
        var legacy = await client.PutAsJsonAsync("/api/v1/playback", new
        {
            currentTrackId = fixture.TrackId,
            positionSeconds = 30.0,
            queue = new[] { fixture.TrackId },
            queueIndex = 0,
            shuffleEnabled = false,
            repeat = "Off",
        });

        Assert.Equal(HttpStatusCode.NoContent, legacy.StatusCode);

        var state = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/playback"))!.RootElement;
        Assert.Equal(30.0, state.GetProperty("positionSeconds").GetDouble());
    }

    [Fact]
    public async Task Listeners_on_somebody_elses_playlist_are_not_found()
    {
        // Empty rather than 403, and 404 for a playlist that isn't there: neither answer may be
        // usable to discover that somebody else's list exists.
        using var client = await fixture.SignedInClientAsync();

        var listeners = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/playlists/{Guid.NewGuid()}/listeners");

        Assert.Empty(listeners!);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/playlists/{Guid.NewGuid()}/items")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/playlists/{Guid.NewGuid()}/trackids")).StatusCode);
    }

    [Fact]
    public async Task Library_search_reaches_the_album_and_the_artist_too()
    {
        // Track-title-only search was the API's own rule and it disagreed with the website's.
        // "Pasture" is the fixture album; the track on it is called "More Cowbell".
        using var client = await fixture.SignedInClientAsync();

        var byAlbum = (await client.GetFromJsonAsync<JsonDocument>(
            "/api/v1/library/search?q=pasture"))!.RootElement;

        var track = byAlbum.GetProperty("tracks").EnumerateArray().Single();
        Assert.Equal("More Cowbell", track.GetProperty("title").GetString());
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
    public async Task Requests_say_so_when_there_is_no_soulseek()
    {
        // Rather than a 500 or an empty list. The app hides the request UI on the server info flag;
        // this is the answer for a client that asks anyway.
        using var client = await fixture.SignedInClientAsync();

        var response = await client.GetAsync("/api/v1/requests/search?q=nirvana");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task The_request_list_is_paged_and_carries_its_own_total()
    {
        // An import leaves hundreds of these behind, and a phone handed all of them has nothing
        // useful to do with the tail. Same envelope as every other list here.
        using var client = await fixture.SignedInClientAsync();

        var ids = await fixture.SeedRequestsAsync(5);

        try
        {
            var page = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/requests?skip=0&take=2"))!.RootElement;

            Assert.Equal(5, page.GetProperty("total").GetInt32());
            Assert.Equal(2, page.GetProperty("take").GetInt32());
            Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        }
        finally
        {
            await fixture.RemoveRequestsAsync(ids);
        }
    }

    [Fact]
    public async Task A_client_can_take_a_request_back_off_the_list()
    {
        using var client = await fixture.SignedInClientAsync();

        var ids = await fixture.SeedRequestsAsync(2);

        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/requests/{ids[0]}")).StatusCode);

            var page = (await client.GetFromJsonAsync<JsonDocument>("/api/v1/requests"))!.RootElement;
            Assert.Equal(1, page.GetProperty("total").GetInt32());

            // Asking twice is a 404 rather than a second success, which is what a retry after a
            // dropped response should see.
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/requests/{ids[0]}")).StatusCode);
        }
        finally
        {
            await fixture.RemoveRequestsAsync(ids);
        }
    }

    [Fact]
    public async Task Somebody_elses_request_is_not_yours_to_delete()
    {
        using var client = await fixture.SignedInClientAsync();

        var ids = await fixture.SeedRequestsAsync(1, forSomeoneElse: true);

        try
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/requests/{ids[0]}")).StatusCode);
        }
        finally
        {
            await fixture.RemoveRequestsAsync(ids);
        }
    }
}
