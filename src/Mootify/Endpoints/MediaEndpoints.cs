using Microsoft.EntityFrameworkCore;
using Mootify.Data;

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
            var track = await db.Tracks
                .AsNoTracking()
                .Where(t => t.Id == trackId && t.IsPresent)
                .Select(t => new { t.Path, t.FileModifiedAt, t.FileSize })
                .FirstOrDefaultAsync(ct);

            if (track is null)
            {
                return Results.NotFound();
            }

            if (!File.Exists(track.Path))
            {
                // The row outlived the file; the next scan will mark it absent.
                return Results.NotFound();
            }

            return Results.File(
                track.Path,
                contentType: "audio/mpeg",
                lastModified: track.FileModifiedAt,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
                    $"\"{track.FileSize:x}-{track.FileModifiedAt.Ticks:x}\""),
                enableRangeProcessing: true);
        });
    }

}
