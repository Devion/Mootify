using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Services.Auth;
using Mootify.Services.Playlists;

namespace Mootify.Endpoints.Api;

/// <summary>
/// Playlists over HTTP. Every call goes through <see cref="PlaylistService"/> — the API is not
/// allowed its own ownership checks, for exactly the reason the service exists: with personal and
/// team playlists there are four cases to get wrong, and a second copy of the rules is a second
/// place for them to drift.
/// </summary>
public static class ApiPlaylistEndpoints
{
    public static void MapApiPlaylistEndpoints(this IEndpointRouteBuilder app)
    {
        var playlists = app.MapApiGroup("/api/v1/playlists");

        playlists.MapGet("", async (
            HttpContext http, PlaylistService service, CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var rows = await service.GetForUserAsync(userId, ct);

            return Results.Ok(rows
                .Select(p => new ApiPlaylist(
                    p.Id, p.Name, null, p.TrackCount,
                    (long)p.TotalDuration.TotalMilliseconds, p.TeamId, p.TeamName))
                .ToList());
        });

        // skip/take, because a playlist is a list somebody can make arbitrarily long. It was
        // unpaged, and a car browsing a 200-track playlist re-fetched all 200 rows for every
        // twenty it showed. The page defaults to PlaylistService.DefaultPageSize and is clamped
        // to the same ceiling every other list here uses.
        playlists.MapGet("/{playlistId:guid}", async (
            HttpContext http,
            Guid playlistId,
            int? skip,
            int? take,
            PlaylistService service,
            IOptionsMonitor<ApiOptions> options,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var (s, t) = ApiSetup.Page(skip, take, options.CurrentValue.MaxPageSize, PlaylistService.DefaultPageSize);

            // Null both for "no such playlist" and "not yours" — the client's move is the same
            // either way, and telling them apart would leak the existence of other people's lists.
            var page = await service.GetPageAsync(playlistId, userId, s, t, ct);
            if (page is null) return Results.NotFound();

            return Results.Ok(new ApiPlaylistDetail(
                page.Id,
                page.Name,
                page.Description,
                page.TeamId,
                page.TeamName,
                // GetPageAsync already refused anything this user can't read, and read == edit.
                CanEdit: true,
                CanDelete: page.CanDelete,
                TrackCount: page.Total,
                DurationMs: ApiMap.Ms(page.TotalDuration),
                Items: Items(page)));
        });

        // Just the rows. What a browse tree asks for on page 2 and after — the header it already
        // has, and re-sending it per page is the shape this endpoint exists to stop.
        playlists.MapGet("/{playlistId:guid}/items", async (
            HttpContext http,
            Guid playlistId,
            int? skip,
            int? take,
            PlaylistService service,
            IOptionsMonitor<ApiOptions> options,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var (s, t) = ApiSetup.Page(skip, take, options.CurrentValue.MaxPageSize, PlaylistService.DefaultPageSize);

            var page = await service.GetPageAsync(playlistId, userId, s, t, ct);
            return page is null ? Results.NotFound() : Results.Ok(Items(page));
        });

        // Every track id, in order, in one column. This is what "play the whole playlist" needs,
        // and it stays small however long the list is — a client building a queue does not have to
        // page through metadata it already has or is about to fetch anyway.
        playlists.MapGet("/{playlistId:guid}/trackids", async (
            HttpContext http,
            Guid playlistId,
            PlaylistService service,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var ids = await service.GetTrackIdsAsync(playlistId, userId, ct);

            return ids is null ? Results.NotFound() : Results.Ok(ids);
        });

        // Who else is on this playlist right now. Empty for a playlist this user can't read —
        // the same answer as "nobody", so it can't be used to discover somebody else's list.
        playlists.MapGet("/{playlistId:guid}/listeners", async (
            HttpContext http,
            Guid playlistId,
            ListeningService listening,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var rows = await listening.GetForPlaylistAsync(playlistId, userId, ct);

            return Results.Ok(rows.Select(ApiMap.Listener).ToList());
        });

        playlists.MapPost("", async (
            HttpContext http,
            CreatePlaylistRequest body,
            PlaylistService service,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            var id = await service.CreateAsync(userId, body.Name, body.TeamId, ct);

            return id is null
                ? Results.Problem("Couldn't create that playlist. Are you in that team?", statusCode: 403)
                : Results.Ok(new { id });
        });

        playlists.MapPut("/{playlistId:guid}", async (
            HttpContext http,
            Guid playlistId,
            RenamePlaylistRequest body,
            PlaylistService service,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);
            return await service.RenameAsync(playlistId, userId, body.Name, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });

        playlists.MapDelete("/{playlistId:guid}", async (
            HttpContext http,
            Guid playlistId,
            PlaylistService service,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            // Deleting a team playlist is an owner's call — see PlaylistAccess.CanDelete.
            return await service.DeleteAsync(playlistId, userId, ct)
                ? Results.NoContent()
                : Results.Problem("That playlist is gone, or isn't yours to delete.", statusCode: 403);
        });

        playlists.MapPost("/{playlistId:guid}/tracks", async (
            HttpContext http,
            Guid playlistId,
            AddTracksRequest body,
            PlaylistService service,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            if (body.TrackIds.Count == 0) return Results.Ok(new { added = 0 });

            // AddTracksAsync answers 0 both for "refused" and "nothing new to add", so ask first —
            // the app needs to show "not yours" differently from "already in there".
            if (!await service.CanEditAsync(playlistId, userId, ct))
            {
                return Results.Problem("That playlist isn't yours to change.", statusCode: 403);
            }

            var added = await service.AddTracksAsync(playlistId, userId, body.TrackIds, null, ct);
            return Results.Ok(new { added });
        });

        playlists.MapDelete("/{playlistId:guid}/items/{itemId:guid}", async (
            HttpContext http,
            Guid playlistId,
            Guid itemId,
            PlaylistService service,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            // playlistId is in the route for a tidy URL; the service resolves the item's own
            // playlist and checks access against that, so a mismatched pair can't sneak through.
            return await service.RemoveItemAsync(itemId, userId, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    /// <summary>
    /// A page of rows as items. Items rather than tracks, because the same song can legitimately
    /// appear in a playlist twice and removing one is addressed by the item's own id.
    /// </summary>
    private static ApiPage<ApiPlaylistItem> Items(PlaylistTrackPage page) => new(
        page.Total,
        page.Skip,
        page.Take,
        [.. page.Rows.Select(ApiMap.PlaylistItem)]);
}
