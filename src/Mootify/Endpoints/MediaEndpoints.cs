using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Transcoding;

namespace Mootify.Endpoints;

public static class MediaEndpoints
{
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var media = app.MapGroup("/media").RequireAuthorization();

        // Range processing is not optional: without it seeking silently breaks and Safari
        // refuses to play at all.
        media.MapGet("/{trackId:guid}", async (
            Guid trackId,
            MootifyDbContext db,
            CancellationToken ct) =>
        {
            var track = await LoadAsync(db, trackId, ct);
            if (track is null || !File.Exists(track.Path)) return Results.NotFound();

            return Serve(track.Path, ContentTypeFor(track.Path), track.FileModifiedAt, track.FileSize);
        });

        // The fallback. Browsers that can't decode the original ask for this instead, and
        // player.js decides which to use — see its canPlayType check. Converted files are
        // cached, so the wait is once per track rather than once per play.
        media.MapGet("/{trackId:guid}/mp3", async (
            Guid trackId,
            MootifyDbContext db,
            TranscodeCache cache,
            CancellationToken ct) =>
        {
            var track = await LoadAsync(db, trackId, ct);
            if (track is null) return Results.NotFound();

            var path = await cache.GetOrCreateAsync(trackId, track.Path, ct);
            if (path is null || !File.Exists(path)) return Results.NotFound();

            var info = new FileInfo(path);
            return Serve(path, "audio/mpeg", info.LastWriteTimeUtc, info.Length);
        });
    }

    private static async Task<TrackFile?> LoadAsync(MootifyDbContext db, Guid trackId, CancellationToken ct) =>
        await db.Tracks
            .AsNoTracking()
            .Where(t => t.Id == trackId && t.IsPresent)
            .Select(t => new TrackFile(t.Path, t.FileModifiedAt, t.FileSize))
            .FirstOrDefaultAsync(ct);

    private sealed record TrackFile(string Path, DateTimeOffset FileModifiedAt, long FileSize);

    private static IResult Serve(string path, string contentType, DateTimeOffset modified, long size) =>
        Results.File(
            path,
            contentType: contentType,
            lastModified: modified,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{size:x}-{modified.Ticks:x}\""),
            enableRangeProcessing: true);

    /// <summary>
    /// The real type, not always audio/mpeg. Sending FLAC as audio/mpeg makes Safari refuse
    /// it outright and confuses caches.
    /// </summary>
    private static string ContentTypeFor(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".flac" => "audio/flac",
            _ => "audio/mpeg",
        };

}
