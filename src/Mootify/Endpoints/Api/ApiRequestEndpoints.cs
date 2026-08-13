using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Lidarr;
using Mootify.Services.MusicBrainz;
using Mootify.Services.Requests;

namespace Mootify.Endpoints.Api;

/// <summary>
/// Asking for music that isn't there yet — the thing Mootify does that a music player doesn't.
///
/// The shape mirrors the website's search page because the underlying constraint is the same:
/// Lidarr fetches albums and has no song index, so a request is always "this album, keep one
/// track". The client searches albums, expands one to a tracklist from MusicBrainz, and picks a
/// song; the recording MBID it sends is what finds that song once the album lands.
/// </summary>
public static class ApiRequestEndpoints
{
    /// <summary>
    /// How long a search result stays available to turn into a request. Lidarr has no
    /// "get album by MBID" for something it hasn't adopted yet, so the album object the search
    /// returned has to be kept — see the fallback in the create handler for when this expires.
    /// </summary>
    private static readonly TimeSpan LookupCacheDuration = TimeSpan.FromMinutes(30);

    public static void MapApiRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var requests = app.MapApiGroup("/api/v1/requests");

        requests.MapGet("", async (
            HttpContext http, RequestService service, CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var rows = await service.GetForUserAsync(userId, ct);

            return Results.Ok(rows.Select(Map).ToList());
        });

        // Album search against Lidarr. Not the library — /library/search is that.
        requests.MapGet("/search", async (
            string? q,
            LidarrClient lidarr,
            IMemoryCache cache,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (!lidarr.IsConfigured)
            {
                return Results.Problem("Lidarr isn't configured, so nothing new can be fetched.", statusCode: 503);
            }

            if (!ApiSetup.HasQuery(q)) return Results.Ok(new List<ApiRemoteAlbum>());

            var albums = await lidarr.LookupAlbumsAsync(q!.Trim(), ct);

            // Remember what we found: the create call needs this object back, and Lidarr can't
            // hand it over a second time by MBID alone.
            foreach (var album in albums.Where(a => a.MusicBrainzId is not null))
            {
                cache.Set(CacheKey(album.MusicBrainzId!), album, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = LookupCacheDuration,
                    // The shared cache is size-limited, so every entry has to declare one.
                    Size = 1,
                });
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var mbids = albums.Where(a => a.MusicBrainzId is not null).Select(a => a.MusicBrainzId!).ToList();
            var known = await db.Albums
                .AsNoTracking()
                .Where(a => a.MusicBrainzId != null && mbids.Contains(a.MusicBrainzId)
                         && a.Tracks.Any(t => t.IsPresent))
                .Select(a => a.MusicBrainzId!)
                .ToListAsync(ct);

            return Results.Ok(albums
                .Select(a => new ApiRemoteAlbum(
                    a.MusicBrainzId,
                    a.Title,
                    a.Artist?.ArtistName ?? "",
                    a.Artist?.MusicBrainzId,
                    a.Year,
                    a.AlbumType,
                    a.CoverUrl,
                    a.MusicBrainzId is not null && known.Contains(a.MusicBrainzId)))
                .ToList());
        });

        // The tracklist, from MusicBrainz. Lidarr's album lookup returns a track count and no
        // titles, which is why this is a second call to a second service.
        requests.MapGet("/albums/{albumMbid}/tracks", async (
            string albumMbid,
            MusicBrainzClient musicBrainz,
            CancellationToken ct) =>
        {
            var tracks = await musicBrainz.GetTracksAsync(albumMbid, ct);

            return Results.Ok(tracks
                .Select(t => new ApiRemoteTrack(
                    t.Position,
                    t.Title,
                    t.RecordingId,
                    t.Duration == TimeSpan.Zero ? null : (long)t.Duration.TotalMilliseconds))
                .ToList());
        });

        requests.MapPost("", async (
            HttpContext http,
            CreateRequestBody body,
            RequestService service,
            LidarrClient lidarr,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            if (!lidarr.IsConfigured)
            {
                return Results.Problem("Lidarr isn't configured, so nothing new can be fetched.", statusCode: 503);
            }

            if (string.IsNullOrWhiteSpace(body.AlbumMusicBrainzId))
            {
                return Results.Problem("A request needs an album to fetch.", statusCode: 400);
            }

            var album = await ResolveAlbumAsync(body, lidarr, cache, ct);
            if (album is null)
            {
                // The client can recover from this: search again and post the fresh result.
                return Results.Problem(
                    "That search result has gone stale. Search again and pick it once more.",
                    statusCode: 409);
            }

            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            var result = await service.CreateAsync(
                userId,
                album,
                body.Kind,
                body.TrackTitle,
                body.RecordingMusicBrainzId,
                body.TargetPlaylistId,
                ct);

            // The quota, a duplicate, or Lidarr saying no — all of them are the user's business
            // and all of them read fine as a 400 with the service's own sentence.
            return result.Ok
                ? Results.Ok(new { id = result.RequestId })
                : Results.Problem(result.Error, statusCode: 400);
        });
    }

    /// <summary>
    /// Gets back the <see cref="LidarrAlbum"/> the client picked. Cache first; failing that,
    /// re-run the search term the client echoed and match on MBID. Without the fallback, leaving
    /// the app open over lunch and then tapping Request would fail for no reason a user could see.
    /// </summary>
    private static async Task<LidarrAlbum?> ResolveAlbumAsync(
        CreateRequestBody body, LidarrClient lidarr, IMemoryCache cache, CancellationToken ct)
    {
        var mbid = body.AlbumMusicBrainzId!;

        if (cache.TryGetValue(CacheKey(mbid), out LidarrAlbum? cached) && cached is not null)
        {
            return cached;
        }

        if (!ApiSetup.HasQuery(body.SearchTerm)) return null;

        var albums = await lidarr.LookupAlbumsAsync(body.SearchTerm!.Trim(), ct);
        return albums.FirstOrDefault(a => a.MusicBrainzId == mbid);
    }

    private static string CacheKey(string mbid) => $"api:lidarr:album:{mbid}";

    private static ApiRequest Map(Request r) => new(
        r.Id,
        r.Kind,
        r.Status,
        r.Query,
        r.ArtistName,
        r.AlbumTitle,
        r.TrackTitle,
        r.TargetPlaylistId,
        r.TargetPlaylist?.Name,
        r.FailureReason,
        r.CreatedAt,
        r.CompletedAt);
}
