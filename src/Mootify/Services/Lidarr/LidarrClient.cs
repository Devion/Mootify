using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.Lidarr;

// Only the fields Mootify actually uses. Lidarr's payloads are enormous and binding all of
// it would break on every upgrade.
public sealed class LidarrAlbum
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("foreignAlbumId")] public string? MusicBrainzId { get; set; }
    [JsonPropertyName("albumType")] public string? AlbumType { get; set; }
    [JsonPropertyName("releaseDate")] public DateTimeOffset? ReleaseDate { get; set; }
    [JsonPropertyName("monitored")] public bool Monitored { get; set; }
    [JsonPropertyName("artist")] public LidarrArtist? Artist { get; set; }
    [JsonPropertyName("images")] public List<LidarrImage> Images { get; set; } = [];
    [JsonPropertyName("statistics")] public LidarrStatistics? Statistics { get; set; }

    public string? CoverUrl => Images.FirstOrDefault(i => i.CoverType == "cover")?.RemoteUrl
                               ?? Images.FirstOrDefault()?.RemoteUrl;

    public int? Year => ReleaseDate?.Year;
}

public sealed class LidarrArtist
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("artistName")] public string ArtistName { get; set; } = "";
    [JsonPropertyName("foreignArtistId")] public string? MusicBrainzId { get; set; }
    [JsonPropertyName("monitored")] public bool Monitored { get; set; }
}

public sealed class LidarrImage
{
    [JsonPropertyName("coverType")] public string CoverType { get; set; } = "";
    [JsonPropertyName("remoteUrl")] public string? RemoteUrl { get; set; }
}

public sealed class LidarrStatistics
{
    [JsonPropertyName("trackCount")] public int TrackCount { get; set; }
    [JsonPropertyName("trackFileCount")] public int TrackFileCount { get; set; }
    [JsonPropertyName("percentOfTracks")] public double PercentOfTracks { get; set; }
}

public sealed class LidarrQueueItem
{
    [JsonPropertyName("albumId")] public int AlbumId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("trackedDownloadState")] public string? TrackedDownloadState { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
}

public sealed class LidarrTrackFile
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("albumId")] public int AlbumId { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("quality")] public JsonElement Quality { get; set; }
}

/// <summary>
/// Typed client for Lidarr v1. Retry and circuit-breaker come from the resilience handler
/// configured in Program.cs — a Lidarr that's down must not take Mootify with it.
/// </summary>
public sealed class LidarrClient(
    HttpClient http,
    IOptionsMonitor<LidarrOptions> options,
    ILogger<LidarrClient> log)
{
    public bool IsConfigured => options.CurrentValue.IsConfigured;

    /// <summary>Whether <see cref="AddAlbumAsync"/> kicks off a search of its own.</summary>
    public bool SearchesOnAdd => options.CurrentValue.SearchOnAdd;

    /// <summary>
    /// The key is read per call rather than captured at construction, so editing
    /// mootify.json actually takes effect without a restart.
    /// </summary>
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var opts = options.CurrentValue;
        var request = new HttpRequestMessage(method, new Uri(new Uri(opts.BaseUrl.TrimEnd('/') + "/"), path));
        request.Headers.Add("X-Api-Key", opts.ApiKey);
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // By far the most common setup failure. Say so plainly.
                log.LogError("Lidarr rejected the API key (401). Check Lidarr:ApiKey in mootify.json.");
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Lidarr {Path} returned {Status}", request.RequestUri?.PathAndQuery, response.StatusCode);
                return default;
            }

            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Lidarr call failed: {Path}", request.RequestUri?.PathAndQuery);
            return default;
        }
    }

    public async Task<(bool Ok, string? Version, string? Error)> CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return (false, null, "Not configured — set Lidarr:BaseUrl and Lidarr:ApiKey.");
        }

        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "api/v1/system/status"), ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (false, null, "Lidarr rejected the API key.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"Lidarr returned {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var version = payload.TryGetProperty("version", out var v) ? v.GetString() : null;
            return (true, version, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>Search Lidarr's metadata for albums matching free text.</summary>
    public async Task<List<LidarrAlbum>> LookupAlbumsAsync(string term, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(term)) return [];

        var result = await SendAsync<List<LidarrAlbum>>(
            Request(HttpMethod.Get, $"api/v1/album/lookup?term={Uri.EscapeDataString(term)}"), ct);

        return result ?? [];
    }

    public async Task<List<LidarrArtist>> LookupArtistsAsync(string term, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(term)) return [];

        var result = await SendAsync<List<LidarrArtist>>(
            Request(HttpMethod.Get, $"api/v1/artist/lookup?term={Uri.EscapeDataString(term)}"), ct);

        return result ?? [];
    }

    public async Task<LidarrAlbum?> GetAlbumAsync(int albumId, CancellationToken ct = default) =>
        await SendAsync<LidarrAlbum>(Request(HttpMethod.Get, $"api/v1/album/{albumId}"), ct);

    /// <summary>
    /// The whole queue, not the first page of it. An import can put several hundred albums in
    /// flight at once, and a request whose queue record fell off page one looks to the reconciler
    /// exactly like a request Lidarr never grabbed — which is how it ends up being re-searched
    /// while it is already downloading.
    /// </summary>
    public async Task<List<LidarrQueueItem>> GetQueueAsync(CancellationToken ct = default)
    {
        const int PageSize = 200;
        // A stop so a Lidarr that ignores paging can't spin this forever.
        const int MaxPages = 25;

        var all = new List<LidarrQueueItem>();

        for (var pageNumber = 1; pageNumber <= MaxPages; pageNumber++)
        {
            var page = await SendAsync<JsonElement>(
                Request(HttpMethod.Get, $"api/v1/queue?page={pageNumber}&pageSize={PageSize}&includeAlbum=true"), ct);

            if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("records", out var records))
            {
                break;
            }

            var batch = records.Deserialize<List<LidarrQueueItem>>() ?? [];
            all.AddRange(batch);

            if (batch.Count < PageSize) break;

            var total = page.TryGetProperty("totalRecords", out var t) && t.TryGetInt32(out var n) ? n : 0;
            if (total > 0 && all.Count >= total) break;
        }

        return all;
    }

    /// <summary>
    /// Asks Lidarr to go looking for an album it already has monitored. This is the call that
    /// "Check now" is really about: <c>AddAlbumAsync</c> searches once at request time, and a
    /// search that came back empty leaves nothing behind to retry it.
    /// </summary>
    public async Task<bool> SearchAlbumAsync(int albumId, CancellationToken ct = default) =>
        await SearchAlbumsAsync([albumId], ct);

    /// <summary>
    /// One command for many albums. Lidarr runs commands one at a time, so 200 separate
    /// AlbumSearch commands is 200 queue entries it works through in series; batched, it is one.
    /// </summary>
    public async Task<bool> SearchAlbumsAsync(IReadOnlyCollection<int> albumIds, CancellationToken ct = default)
    {
        if (!IsConfigured || albumIds.Count == 0) return false;

        var command = Request(HttpMethod.Post, "api/v1/command");
        command.Content = JsonContent.Create(new { name = "AlbumSearch", albumIds });

        var result = await SendAsync<JsonElement>(command, ct);
        return result.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// Monitor state for albums. Cancelling a request unmonitors, so Lidarr stops chasing
    /// something nobody is waiting for any more.
    /// </summary>
    public async Task<bool> SetAlbumsMonitoredAsync(
        IReadOnlyCollection<int> albumIds, bool monitored, CancellationToken ct = default)
    {
        if (!IsConfigured || albumIds.Count == 0) return false;

        var monitor = Request(HttpMethod.Put, "api/v1/album/monitor");
        monitor.Content = JsonContent.Create(new { albumIds, monitored });

        var result = await SendAsync<JsonElement>(monitor, ct);
        return result.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    public async Task<List<LidarrTrackFile>> GetTrackFilesAsync(int albumId, CancellationToken ct = default)
    {
        var result = await SendAsync<List<LidarrTrackFile>>(
            Request(HttpMethod.Get, $"api/v1/trackfile?albumId={albumId}"), ct);

        return result ?? [];
    }

    /// <summary>
    /// Adds the artist (Lidarr has no concept of adding a bare album) with only the requested
    /// album monitored, then kicks off a search. Returns the Lidarr album id to track.
    /// </summary>
    public async Task<(int? ArtistId, int? AlbumId, string? Error)> AddAlbumAsync(
        LidarrAlbum album,
        CancellationToken ct = default)
    {
        var opts = options.CurrentValue;

        if (album.Artist?.MusicBrainzId is null || album.MusicBrainzId is null)
        {
            return (null, null, "That result is missing MusicBrainz ids, so Lidarr can't take it.");
        }

        // Already tracked?
        var existing = await SendAsync<List<LidarrArtist>>(Request(HttpMethod.Get, "api/v1/artist"), ct) ?? [];
        var artist = existing.FirstOrDefault(a => a.MusicBrainzId == album.Artist.MusicBrainzId);

        if (artist is null)
        {
            var body = new
            {
                artistName = album.Artist.ArtistName,
                foreignArtistId = album.Artist.MusicBrainzId,
                qualityProfileId = opts.QualityProfileId,
                metadataProfileId = opts.MetadataProfileId,
                rootFolderPath = opts.RootFolderPath,
                monitored = true,
                monitorNewItems = "none",
                addOptions = new
                {
                    monitor = "none",
                    searchForMissingAlbums = false,
                },
            };

            var request = Request(HttpMethod.Post, "api/v1/artist");
            request.Content = JsonContent.Create(body);
            artist = await SendAsync<LidarrArtist>(request, ct);

            if (artist is null)
            {
                return (null, null, "Lidarr refused to add the artist.");
            }

            log.LogInformation("Added artist {Artist} to Lidarr as {Id}", artist.ArtistName, artist.Id);
        }

        // Find the album under that artist and monitor just it.
        var albums = await SendAsync<List<LidarrAlbum>>(
            Request(HttpMethod.Get, $"api/v1/album?artistId={artist.Id}"), ct) ?? [];

        var target = albums.FirstOrDefault(a => a.MusicBrainzId == album.MusicBrainzId);
        if (target is null)
        {
            return (artist.Id, null, "Lidarr added the artist but doesn't list that album yet. Try again shortly.");
        }

        await SetAlbumsMonitoredAsync([target.Id], true, ct);

        if (opts.SearchOnAdd)
        {
            await SearchAlbumAsync(target.Id, ct);
        }

        return (artist.Id, target.Id, null);
    }
}
