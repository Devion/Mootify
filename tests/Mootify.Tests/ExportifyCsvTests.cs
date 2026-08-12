using Mootify.Services.Import;

namespace Mootify.Tests;

/// <summary>
/// Reading the CSV Exportify writes. The header and a couple of rows here are copied
/// verbatim from a real export.
/// </summary>
public sealed class ExportifyCsvTests
{
    private const string Header =
        "Track URI,Track Name,Album Name,Artist Name(s),Release Date,Duration (ms),Popularity,Explicit,Added By,Added At,Genres,Record Label";

    [Fact]
    public void A_real_export_row_parses()
    {
        var csv = Header + "\n" +
            "spotify:track:2nLtzopw4rPReszdYBJU6h,\"Numb\",\"Meteora\",\"Linkin Park\",2003-03-25,187520,91,false,user,2025-09-24T10:02:00Z,\"nu metal,rock\",\"Warner Records\"\n";

        var track = Assert.Single(ExportifyCsv.Parse(csv));

        Assert.Equal("Numb", track.Title);
        Assert.Equal("Meteora", track.Album);
        Assert.Equal("Linkin Park", track.Artist);
        Assert.Equal(TimeSpan.FromMilliseconds(187520), track.Duration);
    }

    [Fact]
    public void Commas_inside_quoted_fields_do_not_shift_the_columns()
    {
        // The reason this isn't a string.Split: both the artist list and the genre list are
        // comma-separated *inside* quotes, and splitting the line mangles every collaboration.
        var csv = Header + "\n" +
            "uri,\"Song\",\"Album\",\"Artist A,Artist B\",2020,200000,50,false,user,2025-01-01T00:00:00Z,\"pop,rock\",\"Label\"\n";

        var track = Assert.Single(ExportifyCsv.Parse(csv));

        Assert.Equal("Song", track.Title);
        Assert.Equal("Album", track.Album);
        Assert.Equal(["Artist A", "Artist B"], track.AllArtists);
        Assert.Equal("Artist A", track.Artist);
    }

    [Fact]
    public void A_doubled_quote_is_a_literal_quote()
    {
        var csv = Header + "\n" +
            "uri,\"The \"\"Real\"\" Thing\",\"Album\",\"Artist\",2020,200000,50,false,user,2025-01-01T00:00:00Z,\"pop\",\"Label\"\n";

        Assert.Equal("The \"Real\" Thing", Assert.Single(ExportifyCsv.Parse(csv)).Title);
    }

    [Fact]
    public void Rows_with_no_artist_are_dropped()
    {
        // Local files and podcast episodes export like this. They can never be matched or
        // requested, so counting them as "missing" would just be noise.
        var csv = Header + "\n" +
            "uri,\"Ghost\",\"\",\"\",,,,,,,,\n" +
            "uri,\"Real Song\",\"Album\",\"Artist\",2020,200000,50,false,user,2025-01-01T00:00:00Z,\"pop\",\"Label\"\n";

        Assert.Equal("Real Song", Assert.Single(ExportifyCsv.Parse(csv)).Title);
    }

    [Fact]
    public void A_file_that_is_not_an_export_yields_nothing()
    {
        // Better to import nothing than 600 rows of somebody's spreadsheet.
        Assert.Empty(ExportifyCsv.Parse("name,email\nbob,bob@example.com\n"));
        Assert.Empty(ExportifyCsv.Parse(""));
    }

    [Fact]
    public void The_last_row_survives_a_missing_trailing_newline()
    {
        var csv = Header + "\n" +
            "uri,\"Song\",\"Album\",\"Artist\",2020,200000,50,false,user,2025-01-01T00:00:00Z,\"pop\",\"Label\"";

        Assert.Single(ExportifyCsv.Parse(csv));
    }

    [Theory]
    [InlineData("VAA_Team_Alpha.csv", "VAA Team Alpha")]
    [InlineData("Liked_Songs.csv", "Liked Songs")]
    [InlineData("Everything.csv", "Everything")]
    [InlineData("", "Imported playlist")]
    public void The_file_name_becomes_the_playlist_name(string fileName, string expected)
    {
        Assert.Equal(expected, ExportifyCsv.PlaylistNameFrom(fileName));
    }
}

/// <summary>
/// Matching an export against the library. Spotify titles carry decoration the files don't,
/// and an exact comparison finds almost nothing on a real library.
/// </summary>
public sealed class ImportMatchingTests
{
    [Theory]
    [InlineData("Numb", "numb")]
    [InlineData("Numb (2011 Remaster)", "numb")]
    [InlineData("Numb - Remastered 2011", "numb")]
    [InlineData("Numb [Live]", "numb")]
    [InlineData("Numb feat. Jay-Z", "numb")]
    [InlineData("In the End", "in the end")]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("AC/DC", "acdc")]
    [InlineData("  Spaced   Out  ", "spaced out")]
    public void Decoration_is_stripped_so_the_same_song_matches(string input, string expected)
    {
        Assert.Equal(expected, PlaylistImportService.Normalize(input));
    }

    [Fact]
    public void Different_songs_still_normalise_differently()
    {
        // The stripping must not be so aggressive that unrelated songs collide.
        Assert.NotEqual(
            PlaylistImportService.Normalize("Numb"),
            PlaylistImportService.Normalize("Numb Encore"));

        Assert.NotEqual(
            PlaylistImportService.Normalize("One Step Closer"),
            PlaylistImportService.Normalize("One More Time"));
    }

    [Fact]
    public void An_empty_title_normalises_to_nothing_rather_than_throwing()
    {
        Assert.Equal("", PlaylistImportService.Normalize(""));
        Assert.Equal("", PlaylistImportService.Normalize("   "));
        Assert.Equal("", PlaylistImportService.Normalize("(Remastered)"));
    }
}
