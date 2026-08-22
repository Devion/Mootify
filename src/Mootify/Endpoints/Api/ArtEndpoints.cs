using Microsoft.Net.Http.Headers;
using Mootify.Services.Library;

namespace Mootify.Endpoints.Api;

/// <summary>
/// Album art, at <c>/art/album/{albumId}</c>, and <b>unauthenticated on purpose</b>.
///
/// Android Auto is why. Media3 hands browse items to the car as a <c>MediaMetadata</c> carrying an
/// <c>artworkUri</c>, and the head unit fetches that URI from its own process — our OkHttp client,
/// and therefore our bearer token, is not involved. The alternatives were worse: shipping every
/// cover as bytes through the browse parcel (which has a size limit measured in kilobytes), or
/// showing a car full of grey squares.
///
/// So the album id <i>is</i> the credential — a random GUID that only ever reaches a client that
/// has already authenticated. What leaks if one escapes is a picture of an album cover, which is
/// also on the front of the record. Audio is not treated this way: <c>/media/{trackId}</c> still
/// demands a cookie or a token, because that's the actual content.
/// </summary>
public static class ArtEndpoints
{
    public static void MapArtEndpoints(this IEndpointRouteBuilder app)
    {
        var art = app.MapGroup("/art").AllowAnonymous();

        art.MapGet("/album/{albumId:guid}", async (
            HttpContext http,
            Guid albumId,
            AlbumArtService service,
            CancellationToken ct) =>
        {
            var found = await service.GetAsync(albumId, ct);
            if (found is null) return Results.NotFound();

            // The answer is memoized, so a path that has gone would otherwise 404 for ever
            // rather than until the next look. Forgetting it here is what makes a cover deleted
            // — or moved by the organizer — heal on the following request instead of on a
            // restart.
            if (!File.Exists(found.Path))
            {
                service.Forget(albumId);
                return Results.NotFound();
            }

            var info = new FileInfo(found.Path);

            // Art for a given album never changes without the files changing, and a car scrolling
            // a library asks for hundreds of these. Long max-age plus an ETag means it asks once.
            http.Response.Headers.CacheControl = "private, max-age=604800";

            return Results.File(
                found.Path,
                contentType: found.ContentType,
                lastModified: info.LastWriteTimeUtc,
                entityTag: new EntityTagHeaderValue($"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\""));
        });
    }
}
