using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Notifications;

namespace Mootify.Endpoints.Api;

/// <summary>
/// The three things a second client needs that a single-page website never had to expose:
/// where you were, what you actually listened to, and what the cowbell was about.
///
/// <see cref="PlaybackState"/> and <see cref="PlayEvent"/> were in the schema from the start and
/// unused — the website keeps playback in a circuit that dies with the tab. A phone that gets in
/// the car ten minutes later is the case they were designed for.
/// </summary>
public static class ApiSyncEndpoints
{
    public static void MapApiSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapApiGroup("/api/v1");

        // ---- playback state -----------------------------------------------
        api.MapGet("/playback", async (
            HttpContext http,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var state = await db.PlaybackStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId, ct);

            if (state is null)
            {
                return Results.Ok(new ApiPlaybackState(
                    null, 0, [], 0, false, RepeatMode.Off, DateTimeOffset.UtcNow));
            }

            return Results.Ok(new ApiPlaybackState(
                state.CurrentTrackId,
                state.PositionSeconds,
                ParseQueue(state.QueueJson),
                state.QueueIndex,
                state.ShuffleEnabled,
                state.Repeat,
                state.UpdatedAt));
        });

        // The resolved queue, so a client restoring a session doesn't have to fetch 40 tracks
        // one id at a time. Order is preserved; tracks that have since vanished are dropped.
        api.MapGet("/playback/queue", async (
            HttpContext http,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var json = await db.PlaybackStates
                .AsNoTracking()
                .Where(s => s.UserId == userId)
                .Select(s => s.QueueJson)
                .FirstOrDefaultAsync(ct);

            var ids = ParseQueue(json);
            return Results.Ok(await LibraryQueries.TracksByIdAsync(db, ids, ct));
        });

        api.MapPut("/playback", async (
            HttpContext http,
            SavePlaybackRequest body,
            IDbContextFactory<MootifyDbContext> dbFactory,
            IOptionsMonitor<ApiOptions> options,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            // Capped: the queue is a column, not a table, and a client with a bug shouldn't be
            // able to write a megabyte of ids into the row on every skip.
            var queue = (body.Queue ?? []).Take(options.CurrentValue.MaxPageSize).ToList();

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var state = await db.PlaybackStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);

            if (state is null)
            {
                state = new PlaybackState { UserId = userId };
                db.PlaybackStates.Add(state);
            }

            state.CurrentTrackId = body.CurrentTrackId;
            state.PositionSeconds = Math.Max(0, body.PositionSeconds);
            state.QueueJson = JsonSerializer.Serialize(queue);
            state.QueueIndex = Math.Clamp(body.QueueIndex, 0, Math.Max(0, queue.Count - 1));
            state.ShuffleEnabled = body.ShuffleEnabled;
            state.Repeat = body.Repeat;
            state.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- play history -------------------------------------------------
        api.MapPost("/plays", async (
            HttpContext http,
            RecordPlayRequest body,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Silently ignore a track that no longer exists rather than 404ing: this arrives
            // after the fact, often from a queue that was flushed when the app came back online,
            // and there is nothing the client could do about it.
            if (!await db.Tracks.AnyAsync(t => t.Id == body.TrackId, ct))
            {
                return Results.NoContent();
            }

            db.PlayEvents.Add(new PlayEvent
            {
                UserId = userId,
                TrackId = body.TrackId,
                PlayedAt = DateTimeOffset.UtcNow,
                SecondsPlayed = Math.Max(0, body.SecondsPlayed),
            });

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- notifications ------------------------------------------------
        api.MapGet("/notifications", async (
            HttpContext http,
            int? take,
            NotificationDispatcher notifications,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var rows = await notifications.GetRecentAsync(userId, Math.Clamp(take ?? 20, 1, 100), ct);

            return Results.Ok(new
            {
                unread = await notifications.GetUnreadCountAsync(userId, ct),
                items = rows.Select(n => new ApiNotification(
                    n.Id, n.Type, n.Title, n.Body, n.Url, n.CreatedAt, n.ReadAt)).ToList(),
            });
        });

        api.MapPost("/notifications/{notificationId:guid}/read", async (
            HttpContext http,
            Guid notificationId,
            NotificationDispatcher notifications,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            return await notifications.MarkReadAsync(notificationId, userId, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        api.MapPost("/notifications/read-all", async (
            HttpContext http,
            NotificationDispatcher notifications,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            await notifications.MarkAllReadAsync(userId, ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// The queue column is JSON written by whoever saved last. Garbage in it is a reason to start
    /// with an empty queue, never a reason to fail the request the user is making right now.
    /// </summary>
    private static List<Guid> ParseQueue(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
