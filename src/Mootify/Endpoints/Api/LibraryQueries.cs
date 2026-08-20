using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Library;

namespace Mootify.Endpoints.Api;

/// <summary>
/// Every library read the API does, in one place and away from the endpoint lambdas, so the
/// tests can run them against real SQLite. That is not ceremony: the two persistence gotchas in
/// this codebase are both <i>translation</i> failures that only show up when a real provider is
/// asked to compile the query.
///
/// - <see cref="Track.Duration"/> is <c>[NotMapped]</c>. Durations are selected as
///   <see cref="Track.DurationTicks"/> and converted after materializing.
/// - <see cref="DateTimeOffset"/> goes through a value converter, so aggregating one
///   (<c>MAX(AddedAt)</c> for "recently added") is a query worth having a test for rather than
///   a query worth assuming.
///
/// Projections throughout, never entities: <see cref="Track.Path"/> is an absolute path on the
/// server's share and must never reach a client.
/// </summary>
internal static class LibraryQueries
{
    internal enum TrackOrder
    {
        /// <summary>Disc then track — how an album is meant to be heard.</summary>
        AlbumOrder,

        /// <summary>Alphabetical, for search results and flat lists.</summary>
        TitleOrder,
    }

    internal enum AlbumOrder
    {
        /// <summary>Artist, then chronological within the artist.</summary>
        ArtistOrder,

        /// <summary>Newest arrival first — what the app's home screen opens on.</summary>
        RecentFirst,
    }

    // ---- artists ----------------------------------------------------------

    /// <summary>
    /// Artists with nothing playable are dead rows left by a removed folder — the scanner keeps
    /// them so playlists don't break, and every list in the app hides them.
    /// </summary>
    private static IQueryable<Artist> ArtistBase(MootifyDbContext db) =>
        db.Artists.AsNoTracking().Where(a => a.Tracks.Any(t => t.IsPresent));

    private static IQueryable<ApiArtist> ArtistShape(IQueryable<Artist> query) =>
        query.Select(a => new ApiArtist(
            a.Id,
            a.Name,
            a.SortName,
            a.Albums.Count(al => al.Tracks.Any(t => t.IsPresent)),
            a.Tracks.Count(t => t.IsPresent)));

    internal static async Task<ApiPage<ApiArtist>> ArtistsAsync(
        MootifyDbContext db, string? q, int skip, int take, CancellationToken ct)
    {
        // A filter of nothing but wildcards is a filter of nothing — the list stays whole,
        // which is what LibraryMatch.Artist answers for an empty term.
        var query = ArtistBase(db).Where(LibraryMatch.Artist(q));

        var total = await query.CountAsync(ct);
        var rows = await ArtistShape(query.OrderBy(a => a.SortName).Skip(skip).Take(take)).ToListAsync(ct);

        return new ApiPage<ApiArtist>(total, skip, take, rows);
    }

    internal static Task<ApiArtist?> ArtistAsync(MootifyDbContext db, Guid artistId, CancellationToken ct) =>
        ArtistShape(db.Artists.AsNoTracking().Where(a => a.Id == artistId)).FirstOrDefaultAsync(ct);

    // ---- albums -----------------------------------------------------------

    private static IQueryable<Album> AlbumBase(MootifyDbContext db) =>
        db.Albums.AsNoTracking().Where(a => a.Tracks.Any(t => t.IsPresent));

    internal static async Task<ApiPage<ApiAlbum>> AlbumsAsync(
        MootifyDbContext db,
        Guid? artistId,
        string? q,
        AlbumOrder order,
        int skip,
        int take,
        CancellationToken ct)
    {
        var query = AlbumBase(db)
            .Where(a => artistId == null || a.ArtistId == artistId)
            .Where(LibraryMatch.Album(q));

        var total = await query.CountAsync(ct);
        var rows = await AlbumRowsAsync(query, order, skip, take, ct);

        return new ApiPage<ApiAlbum>(total, skip, take, rows);
    }

    internal static async Task<ApiAlbum?> AlbumAsync(MootifyDbContext db, Guid albumId, CancellationToken ct)
    {
        var rows = await AlbumRowsAsync(
            AlbumBase(db).Where(a => a.Id == albumId), AlbumOrder.ArtistOrder, 0, 1, ct);

        return rows.FirstOrDefault();
    }

    private static async Task<List<ApiAlbum>> AlbumRowsAsync(
        IQueryable<Album> query, AlbumOrder order, int skip, int take, CancellationToken ct)
    {
        var shaped = query.Select(a => new
        {
            a.Id,
            a.Title,
            a.ArtistId,
            ArtistName = a.Artist!.Name,
            a.Year,
            TrackCount = a.Tracks.Count(t => t.IsPresent),
            Ticks = a.Tracks.Where(t => t.IsPresent).Sum(t => t.DurationTicks),
            AddedAt = a.Tracks.Where(t => t.IsPresent).Max(t => t.AddedAt),
        });

        shaped = order == AlbumOrder.RecentFirst
            ? shaped.OrderByDescending(a => a.AddedAt)
            : shaped.OrderBy(a => a.ArtistName).ThenBy(a => a.Year).ThenBy(a => a.Title);

        var rows = await shaped.Skip(skip).Take(take).ToListAsync(ct);

        return
        [
            .. rows.Select(a => new ApiAlbum(
                a.Id, a.Title, a.ArtistId, a.ArtistName, a.Year, a.TrackCount,
                ApiMap.Ms(a.Ticks), ApiMap.ArtUrl(a.Id)))
        ];
    }

    // ---- tracks -----------------------------------------------------------

    private static Expression<Func<Track, bool>> InScope(Guid? albumId, Guid? artistId) =>
        t => (albumId == null || t.AlbumId == albumId)
          && (artistId == null || t.ArtistId == artistId);

    /// <summary>
    /// Narrowing a list, so <c>q</c> matches the <b>title</b> only — see
    /// <see cref="LibraryMatch.TrackTitle"/>. Searching, which matches the artist and album too,
    /// is <see cref="SearchAsync"/>.
    /// </summary>
    internal static async Task<ApiPage<ApiTrack>> TracksAsync(
        MootifyDbContext db,
        Guid? albumId,
        Guid? artistId,
        string? q,
        int skip,
        int take,
        CancellationToken ct)
    {
        var query = db.Tracks.AsNoTracking()
            .Where(t => t.IsPresent)
            .Where(InScope(albumId, artistId))
            .Where(LibraryMatch.TrackTitle(q));

        var total = await query.CountAsync(ct);

        // A request scoped to one album wants it in album order; anything else is a flat list.
        var order = albumId is null ? TrackOrder.TitleOrder : TrackOrder.AlbumOrder;
        var rows = await TrackRowsAsync(query, order, skip, take, ct);

        return new ApiPage<ApiTrack>(total, skip, take, rows);
    }

    internal static Task<List<ApiTrack>> AlbumTracksAsync(
        MootifyDbContext db, Guid albumId, CancellationToken ct) =>
        TrackRowsAsync(
            db.Tracks.AsNoTracking().Where(t => t.IsPresent && t.AlbumId == albumId),
            TrackOrder.AlbumOrder,
            0,
            int.MaxValue,
            ct);

    /// <summary>
    /// Everything by an artist, in album order. This is what "play this artist" resolves to, and
    /// what a car's browse tree hands the player when somebody picks an artist rather than a song.
    /// </summary>
    internal static Task<List<ApiTrack>> ArtistTracksAsync(
        MootifyDbContext db, Guid artistId, CancellationToken ct) =>
        TrackRowsAsync(
            db.Tracks.AsNoTracking().Where(t => t.IsPresent && t.ArtistId == artistId),
            TrackOrder.AlbumOrder,
            0,
            int.MaxValue,
            ct,
            albumFirst: true);

    private static async Task<List<ApiTrack>> TrackRowsAsync(
        IQueryable<Track> query,
        TrackOrder order,
        int skip,
        int take,
        CancellationToken ct,
        bool albumFirst = false)
    {
        var shaped = query.Select(t => new
        {
            t.Id,
            t.Title,
            t.ArtistId,
            ArtistName = t.Artist!.Name,
            t.AlbumId,
            AlbumTitle = t.Album!.Title,
            Year = t.Album!.Year,
            t.TrackNumber,
            t.DiscNumber,
            t.DurationTicks,
            t.Bitrate,
        });

        shaped = (order, albumFirst) switch
        {
            (TrackOrder.AlbumOrder, true) => shaped
                .OrderBy(t => t.Year).ThenBy(t => t.AlbumTitle)
                .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            (TrackOrder.AlbumOrder, false) => shaped
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.Title),
            _ => shaped.OrderBy(t => t.Title).ThenBy(t => t.ArtistName),
        };

        var rows = await shaped.Skip(skip).Take(take).ToListAsync(ct);

        return
        [
            .. rows.Select(t => new ApiTrack(
                t.Id, t.Title, t.ArtistId, t.ArtistName, t.AlbumId, t.AlbumTitle, t.Year,
                t.TrackNumber, t.DiscNumber, ApiMap.Ms(t.DurationTicks), t.Bitrate,
                ApiMap.StreamUrl(t.Id), ApiMap.ArtUrl(t.AlbumId)))
        ];
    }

    /// <summary>
    /// Tracks by id, <b>in the order the ids were given</b>. A saved queue is a list of ids and
    /// has to come back as the same list rather than in whatever order SQL felt like; missing
    /// ids (a track deleted since the queue was saved) are dropped rather than left as holes.
    /// </summary>
    internal static async Task<List<ApiTrack>> TracksByIdAsync(
        MootifyDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var found = await TrackRowsAsync(
            db.Tracks.AsNoTracking().Where(t => t.IsPresent && ids.Contains(t.Id)),
            TrackOrder.TitleOrder,
            0,
            ids.Count,
            ct);

        var byId = found.ToDictionary(t => t.Id);
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    // ---- search and stats -------------------------------------------------

    internal static async Task<ApiSearchResults> SearchAsync(
        MootifyDbContext db, string q, int take, CancellationToken ct)
    {
        // Unlike a filter, a search with no usable term returns nothing rather than everything:
        // a voice search that misheard the user must not start playing the whole library.
        if (ApiSetup.Clean(q) is null) return new ApiSearchResults([], [], []);

        var artists = await ArtistShape(
                ArtistBase(db)
                    .Where(LibraryMatch.Artist(q))
                    .OrderBy(a => a.SortName)
                    .Take(take))
            .ToListAsync(ct);

        var albums = await AlbumRowsAsync(
            AlbumBase(db).Where(LibraryMatch.Album(q)), AlbumOrder.ArtistOrder, 0, take, ct);

        // Anywhere, not just the title: a voice search for "nevermind" has to end in the songs on
        // it, and matching track titles alone answered nothing. Same rule the website's search box
        // has always used.
        var tracks = await TrackRowsAsync(
            db.Tracks.AsNoTracking().Where(t => t.IsPresent).Where(LibraryMatch.TrackAnywhere(q)),
            TrackOrder.TitleOrder,
            0,
            take,
            ct);

        return new ApiSearchResults(artists, albums, tracks);
    }

    internal static async Task<ApiLibraryStats> StatsAsync(MootifyDbContext db, CancellationToken ct)
    {
        var present = db.Tracks.AsNoTracking().Where(t => t.IsPresent);

        var trackCount = await present.CountAsync(ct);

        // SumAsync over an empty sequence is 0 in SQL, but MaxAsync throws — the empty library
        // is the state every install starts in.
        var ticks = trackCount == 0 ? 0 : await present.SumAsync(t => t.DurationTicks, ct);
        var lastAdded = trackCount == 0 ? (DateTimeOffset?)null : await present.MaxAsync(t => t.AddedAt, ct);

        return new ApiLibraryStats(
            Artists: await db.Artists.CountAsync(a => a.Tracks.Any(t => t.IsPresent), ct),
            Albums: await db.Albums.CountAsync(a => a.Tracks.Any(t => t.IsPresent), ct),
            Tracks: trackCount,
            DurationMs: ApiMap.Ms(ticks),
            LastAddedAt: lastAdded);
    }
}
