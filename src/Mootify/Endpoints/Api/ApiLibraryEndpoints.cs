using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mootify.Configuration;
using Mootify.Data;

namespace Mootify.Endpoints.Api;

/// <summary>
/// Reading the library. Thin on purpose — the queries live in <see cref="LibraryQueries"/> where
/// the tests can reach them.
/// </summary>
public static class ApiLibraryEndpoints
{
    public static void MapApiLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapApiGroup("/api/v1/library");

        library.MapGet("/artists", async (
            string? q,
            int? skip,
            int? take,
            IDbContextFactory<MootifyDbContext> dbFactory,
            IOptionsMonitor<ApiOptions> options,
            CancellationToken ct) =>
        {
            var (s, t) = ApiSetup.Page(skip, take, options.CurrentValue.MaxPageSize, defaultTake: 200);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Results.Ok(await LibraryQueries.ArtistsAsync(db, q, s, t, ct));
        });

        library.MapGet("/artists/{artistId:guid}", async (
            Guid artistId,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var artist = await LibraryQueries.ArtistAsync(db, artistId, ct);
            if (artist is null) return Results.NotFound();

            var albums = await LibraryQueries.AlbumsAsync(
                db, artistId, null, LibraryQueries.AlbumOrder.ArtistOrder, 0, int.MaxValue, ct);

            return Results.Ok(new { artist, albums = albums.Items });
        });

        // Everything by one artist, for "play this artist" in the car.
        library.MapGet("/artists/{artistId:guid}/tracks", async (
            Guid artistId,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Results.Ok(await LibraryQueries.ArtistTracksAsync(db, artistId, ct));
        });

        library.MapGet("/albums", async (
            Guid? artistId,
            string? q,
            string? sort,
            int? skip,
            int? take,
            IDbContextFactory<MootifyDbContext> dbFactory,
            IOptionsMonitor<ApiOptions> options,
            CancellationToken ct) =>
        {
            var (s, t) = ApiSetup.Page(skip, take, options.CurrentValue.MaxPageSize);

            var order = string.Equals(sort, "recent", StringComparison.OrdinalIgnoreCase)
                ? LibraryQueries.AlbumOrder.RecentFirst
                : LibraryQueries.AlbumOrder.ArtistOrder;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Results.Ok(await LibraryQueries.AlbumsAsync(db, artistId, q, order, s, t, ct));
        });

        library.MapGet("/albums/{albumId:guid}", async (
            Guid albumId,
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var album = await LibraryQueries.AlbumAsync(db, albumId, ct);
            if (album is null) return Results.NotFound();

            return Results.Ok(new { album, tracks = await LibraryQueries.AlbumTracksAsync(db, albumId, ct) });
        });

        library.MapGet("/tracks", async (
            Guid? albumId,
            Guid? artistId,
            string? q,
            int? skip,
            int? take,
            IDbContextFactory<MootifyDbContext> dbFactory,
            IOptionsMonitor<ApiOptions> options,
            CancellationToken ct) =>
        {
            var (s, t) = ApiSetup.Page(skip, take, options.CurrentValue.MaxPageSize);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Results.Ok(await LibraryQueries.TracksAsync(db, albumId, artistId, q, s, t, ct));
        });

        // One call, three lists. This is what a voice search ("play Nirvana") lands on, and a car
        // client can't spend three round trips deciding what to play.
        library.MapGet("/search", async (
            string? q,
            int? take,
            IDbContextFactory<MootifyDbContext> dbFactory,
            IOptionsMonitor<ApiOptions> options,
            CancellationToken ct) =>
        {
            if (!ApiSetup.HasQuery(q)) return Results.Ok(new ApiSearchResults([], [], []));

            var (_, t) = ApiSetup.Page(0, take, options.CurrentValue.MaxPageSize, defaultTake: 20);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Results.Ok(await LibraryQueries.SearchAsync(db, q!, t, ct));
        });

        library.MapGet("/stats", async (
            IDbContextFactory<MootifyDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return Results.Ok(await LibraryQueries.StatsAsync(db, ct));
        });
    }
}
