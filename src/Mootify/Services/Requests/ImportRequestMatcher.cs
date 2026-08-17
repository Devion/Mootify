using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Import;

namespace Mootify.Services.Requests;

public sealed record ImportMatchSummary(int Requests, int Tracks)
{
    public static readonly ImportMatchSummary Nothing = new(0, 0);
}

/// <summary>
/// Links music that arrived by hand to the requests that asked for it.
///
/// Lidarr is one way a request gets satisfied, not the only one: when it can't find something
/// — which is what most of the <see cref="RequestStatus.NotFound"/> rows are — somebody
/// tends to go and get the file themselves and drop it in the import folder. Without this it
/// lands in the library as an unrelated track and the request sits there saying "nothing
/// found after a week of searching" next to music that is right there.
///
/// **A match must be seeded by a file that just arrived.** Every candidate is a track under a
/// folder something was just filed into, which for an existing artist folder means the whole
/// artist — so an album request is only ever completed off the back of a newly imported track,
/// never off tracks that were sitting there all along.
/// </summary>
public sealed class ImportRequestMatcher(
    IDbContextFactory<MootifyDbContext> dbFactory,
    RequestFulfiller fulfiller,
    ILogger<ImportRequestMatcher> log)
{
    public async Task<ImportMatchSummary> MatchAsync(IReadOnlyList<string> importedPaths, CancellationToken ct = default)
    {
        if (importedPaths.Count == 0) return ImportMatchSummary.Nothing;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Anything not already delivered. NotFound and Failed are included deliberately: those
        // are exactly the rows somebody gives up on Lidarr for and fetches by hand, so they are
        // the likeliest thing a dropped file is answering.
        var open = await db.Requests
            .Where(r => r.Status != RequestStatus.Available)
            .ToListAsync(ct);

        if (open.Count == 0) return ImportMatchSummary.Nothing;

        var candidates = await LoadCandidatesAsync(db, importedPaths, ct);
        if (candidates.Count == 0) return ImportMatchSummary.Nothing;

        var arrived = importedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = candidates.Where(c => arrived.Contains(c.Path)).ToList();

        if (imported.Count == 0)
        {
            // The files moved but the scan hasn't caught up with them, which happens when one
            // was still locked. The next pass sees them.
            log.LogDebug("None of the {Count} filed path(s) are indexed yet", importedPaths.Count);
            return ImportMatchSummary.Nothing;
        }

        int matched = 0, linked = 0;

        foreach (var request in open)
        {
            ct.ThrowIfCancellationRequested();

            var trackIds = Match(request, imported, candidates);
            if (trackIds.Count == 0) continue;

            try
            {
                await fulfiller.CompleteAsync(db, request, trackIds, ct);

                log.LogInformation(
                    "Request {RequestId} ({What}) was satisfied by {Count} imported track(s)",
                    request.Id, Describe(request), trackIds.Count);

                matched++;
                linked += trackIds.Count;
            }
            catch (Exception ex)
            {
                // One request that won't complete must not cost the rest of the batch.
                log.LogError(ex, "Could not complete request {RequestId} from the import folder", request.Id);
            }
        }

        return new ImportMatchSummary(matched, linked);
    }

    private sealed record Candidate(
        Guid Id, string Path, string Title, string ArtistName, string AlbumTitle, Guid AlbumId,
        string? RecordingMusicBrainzId, string? AlbumMusicBrainzId);

    private static async Task<List<Candidate>> LoadCandidatesAsync(
        MootifyDbContext db, IReadOnlyList<string> paths, CancellationToken ct)
    {
        // By folder rather than by exact path: StartsWith translates to LIKE, which SQLite
        // matches case-insensitively for ASCII, and a path assembled from the configured music
        // root won't always agree on case with the one the scanner enumerated. The exact-path
        // filter that decides what is *new* then happens in memory, where the comparison is ours.
        var folders = paths
            .Select(System.IO.Path.GetDirectoryName)
            .OfType<string>()
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (folders.Count == 0) return [];

        return await db.Tracks
            .AsNoTracking()
            .Where(t => t.IsPresent && folders.Any(f => t.Path.StartsWith(f)))
            .Select(t => new Candidate(
                t.Id,
                t.Path,
                t.Title,
                t.Artist!.Name,
                t.Album!.Title,
                t.AlbumId,
                t.RecordingMusicBrainzId,
                t.Album!.MusicBrainzId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// In order of confidence: the MusicBrainz id the request was made against, then the
    /// normalised title and artist.
    ///
    /// The normaliser is <see cref="PlaylistImportService.Normalize"/>, the same one the CSV
    /// import matches on, because the problem is identical — a request stores the title as
    /// MusicBrainz spells it and a file on disk carries whatever the tagger felt like, so
    /// "Numb (2011 Remaster)" has to reach "numb" from both ends. Two different normalisers
    /// would be two different opinions about what counts as the same song.
    /// </summary>
    private static List<Guid> Match(Request request, List<Candidate> imported, List<Candidate> all) =>
        request.Kind == RequestKind.Track
            ? MatchTrack(request, imported)
            : MatchAlbum(request, imported, all);

    private static List<Guid> MatchTrack(Request request, List<Candidate> imported)
    {
        if (request.RecordingMusicBrainzId is { Length: > 0 } mbid &&
            imported.FirstOrDefault(c => c.RecordingMusicBrainzId == mbid) is { } exact)
        {
            return [exact.Id];
        }

        var title = PlaylistImportService.Normalize(request.TrackTitle ?? "");
        if (title.Length == 0) return [];

        var artist = PlaylistImportService.Normalize(request.ArtistName);
        var byTitle = imported.Where(c => PlaylistImportService.Normalize(c.Title) == title).ToList();

        if (byTitle.Count == 0) return [];

        // The artist has to agree. A title on its own matches covers, and quietly closing
        // somebody's request with the wrong recording is worse than leaving it open.
        var byArtist = byTitle.FirstOrDefault(c => PlaylistImportService.Normalize(c.ArtistName) == artist);

        return byArtist is not null ? [byArtist.Id] : [];
    }

    private static List<Guid> MatchAlbum(Request request, List<Candidate> imported, List<Candidate> all)
    {
        var seed = Seed(request, imported);
        if (seed is null) return [];

        // Everything else that filed alongside it. Dropping half an album still completes the
        // request — the music arrived, and the half that didn't is a gap in the library rather
        // than an unanswered request.
        return [.. all.Where(c => c.AlbumId == seed.AlbumId).Select(c => c.Id)];
    }

    private static Candidate? Seed(Request request, List<Candidate> imported)
    {
        if (request.AlbumMusicBrainzId is { Length: > 0 } mbid &&
            imported.FirstOrDefault(c => c.AlbumMusicBrainzId == mbid) is { } exact)
        {
            return exact;
        }

        var album = PlaylistImportService.Normalize(request.AlbumTitle ?? "");
        if (album.Length == 0) return null;

        var artist = PlaylistImportService.Normalize(request.ArtistName);

        return imported.FirstOrDefault(c =>
            PlaylistImportService.Normalize(c.AlbumTitle) == album &&
            PlaylistImportService.Normalize(c.ArtistName) == artist);
    }

    private static string Describe(Request request) =>
        request.Kind == RequestKind.Track
            ? $"{request.TrackTitle} by {request.ArtistName}"
            : $"{request.AlbumTitle} by {request.ArtistName}";
}
