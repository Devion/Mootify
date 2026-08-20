using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Mootify.Data;

namespace Mootify.Services.Library;

/// <summary>
/// What counts as a match, in one place.
///
/// There were two opinions about this and they disagreed: the library page filtered artist names
/// only (in memory, over every artist), the search page matched title/artist/album, and the API
/// matched track titles only. Somebody searching "nevermind" therefore got songs on the website,
/// nothing on the library page, and nothing in the car — three answers to one question.
///
/// <b>On speed.</b> Every pattern here is <c>%term%</c>, which no B-tree index can serve — SQLite
/// scans. That is a deliberate trade and it has a shape that keeps it cheap rather than an index
/// that would not work anyway:
///
/// - the scan is over a <i>projection</i> with a <c>LIMIT</c>, never over materialised entities;
/// - the joins it does are primary-key lookups, which is what makes 40,000 rows milliseconds
///   rather than seconds;
/// - callers debounce, so typing is one query rather than one per keystroke;
/// - the total is only counted when the page didn't already answer it (see
///   <see cref="LibrarySearchService.TotalAsync"/>).
///
/// The ceiling is real: somewhere in the low hundreds of thousands of tracks a scan per keystroke
/// stops being free, and the answer at that point is SQLite's FTS5 — a virtual table kept in step
/// by the scanner, queried with <c>MATCH</c>. That is a schema with triggers and raw SQL, which is
/// not worth carrying for a library this size. <b>If searching starts to feel slow, this is the
/// thing to build, and these predicates are what it replaces.</b>
/// </summary>
public static class LibraryMatch
{
    /// <summary>
    /// A search box's contents, or null when there's nothing in it worth searching for.
    ///
    /// Wildcards are dropped rather than escaped — no song title has a bare <c>%</c> somebody is
    /// looking for, and escaping needs a <c>LIKE … ESCAPE</c> the provider may not translate. That
    /// leaves the case where the term was <i>only</i> wildcards, which is why this returns null
    /// rather than a string: <c>%</c> has to mean "you typed nothing useful", not "match everything".
    /// </summary>
    public static string? Clean(string? query)
    {
        if (query is null) return null;

        var cleaned = query.Replace("%", "").Replace("_", "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Wraps a cleaned term for LIKE. SQLite's LIKE is case-insensitive for ASCII, which
    /// <c>string.Contains</c> is not — EF translates that one to <c>instr()</c>, and a
    /// case-sensitive library search finds nothing anybody typed.
    /// </summary>
    public static string LikePattern(string cleaned) => $"%{cleaned}%";

    /// <summary>An empty term matches everything, so a filter of nothing leaves the list whole.</summary>
    public static Expression<Func<Artist, bool>> Artist(string? query)
    {
        if (Clean(query) is not { } term) return _ => true;

        var pattern = LikePattern(term);
        return a => EF.Functions.Like(a.Name, pattern);
    }

    /// <summary>
    /// Albums match on their own title <i>and</i> on the artist's, so "nirvana" finds Nevermind
    /// rather than finding nothing.
    /// </summary>
    public static Expression<Func<Album, bool>> Album(string? query)
    {
        if (Clean(query) is not { } term) return _ => true;

        var pattern = LikePattern(term);
        return a => EF.Functions.Like(a.Title, pattern) || EF.Functions.Like(a.Artist!.Name, pattern);
    }

    /// <summary>
    /// Title only. This is <i>filtering</i> — narrowing a list that is already scoped to an album
    /// or an artist — where matching the album title would match every track on it the moment
    /// somebody typed the album's name.
    /// </summary>
    public static Expression<Func<Track, bool>> TrackTitle(string? query)
    {
        if (Clean(query) is not { } term) return _ => true;

        var pattern = LikePattern(term);
        return t => EF.Functions.Like(t.Title, pattern);
    }

    /// <summary>
    /// Title, artist or album. This is <i>searching</i> — one box, no scope — where "play
    /// nevermind" has to reach the songs on it and a track-title-only match reaches nothing.
    /// </summary>
    public static Expression<Func<Track, bool>> TrackAnywhere(string? query)
    {
        if (Clean(query) is not { } term) return _ => true;

        var pattern = LikePattern(term);
        return t => EF.Functions.Like(t.Title, pattern)
                 || EF.Functions.Like(t.Artist!.Name, pattern)
                 || EF.Functions.Like(t.Album!.Title, pattern);
    }
}

public sealed record ArtistHit(Guid Id, string Name, int AlbumCount, int TrackCount);

public sealed record AlbumHit(
    Guid Id, string Title, Guid ArtistId, string ArtistName, int? Year, int TrackCount);

public sealed record TrackHit(
    Guid Id, string Title, string ArtistName, Guid AlbumId, string AlbumTitle, long DurationTicks)
{
    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
}

/// <summary>One page of one kind of result, with the size of the whole answer.</summary>
public sealed record SearchPage<T>(List<T> Rows, int Total, int Skip, int Take)
{
    public static SearchPage<T> Empty => new([], 0, 0, 0);

    public bool HasPrevious => Skip > 0;
    public bool HasNext => Skip + Rows.Count < Total;

    public int PageNumber => Take <= 0 ? 1 : (Skip / Take) + 1;
    public int PageCount => Take <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)Take));
}

/// <summary>
/// Library search for the website: artists, albums and tracks, paged, done in the database.
///
/// The library page used to load every artist into the circuit and filter the list in C#. That is
/// fine at two hundred artists and is the wrong shape at two thousand — it costs a full table read
/// and a copy of the library per person looking at the page, before anybody has typed anything. So
/// nothing here returns more than a page, and nothing is filtered after it has been fetched.
///
/// The API has its own projections (<c>LibraryQueries</c>) because it has to produce a different
/// shape — stream and art URLs, milliseconds — but both go through <see cref="LibraryMatch"/>, so
/// the phone and the website agree about what the word "matches" means.
/// </summary>
public sealed class LibrarySearchService(IDbContextFactory<MootifyDbContext> dbFactory)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>
    /// Artists with nothing playable are dead rows left behind by a removed folder — the scanner
    /// keeps them so playlists don't break, and every list in the app hides them.
    /// </summary>
    public async Task<SearchPage<ArtistHit>> ArtistsAsync(
        string? query, int skip = 0, int take = DefaultPageSize, CancellationToken ct = default)
    {
        (skip, take) = Clamp(skip, take);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var matching = db.Artists
            .AsNoTracking()
            .Where(a => a.Tracks.Any(t => t.IsPresent))
            .Where(LibraryMatch.Artist(query));

        var rows = await matching
            .OrderBy(a => a.SortName)
            .Skip(skip)
            .Take(take)
            .Select(a => new ArtistHit(
                a.Id,
                a.Name,
                a.Albums.Count(al => al.Tracks.Any(t => t.IsPresent)),
                a.Tracks.Count(t => t.IsPresent)))
            .ToListAsync(ct);

        return new SearchPage<ArtistHit>(rows, await TotalAsync(matching, rows.Count, skip, take, ct), skip, take);
    }

    public async Task<SearchPage<AlbumHit>> AlbumsAsync(
        string? query, int skip = 0, int take = DefaultPageSize, CancellationToken ct = default)
    {
        (skip, take) = Clamp(skip, take);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var matching = db.Albums
            .AsNoTracking()
            .Where(a => a.Tracks.Any(t => t.IsPresent))
            .Where(LibraryMatch.Album(query));

        var rows = await matching
            .OrderBy(a => a.Artist!.Name).ThenBy(a => a.Year).ThenBy(a => a.Title)
            .Skip(skip)
            .Take(take)
            .Select(a => new AlbumHit(
                a.Id,
                a.Title,
                a.ArtistId,
                a.Artist!.Name,
                a.Year,
                a.Tracks.Count(t => t.IsPresent)))
            .ToListAsync(ct);

        return new SearchPage<AlbumHit>(rows, await TotalAsync(matching, rows.Count, skip, take, ct), skip, take);
    }

    /// <summary>
    /// Tracks matching the term anywhere — title, artist or album. Unlike the other two this
    /// refuses an empty term: a list of every song in the library is not an answer to anything,
    /// and building one is the query worth never running by accident.
    /// </summary>
    public async Task<SearchPage<TrackHit>> TracksAsync(
        string? query, int skip = 0, int take = DefaultPageSize, CancellationToken ct = default)
    {
        if (LibraryMatch.Clean(query) is null) return SearchPage<TrackHit>.Empty;

        (skip, take) = Clamp(skip, take);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var matching = db.Tracks
            .AsNoTracking()
            .Where(t => t.IsPresent)
            .Where(LibraryMatch.TrackAnywhere(query));

        var rows = await matching
            .OrderBy(t => t.Title).ThenBy(t => t.Artist!.Name)
            .Skip(skip)
            .Take(take)
            // DurationTicks, never Duration: the TimeSpan property is [NotMapped] and projecting
            // it fails to translate.
            .Select(t => new TrackHit(
                t.Id,
                t.Title,
                t.Artist!.Name,
                t.AlbumId,
                t.Album!.Title,
                t.DurationTicks))
            .ToListAsync(ct);

        return new SearchPage<TrackHit>(rows, await TotalAsync(matching, rows.Count, skip, take, ct), skip, take);
    }

    /// <summary>
    /// Every matching track id, in the order they were shown, for "play all of this".
    /// Bounded — a term like "the" matches thousands, and a queue that long is a mistake somebody
    /// made with one click rather than a feature.
    /// </summary>
    public async Task<List<Guid>> TrackIdsAsync(
        string? query, int limit = MaxPageSize, CancellationToken ct = default)
    {
        if (LibraryMatch.Clean(query) is null) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Tracks
            .AsNoTracking()
            .Where(t => t.IsPresent)
            .Where(LibraryMatch.TrackAnywhere(query))
            .OrderBy(t => t.Title).ThenBy(t => t.Artist!.Name)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    private static (int Skip, int Take) Clamp(int skip, int take) =>
        (Math.Max(0, skip), take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize));

    /// <summary>
    /// How many matched altogether — and, when the page already proves it, without asking.
    ///
    /// <c>COUNT</c> over a <c>LIKE '%…%'</c> is a second full scan with no early exit, so it is
    /// the more expensive half of a search that returns six rows. On the first page of a
    /// short result the answer is simply how many came back, which is the common case for
    /// anybody who typed something specific.
    /// </summary>
    private static async Task<int> TotalAsync<T>(
        IQueryable<T> matching, int rowCount, int skip, int take, CancellationToken ct) =>
        skip == 0 && rowCount < take ? rowCount : await matching.CountAsync(ct);
}
