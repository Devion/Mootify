using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mootify.Configuration;

namespace Mootify.Services.MusicBrainz;

public sealed record AlbumTrack(
    int Position,
    string Title,
    TimeSpan Duration,
    string RecordingId);

// Only the fields used. MusicBrainz payloads are large and binding all of it would break
// on every schema tweak.
internal sealed class MbReleaseList
{
    [JsonPropertyName("releases")] public List<MbRelease> Releases { get; set; } = [];
}

internal sealed class MbRelease
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("media")] public List<MbMedium> Media { get; set; } = [];

    public int TrackCount => Media.Sum(m => m.Tracks.Count);
    public bool IsOfficial => string.Equals(Status, "Official", StringComparison.OrdinalIgnoreCase);
}

internal sealed class MbMedium
{
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("tracks")] public List<MbTrack> Tracks { get; set; } = [];
}

internal sealed class MbTrack
{
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("length")] public long? Length { get; set; }
    [JsonPropertyName("recording")] public MbRecording? Recording { get; set; }
}

internal sealed class MbRecording
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

/// <summary>
/// Track listings for albums referenced by MusicBrainz metadata.
/// returns a track <i>count</i> and nothing else, and <c>/track</c> only works for albums
/// already in the library. So the listing for something you don't own yet comes from here.
///
/// Deliberately not used for searching. MusicBrainz recording search has no popularity signal,
/// so "bohemian rhapsody" returns tribute bands and karaoke albums ahead of Queen. Album search
/// This fills in tracks for metadata-backed imports and matching.
/// </summary>
public sealed class MusicBrainzClient(
    HttpClient http,
    IMemoryCache cache,
    IOptionsMonitor<MusicBrainzOptions> options,
    ILogger<MusicBrainzClient> log)
{
    /// <summary>Serialises calls so the published one-per-second limit is respected.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastCall = DateTimeOffset.MinValue;

    /// <summary>
    /// The album's tracklist, or empty if MusicBrainz doesn't know it. Cached — tracklists
    /// are historical facts and won't change under us.
    /// </summary>
    public async Task<IReadOnlyList<AlbumTrack>> GetTracksAsync(
        string releaseGroupId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupId)) return [];

        var key = $"mb:rg:{releaseGroupId}";
        if (cache.TryGetValue(key, out IReadOnlyList<AlbumTrack>? cached) && cached is not null)
        {
            return cached;
        }

        var payload = await GetAsync<MbReleaseList>(
            $"release?release-group={Uri.EscapeDataString(releaseGroupId)}&fmt=json&inc=recordings&limit=50", ct);

        var tracks = payload is null ? [] : Flatten(PickRelease(payload.Releases));

        cache.Set(key, tracks, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = options.CurrentValue.CacheDuration,
            Size = Math.Max(1, tracks.Count),
        });

        return tracks;
    }

    /// <summary>
    /// Which pressing to show. Official first — bootlegs and promos have odd tracklists.
    /// Then the earliest, because that's the album as released rather than a remaster with
    /// bonus discs bolted on. Among identical dates, the fuller listing wins so we never
    /// show a truncated one.
    /// </summary>
    internal static MbRelease? PickRelease(List<MbRelease> releases)
    {
        var usable = releases.Where(r => r.TrackCount > 0).ToList();
        if (usable.Count == 0) return null;

        var official = usable.Where(r => r.IsOfficial).ToList();
        var pool = official.Count > 0 ? official : usable;

        return pool
            .OrderBy(r => string.IsNullOrWhiteSpace(r.Date) ? "9999" : r.Date, StringComparer.Ordinal)
            .ThenByDescending(r => r.TrackCount)
            .First();
    }

    internal static IReadOnlyList<AlbumTrack> Flatten(MbRelease? release)
    {
        if (release is null) return [];

        var tracks = new List<AlbumTrack>();

        // Numbering restarts per disc, so a two-disc album would otherwise show two track 1s.
        var running = 0;
        foreach (var medium in release.Media.OrderBy(m => m.Position))
        {
            foreach (var track in medium.Tracks.OrderBy(t => t.Position))
            {
                running++;
                tracks.Add(new AlbumTrack(
                    running,
                    track.Title,
                    TimeSpan.FromMilliseconds(track.Length ?? 0),
                    track.Recording?.Id ?? ""));
            }
        }

        return tracks;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        await Gate.WaitAsync(ct);
        try
        {
            var interval = options.CurrentValue.MinRequestInterval;
            var since = DateTimeOffset.UtcNow - _lastCall;
            if (since < interval)
            {
                await Task.Delay(interval - since, ct);
            }

            try
            {
                return await http.GetFromJsonAsync<T>(path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A tracklist we can't fetch is a worse album view, not a broken app.
                log.LogWarning(ex, "MusicBrainz request failed: {Path}", path);
                return null;
            }
            finally
            {
                _lastCall = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
