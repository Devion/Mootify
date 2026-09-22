using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Soulseek;

public sealed record SoulseekFile(
    Guid SearchId,
    string Username,
    string Filename,
    long Size,
    string Extension,
    int? BitRate,
    int? BitDepth,
    int? SampleRate,
    int? Length,
    bool HasFreeUploadSlot,
    int QueueLength,
    int UploadSpeed)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(Filename.Replace('\\', '/'));
    public string Folder => Path.GetDirectoryName(Filename.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
}

internal sealed class SoulseekSearch
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("isComplete")] public bool IsComplete { get; set; }
    [JsonPropertyName("responses")] public List<SoulseekResponse> Responses { get; set; } = [];
}

internal sealed class SoulseekResponse
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("hasFreeUploadSlot")] public bool HasFreeUploadSlot { get; set; }
    [JsonPropertyName("queueLength")] public int QueueLength { get; set; }
    [JsonPropertyName("uploadSpeed")] public int UploadSpeed { get; set; }
    [JsonPropertyName("files")] public List<SoulseekResponseFile> Files { get; set; } = [];
}

internal sealed class SoulseekResponseFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("extension")] public string Extension { get; set; } = "";
    [JsonPropertyName("bitRate")] public int? BitRate { get; set; }
    [JsonPropertyName("bitDepth")] public int? BitDepth { get; set; }
    [JsonPropertyName("sampleRate")] public int? SampleRate { get; set; }
    [JsonPropertyName("length")] public int? Length { get; set; }
    [JsonPropertyName("isLocked")] public bool IsLocked { get; set; }
}

public sealed class SoulseekBatch
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("transfers")] public List<SoulseekTransfer> Transfers { get; set; } = [];
}

public sealed class SoulseekTransfer
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("exception")] public string? Exception { get; set; }
}

public sealed class SoulseekClient(
    HttpClient http,
    IOptionsMonitor<SoulseekOptions> options,
    ILogger<SoulseekClient> log)
{
    private static readonly HashSet<string> AudioExtensions =
        new(["mp3", "flac", "m4a", "aac", "ogg", "opus", "wav", "wma"], StringComparer.OrdinalIgnoreCase);

    // Avoid concert recordings and unofficial releases. Requiring delimiters or common live-
    // recording phrases avoids rejecting legitimate names such as "Live to Tell".
    private static readonly Regex UnwantedVersion = new(
        @"(?:^|[\\/\[\(\{ _.-])bootleg(?:$|[\\/\]\)\} _.-])|" +
        @"[\[\(\{]\s*live(?:\s+at|\s+in|\s+from|\s+on|\s*[,;:\-]|\s*\d{4}|\s*[\]\)\}])|" +
        @"(?:^|[\\/ _.-])live\s+(?:at|in|from|on)\b|" +
        @"[\\/]live(?:\s+\d{4})?[\\/]|\s+-\s+live(?:\s+\d{4})?(?:\.[a-z0-9]+|$)|" +
        @"\b(?:audience recording|soundboard recording|concert recording)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var opts = options.CurrentValue;
        var request = new HttpRequestMessage(method, new Uri(new Uri(opts.BaseUrl.TrimEnd('/') + "/"), path));
        request.Headers.Add("X-API-Key", opts.ApiKey);
        return request;
    }

    public async Task<(bool Ok, string? Version, string? Error)> CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return (false, null, "Not configured — set Soulseek:BaseUrl and Soulseek:ApiKey.");

        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "api/v0/application"), ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (false, null, "slskd rejected the API key.");
            if (!response.IsSuccessStatusCode) return (false, null, $"slskd returned {(int)response.StatusCode}.");

            var body = await response.Content.ReadFromJsonAsync<ApplicationStatus>(cancellationToken: ct);
            return (true, body?.Version?.Current, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<List<SoulseekFile>> SearchAsync(string text, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(text)) return [];

        var opts = options.CurrentValue;
        var id = Guid.NewGuid();
        var start = Request(HttpMethod.Post, "api/v0/searches");
        start.Content = JsonContent.Create(new
        {
            id,
            searchText = text.Trim(),
            searchTimeout = Math.Max(5, opts.SearchTimeoutSeconds),
            fileLimit = opts.FileLimit,
            responseLimit = opts.ResponseLimit,
            filterResponses = true,
            maximumPeerQueueLength = opts.MaximumPeerQueueLength,
        });

        try
        {
            using var started = await http.SendAsync(start, ct);
            if (!started.IsSuccessStatusCode)
            {
                log.LogWarning("slskd search returned {Status}: {Body}", started.StatusCode,
                    await started.Content.ReadAsStringAsync(ct));
                return [];
            }

            SoulseekSearch? search = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, opts.SearchTimeoutSeconds) + 3);
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                using var poll = await http.SendAsync(Request(HttpMethod.Get,
                    $"api/v0/searches/{id}?includeResponses=true"), ct);
                if (!poll.IsSuccessStatusCode) break;
                search = await poll.Content.ReadFromJsonAsync<SoulseekSearch>(cancellationToken: ct);
            }
            while (search is not { IsComplete: true } && DateTimeOffset.UtcNow < deadline);

            return Rank(id, search?.Responses ?? [], opts.MaxResults);
        }
        finally
        {
            try { using var _ = await http.SendAsync(Request(HttpMethod.Delete, $"api/v0/searches/{id}"), CancellationToken.None); }
            catch (Exception ex) { log.LogDebug(ex, "Could not remove completed slskd search {SearchId}", id); }
        }
    }

    private static List<SoulseekFile> Rank(Guid searchId, IEnumerable<SoulseekResponse> responses, int maxResults) =>
        [.. responses
            .SelectMany(r => r.Files.Where(f => !f.IsLocked
                                             && AudioExtensions.Contains(Extension(f))
                                             && !UnwantedVersion.IsMatch(f.Filename))
                .Select(f => new SoulseekFile(searchId, r.Username, f.Filename, f.Size, Extension(f),
                    f.BitRate, f.BitDepth, f.SampleRate, f.Length, r.HasFreeUploadSlot,
                    r.QueueLength, r.UploadSpeed)))
            .OrderByDescending(f => f.HasFreeUploadSlot)
            .ThenBy(f => f.QueueLength)
            .ThenByDescending(f => string.Equals(f.Extension, "mp3", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(f => f.BitRate ?? 0)
            .ThenByDescending(f => f.UploadSpeed)
            .Take(Math.Clamp(maxResults, 1, 500))];

    private static string Extension(SoulseekResponseFile file) =>
        string.IsNullOrWhiteSpace(file.Extension)
            ? Path.GetExtension(file.Filename).TrimStart('.')
            : file.Extension.TrimStart('.');

    public async Task<(bool Ok, string? Error)> EnqueueAsync(
        Guid requestId, SoulseekFile file, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var destination = DestinationFor(requestId);
        var request = Request(HttpMethod.Post, "api/v0/transfers/downloads/batches");
        request.Content = JsonContent.Create(new
        {
            id = requestId,
            username = file.Username,
            files = new[] { new { filename = file.Filename, size = file.Size } },
            options = new { destination, externalId = requestId.ToString() },
        });

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or (HttpStatusCode)207)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                return body.Contains("\"failures\":[]", StringComparison.OrdinalIgnoreCase)
                    ? (true, null)
                    : (response.StatusCode == HttpStatusCode.Created, response.StatusCode == HttpStatusCode.Created ? null : body);
            }

            return (false, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not enqueue slskd download for request {RequestId}", requestId);
            return (false, ex.Message);
        }
    }

    public async Task<SoulseekBatch?> GetBatchAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, $"api/v0/transfers/downloads/batches/{id}"), ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<SoulseekBatch>(cancellationToken: ct)
                : null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read slskd batch {BatchId}", id);
            return null;
        }
    }

    public async Task CancelBatchAsync(Guid id, CancellationToken ct = default)
    {
        var batch = await GetBatchAsync(id, ct);
        if (batch is null) return;
        foreach (var transfer in batch.Transfers)
        {
            try
            {
                using var _ = await http.SendAsync(Request(HttpMethod.Delete,
                    $"api/v0/transfers/downloads/{Uri.EscapeDataString(transfer.Username)}/{transfer.Id}?remove=true"), ct);
            }
            catch (Exception ex) { log.LogDebug(ex, "Could not cancel slskd transfer {TransferId}", transfer.Id); }
        }
    }

    public string DestinationFor(Guid requestId)
    {
        var root = options.CurrentValue.DownloadDestination.Trim().Trim('/', '\\');
        return string.IsNullOrEmpty(root) ? requestId.ToString("N") : $"{root}/{requestId:N}";
    }

    private sealed class ApplicationStatus
    {
        [JsonPropertyName("version")] public VersionInfo? Version { get; set; }
    }

    private sealed class VersionInfo
    {
        [JsonPropertyName("current")] public string? Current { get; set; }
    }
}
