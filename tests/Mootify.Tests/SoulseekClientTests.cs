using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mootify.Configuration;
using Mootify.Services.Soulseek;

namespace Mootify.Tests;

public sealed class SoulseekClientTests
{
    [Fact]
    public async Task Search_excludes_live_and_bootleg_files_and_deletes_the_search()
    {
        var handler = new SearchHandler();
        var client = new SoulseekClient(new HttpClient(handler),
            new StaticOptionsMonitor<SoulseekOptions>(new SoulseekOptions
            {
                BaseUrl = "http://slskd.invalid",
                ApiKey = "test-key",
                SearchTimeoutSeconds = 5,
            }), NullLogger<SoulseekClient>.Instance);

        var files = await client.SearchAsync("artist song");

        Assert.Single(files);
        Assert.Equal("Music\\Artist\\01 Song.mp3", files[0].Filename);
        Assert.Equal("Artist Song", handler.PostedSearchText);
        Assert.Equal(5000, handler.PostedSearchTimeout);
        Assert.True(handler.Deleted);
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public bool Deleted { get; private set; }
        public string? PostedSearchText { get; private set; }
        public int? PostedSearchTimeout { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                using var document = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                PostedSearchText = document.RootElement.GetProperty("searchText").GetString();
                PostedSearchTimeout = document.RootElement.GetProperty("searchTimeout").GetInt32();
                return Json(HttpStatusCode.OK, "{\"id\":\"00000000-0000-0000-0000-000000000000\",\"isComplete\":false}");
            }

            if (request.Method == HttpMethod.Delete)
            {
                Deleted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            var id = request.RequestUri!.Segments[^1].Split('?')[0];
            var body = JsonSerializer.Serialize(new
            {
                id,
                isComplete = true,
                responses = new[]
                {
                    new
                    {
                        username = "peer", hasFreeUploadSlot = true, queueLength = 0, uploadSpeed = 1000,
                        files = new[]
                        {
                            new { filename = "Music\\Artist\\01 Song.mp3", size = 1000, extension = "mp3", bitRate = (int?)320, isLocked = false },
                            new { filename = "Music\\Artist (Live at Berlin)\\01 Song.flac", size = 2000, extension = "flac", bitRate = (int?)null, isLocked = false },
                            new { filename = "Music\\Artist\\Bootleg\\01 Song.mp3", size = 1000, extension = "mp3", bitRate = (int?)320, isLocked = false },
                        },
                    },
                },
            });
            return Json(HttpStatusCode.OK, body);
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
