using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mootify.Data;
using Mootify.Services.Playlists;

namespace Mootify.Services.Import;

public sealed record ImportMatch(ImportedTrack Source, Guid? TrackId);

/// <summary>
/// What appending an import to an existing playlist did. <c>Ok</c> is false only when the user
/// couldn't write there — zero added is a normal outcome when they already own every song.
/// </summary>
public sealed record ImportAppendResult(bool Ok, int Added, int AlreadyThere);

public sealed record ImportPreview(
    string PlaylistName,
    IReadOnlyList<ImportMatch> Matched,
    IReadOnlyList<ImportedTrack> Missing,
    int Skipped)
{
    public int Total => Matched.Count + Missing.Count;
}

/// <summary>
/// Turns an Exportify CSV into a playlist: everything already in the library goes straight
/// in, and what's missing is reported so it can be requested.
///
/// The matching is the whole job. Spotify titles carry decoration the files don't — "(2011
/// Remaster)", "- Radio Edit", a featured artist in the title — so an exact comparison finds
/// almost nothing on a real library. Normalising both sides and falling back progressively
/// is what turns a 5% hit rate into a useful one.
/// </summary>
public sealed class PlaylistImportService(
    IDbContextFactory<MootifyDbContext> dbFactory,
    PlaylistService playlists,
    ILogger<PlaylistImportService> log)
{
    /// <summary>Two files of the same song rarely differ by more than a second or two.</summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(5);

    public async Task<ImportPreview> PreviewAsync(
        string fileName, string csv, CancellationToken ct = default)
    {
        var parsed = ExportifyCsv.Parse(csv);
        var name = ExportifyCsv.PlaylistNameFrom(fileName);

        if (parsed.Count == 0)
        {
            return new ImportPreview(name, [], [], 0);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // One pass over the library rather than a query per row — 600 rows against a
        // round-trip each would take minutes.
        var library = await db.Tracks
            .AsNoTracking()
            .Where(t => t.IsPresent)
            .Select(t => new { t.Id, t.Title, ArtistName = t.Artist!.Name, t.DurationTicks })
            .ToListAsync(ct);

        var byTitleArtist = new Dictionary<string, Guid>();
        var byTitle = new Dictionary<string, List<(Guid Id, TimeSpan Duration)>>();

        foreach (var track in library)
        {
            var title = Normalize(track.Title);
            var key = $"{title}{Normalize(track.ArtistName)}";
            byTitleArtist.TryAdd(key, track.Id);

            if (!byTitle.TryGetValue(title, out var list))
            {
                byTitle[title] = list = [];
            }

            list.Add((track.Id, TimeSpan.FromTicks(track.DurationTicks)));
        }

        var matched = new List<ImportMatch>();
        var missing = new List<ImportedTrack>();
        var seen = new HashSet<Guid>();
        var skipped = 0;

        foreach (var row in parsed)
        {
            var id = Match(row, byTitleArtist, byTitle);

            if (id is null)
            {
                missing.Add(row);
                continue;
            }

            // The same song twice in a Spotify playlist shouldn't become two rows here.
            if (!seen.Add(id.Value))
            {
                skipped++;
                continue;
            }

            matched.Add(new ImportMatch(row, id));
        }

        log.LogInformation(
            "Import preview for {File}: {Matched} matched, {Missing} missing, {Skipped} duplicate",
            fileName, matched.Count, missing.Count, skipped);

        return new ImportPreview(name, matched, missing, skipped);
    }

    private static Guid? Match(
        ImportedTrack row,
        Dictionary<string, Guid> byTitleArtist,
        Dictionary<string, List<(Guid Id, TimeSpan Duration)>> byTitle)
    {
        // Best case: the title and the artist both normalise to the same thing.
        foreach (var artist in row.AllArtists)
        {
            var key = $"{Normalize(row.Title)}{Normalize(artist)}";
            if (byTitleArtist.TryGetValue(key, out var exact)) return exact;
        }

        // The library's artist may be the band while Spotify credits the collaboration, or
        // vice versa. Fall back to the title and let the duration decide, which is a far
        // stronger signal than a fuzzy name comparison.
        if (!byTitle.TryGetValue(Normalize(row.Title), out var candidates)) return null;

        if (candidates.Count == 1) return candidates[0].Id;
        if (row.Duration == TimeSpan.Zero) return null;

        var best = candidates
            .Select(c => (c.Id, Delta: (c.Duration - row.Duration).Duration()))
            .OrderBy(c => c.Delta)
            .First();

        return best.Delta <= DurationTolerance ? best.Id : null;
    }

    /// <summary>
    /// Strips the decoration that stops a Spotify title matching the file on disk:
    /// "Numb (2011 Remaster)" and "Numb - Remastered" both become "numb".
    /// </summary>
    internal static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var text = value.ToLowerInvariant();

        // Anything parenthesised or bracketed: remasters, live years, "feat. X", edition names.
        text = StripBetween(text, '(', ')');
        text = StripBetween(text, '[', ']');

        // " - Remastered 2011", " - Radio Edit", and friends.
        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) text = text[..dash];

        var featured = text.IndexOf(" feat.", StringComparison.Ordinal);
        if (featured > 0) text = text[..featured];

        var builder = new StringBuilder(text.Length);

        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            // Drop accents so "Beyoncé" and "Beyonce" are the same song.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(c)) builder.Append(c);
            else if (char.IsWhiteSpace(c) && builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }

        return builder.ToString().Trim();
    }

    private static string StripBetween(string text, char open, char close)
    {
        var builder = new StringBuilder(text.Length);
        var depth = 0;

        foreach (var c in text)
        {
            if (c == open) depth++;
            else if (c == close) { if (depth > 0) depth--; }
            else if (depth == 0) builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Creates the playlist and adds everything that matched. Returns the new playlist, or
    /// null when the user couldn't write where they asked.
    /// </summary>
    public async Task<Guid?> CreateAsync(
        Guid userId, ImportPreview preview, Guid? teamId, CancellationToken ct = default)
    {
        var playlistId = await playlists.CreateAsync(userId, preview.PlaylistName, teamId, ct);
        if (playlistId is null) return null;

        var trackIds = preview.Matched.Select(m => m.TrackId!.Value).ToList();
        if (trackIds.Count > 0)
        {
            await playlists.AddTracksAsync(playlistId.Value, userId, trackIds, ct: ct);
        }

        return playlistId;
    }

    /// <summary>
    /// Adds everything that matched to a playlist that already exists — the user's own or one
    /// belonging to a team they're in; <see cref="PlaylistService.CanEditAsync"/> decides which.
    ///
    /// Songs already in the target are skipped. Adding the same song twice by hand is allowed
    /// deliberately, but an import is a bulk action nobody reviews row by row: re-importing an
    /// export you've already merged, or one that overlaps a list you keep, would otherwise
    /// double every song in it.
    /// </summary>
    public async Task<ImportAppendResult> AppendAsync(
        Guid userId, ImportPreview preview, Guid playlistId, CancellationToken ct = default)
    {
        if (!await playlists.CanEditAsync(playlistId, userId, ct))
        {
            log.LogWarning("{User} was refused an import append into playlist {Playlist}", userId, playlistId);
            return new ImportAppendResult(false, 0, 0);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var already = (await db.PlaylistItems
                .Where(i => i.PlaylistId == playlistId)
                .Select(i => i.TrackId)
                .ToListAsync(ct))
            .ToHashSet();

        var trackIds = preview.Matched
            .Select(m => m.TrackId!.Value)
            .Where(id => !already.Contains(id))
            .ToList();

        var added = trackIds.Count == 0
            ? 0
            : await playlists.AddTracksAsync(playlistId, userId, trackIds, ct: ct);

        log.LogInformation(
            "Import appended {Added} track(s) to playlist {Playlist}, {Skipped} already there",
            added, playlistId, preview.Matched.Count - trackIds.Count);

        return new ImportAppendResult(true, added, preview.Matched.Count - trackIds.Count);
    }
}
