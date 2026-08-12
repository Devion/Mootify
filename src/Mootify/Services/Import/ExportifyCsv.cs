using System.Text;

namespace Mootify.Services.Import;

public sealed record ImportedTrack(
    string Title,
    string Album,
    string Artist,
    TimeSpan Duration)
{
    /// <summary>All artists as the export listed them, for display when a match fails.</summary>
    public string[] AllArtists { get; init; } = [];
}

/// <summary>
/// Reads the CSV Exportify produces from a Spotify playlist.
///
/// Hand-rolled rather than a CSV library because the format is small and fixed, and because
/// the two things that actually break naive splitting — commas inside quotes and doubled
/// quotes — are four lines to handle. "Artist Name(s)" in particular is a comma-separated
/// list <i>inside</i> a quoted field, so splitting the line on commas mangles every
/// collaboration in the file.
/// </summary>
public static class ExportifyCsv
{
    public static IReadOnlyList<ImportedTrack> Parse(string content)
    {
        var rows = ParseRows(content);
        if (rows.Count == 0) return [];

        var header = rows[0];
        var title = IndexOf(header, "Track Name");
        var album = IndexOf(header, "Album Name");
        var artist = IndexOf(header, "Artist Name(s)");
        var duration = IndexOf(header, "Duration (ms)");

        // Not an Exportify file. Better to return nothing than to import 600 rows of noise.
        if (title < 0 || artist < 0) return [];

        var tracks = new List<ImportedTrack>();

        foreach (var row in rows.Skip(1))
        {
            var name = Field(row, title);
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Local files and podcast episodes come through with no artist; they can never
            // be matched or requested, so drop them rather than reporting them as missing.
            var artists = SplitArtists(Field(row, artist));
            if (artists.Length == 0) continue;

            tracks.Add(new ImportedTrack(
                name.Trim(),
                Field(row, album).Trim(),
                artists[0],
                ParseDuration(Field(row, duration)))
            {
                AllArtists = artists,
            });
        }

        return tracks;
    }

    /// <summary>The playlist name: "VAA_Team_Alpha.csv" becomes "VAA Team Alpha".</summary>
    public static string PlaylistNameFrom(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "").Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(stem) ? "Imported playlist" : stem;
    }

    private static string[] SplitArtists(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static TimeSpan ParseDuration(string value) =>
        long.TryParse(value, out var ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    private static string Field(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : "";

    private static int IndexOf(IReadOnlyList<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    /// <summary>
    /// RFC 4180-ish: quoted fields may contain commas and newlines, and a doubled quote
    /// inside a quoted field is a literal quote.
    /// </summary>
    internal static List<List<string>> ParseRows(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    row.Add(field.ToString());
                    field.Clear();

                    if (row.Any(f => !string.IsNullOrWhiteSpace(f))) rows.Add(row);
                    row = [];
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        // Last line without a trailing newline.
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            if (row.Any(f => !string.IsNullOrWhiteSpace(f))) rows.Add(row);
        }

        return rows;
    }
}
