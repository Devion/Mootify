using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Requests;
using Mootify.Services.Settings;
using Mootify.Services.Soulseek;

namespace Mootify.Tests;

public sealed class RequestServiceTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly FakeSoulseek _handler = new();
    private RequestService _service = null!;
    private AppUser _user = null!;

    public async Task InitializeAsync()
    {
        var options = new SoulseekOptions { BaseUrl = "http://slskd.invalid", ApiKey = "test-key" };
        var client = new SoulseekClient(new HttpClient(_handler),
            new StaticOptionsMonitor<SoulseekOptions>(options), NullLogger<SoulseekClient>.Instance);
        var settings = new SettingsService(_db,
            new StaticOptionsMonitor<AuthOptions>(new AuthOptions()),
            new StaticOptionsMonitor<RequestOptions>(new RequestOptions { MaxOpenPerUser = 100 }));
        _service = new RequestService(_db, client, settings, NullLogger<RequestService>.Instance);
        _user = await _db.AddUserAsync("Dev");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static SoulseekFile File(string name = "Music\\Artist\\01 - Song.mp3") =>
        new(Guid.NewGuid(), "peer", name, 1234, "mp3", 320, null, 44100, 180, true, 0, 1000);

    [Fact]
    public async Task Selected_file_is_enqueued_as_its_own_batch()
    {
        var result = await _service.CreateAsync(_user.Id, File(), "Artist Song", null);
        Assert.True(result.Ok);
        Assert.Equal(1, _handler.EnqueueCalls);

        await using var db = _db.CreateDbContext();
        var row = await db.Requests.SingleAsync();
        Assert.Equal(row.Id, row.SoulseekBatchId);
        Assert.Equal(RequestStatus.Downloading, row.Status);
        Assert.Equal("peer", row.SoulseekUsername);
    }

    [Fact]
    public async Task Same_remote_file_is_not_downloaded_twice_for_the_same_user()
    {
        var file = File();
        Assert.True((await _service.CreateAsync(_user.Id, file, "Artist Song", null)).Ok);
        var duplicate = await _service.CreateAsync(_user.Id, file, "Artist Song", null);
        Assert.False(duplicate.Ok);
        Assert.Equal(1, _handler.EnqueueCalls);
    }

    [Fact]
    public async Task Enqueue_failure_is_kept_as_a_failed_request()
    {
        _handler.Fail = true;
        var result = await _service.CreateAsync(_user.Id, File(), "Artist Song", null);
        Assert.False(result.Ok);

        await using var db = _db.CreateDbContext();
        Assert.Equal(RequestStatus.Failed, (await db.Requests.SingleAsync()).Status);
    }
}

internal sealed class FakeSoulseek : HttpMessageHandler
{
    public int EnqueueCalls { get; private set; }
    public bool Fail { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/downloads/batches"))
        {
            EnqueueCalls++;
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.InternalServerError : HttpStatusCode.Created)
            {
                Content = new StringContent(Fail ? "no" : "{\"failures\":[]}", Encoding.UTF8, "application/json"),
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
