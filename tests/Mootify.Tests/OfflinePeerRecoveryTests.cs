using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Requests;
using Mootify.Services.Soulseek;

namespace Mootify.Tests;

public sealed class OfflinePeerRecoveryTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly RecoveryHandler _handler = new();
    private SoulseekClient _soulseek = null!;
    private AppUser _user = null!;

    public async Task InitializeAsync()
    {
        var options = new SoulseekOptions
        {
            BaseUrl = "http://slskd.invalid", ApiKey = "test-key", SearchTimeoutSeconds = 5,
        };
        _soulseek = new SoulseekClient(new HttpClient(_handler),
            new StaticOptionsMonitor<SoulseekOptions>(options), NullLogger<SoulseekClient>.Instance);
        _user = await _db.AddUserAsync("listener");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private RequestReconciler Reconciler() => new(
        _db, _soulseek, null!, null!, null!,
        new StaticOptionsMonitor<SoulseekOptions>(new SoulseekOptions()),
        new StaticOptionsMonitor<LibraryOptions>(new LibraryOptions()),
        NullLogger<RequestReconciler>.Instance);

    private async Task<Request> AddRequestAsync(RequestStatus status = RequestStatus.Downloading)
    {
        await using var db = _db.CreateDbContext();
        var request = new Request
        {
            Id = Guid.NewGuid(), RequesterId = _user.Id, Kind = RequestKind.Track,
            Status = status, Query = "Artist Song", ArtistName = "Artist Song",
            TrackTitle = "01 - Song", SoulseekBatchId = Guid.NewGuid(),
            SoulseekUsername = "offline-peer", SoulseekFilename = "Music\\Artist\\01 - Song.mp3",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    [Fact]
    public async Task Offline_transfer_searches_and_queues_matching_file_from_another_peer()
    {
        var request = await AddRequestAsync();
        await Reconciler().ReconcileAllAsync();

        await using var db = _db.CreateDbContext();
        var saved = await db.Requests.SingleAsync();
        Assert.Equal(RequestStatus.Downloading, saved.Status);
        Assert.Equal("online-peer", saved.SoulseekUsername);
        Assert.Equal("Music\\Artist\\Song.mp3", saved.SoulseekFilename);
        Assert.NotEqual(request.SoulseekBatchId, saved.SoulseekBatchId);
        Assert.Equal(request.Id, _handler.EnqueuedDestinationId);
        Assert.Equal(1, _handler.EnqueueCalls);
        Assert.Null(saved.NextOfflineRecoveryAt);
    }

    [Fact]
    public async Task No_matching_peer_schedules_a_later_search_instead_of_failing()
    {
        _handler.IncludeMatch = false;
        await AddRequestAsync();

        await Reconciler().ReconcileAllAsync();
        await Reconciler().ReconcileAllAsync();

        await using var db = _db.CreateDbContext();
        var saved = await db.Requests.SingleAsync();
        Assert.Equal(RequestStatus.Searching, saved.Status);
        Assert.Equal(1, saved.OfflineRecoveryAttempts);
        Assert.True(saved.NextOfflineRecoveryAt > DateTimeOffset.UtcNow);
        Assert.Equal(1, _handler.SearchCalls);
        Assert.Equal(0, _handler.EnqueueCalls);
    }

    [Fact]
    public async Task Enqueue_offline_error_stays_open_for_recovery()
    {
        _handler.OfflineOnEnqueue = true;
        var settings = new Mootify.Services.Settings.SettingsService(_db,
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions()),
            new StaticOptionsMonitor<RequestOptions>(new RequestOptions()));
        var service = new RequestService(_db, _soulseek, settings, NullLogger<RequestService>.Instance);
        var file = new SoulseekFile(Guid.NewGuid(), "offline-peer", "Music\\Artist\\01 - Song.mp3",
            1234, "mp3", 320, null, null, null, true, 0, 1000);

        var result = await service.CreateAsync(_user.Id, file, "Artist Song", null);

        Assert.True(result.Ok);
        await using var db = _db.CreateDbContext();
        var saved = await db.Requests.SingleAsync();
        Assert.Equal(RequestStatus.Searching, saved.Status);
        Assert.True(saved.NextOfflineRecoveryAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Previously_failed_offline_request_is_reopened_and_searched()
    {
        var request = await AddRequestAsync(RequestStatus.Failed);
        await using (var db = _db.CreateDbContext())
        {
            var row = await db.Requests.SingleAsync();
            row.FailureReason = "User not online";
            row.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await Reconciler().ReconcileAllAsync();

        await using var check = _db.CreateDbContext();
        var saved = await check.Requests.SingleAsync();
        Assert.Equal(RequestStatus.Downloading, saved.Status);
        Assert.Null(saved.CompletedAt);
        Assert.NotEqual(request.SoulseekBatchId, saved.SoulseekBatchId);
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        public bool IncludeMatch { get; set; } = true;
        public bool OfflineOnEnqueue { get; set; }
        public int SearchCalls { get; private set; }
        public int EnqueueCalls { get; private set; }
        public Guid? EnqueuedDestinationId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/v0/searches")
            {
                SearchCalls++;
                return Json(HttpStatusCode.Created, "{}");
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v0/searches/"))
            {
                var files = IncludeMatch
                    ? """[{"filename":"Music\\Other Band\\Song.mp3","size":1000,"extension":"mp3"},{"filename":"Music\\Artist\\Other Song.mp3","size":1000,"extension":"mp3"},{"filename":"Music\\Artist\\Song.mp3","size":1234,"extension":"mp3"}]"""
                    : """[{"filename":"Music\\Artist\\Other Song.mp3","size":1000,"extension":"mp3"}]""";
                return Json(HttpStatusCode.OK,
                    """{"isComplete":true,"responses":[{"username":"online-peer","hasFreeUploadSlot":true,"files":""" + files + "}]}");
            }
            if (request.Method == HttpMethod.Post && path == "/api/v0/transfers/downloads/batches")
            {
                EnqueueCalls++;
                if (OfflineOnEnqueue) return Json(HttpStatusCode.BadRequest, "User not online");
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var destination = body.RootElement.GetProperty("options").GetProperty("destination").GetString();
                EnqueuedDestinationId = Guid.Parse(destination!.Split('/')[^1]);
                return Json(HttpStatusCode.Created, "{\"failures\":[]}");
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v0/transfers/downloads/batches/"))
                return Json(HttpStatusCode.OK,
                    """{"transfers":[{"id":"11111111-1111-1111-1111-111111111111","username":"offline-peer","state":"Completed","exception":"User not online"}]}""");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
