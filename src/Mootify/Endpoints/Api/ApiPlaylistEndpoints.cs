using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Auth;
using Mootify.Services.Playlists;
using Mootify.Services.Teams;

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

        playlists.MapGet("/{playlistId:guid}", async (
            HttpContext http,
            Guid playlistId,
            PlaylistService service,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var userId = ApiPrincipal.GetRequiredUserId(http.User);

            // Returns null both for "no such playlist" and "not yours" — the client's move is the
            // same either way, and telling them apart would leak the existence of other people's
            // lists.
            var playlist = await service.GetAsync(playlistId, userId, ct);
            if (playlist is null) return Results.NotFound();

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var trackIds = playlist.Items
                .Where(i => i.Track?.IsPresent == true)
                .Select(i => i.TrackId)
                .ToList();

            var tracks = (await LibraryQueries.TracksByIdAsync(db, trackIds, ct))
                .ToDictionary(t => t.Id);

            // Items, not tracks: the same song can legitimately appear twice, so removing one is
            // a per-item operation and the client needs the item id.
            var items = playlist.Items
                .Where(i => i.Track?.IsPresent == true && tracks.ContainsKey(i.TrackId))
                .OrderBy(i => i.SortKey)
                .Select(i => new ApiPlaylistItem(i.Id, tracks[i.TrackId], i.AddedAt))
                .ToList();

            var canDelete = await CanDeleteAsync(db, playlist, userId, ct);

            return Results.Ok(new ApiPlaylistDetail(
                playlist.Id,
                playlist.Name,
                playlist.Description,
                playlist.TeamId,
                playlist.Team?.Name,
                CanEdit: true, // GetAsync already refused anything this user can't read, and read == edit
                CanDelete: canDelete,
                Items: items));
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
    /// Whether this user could destroy the playlist, so the app can hide a button that would
    /// only fail. The decision itself is still made server-side on the delete call.
    /// </summary>
    private static async Task<bool> CanDeleteAsync(
        MootifyDbContext db, Playlist playlist, Guid userId, CancellationToken ct)
    {
        var teamIds = await TeamService.GetTeamIdsAsync(db, userId, ct);
        var ownedTeamIds = await TeamService.GetOwnedTeamIdsAsync(db, userId, ct);
        return PlaylistAccess.CanDelete(playlist, userId, teamIds, ownedTeamIds);
    }
}
