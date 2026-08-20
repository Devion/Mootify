using Mootify.Services.Import;

namespace Mootify.Services.Library;

/// <summary>
/// What a name in the library is allowed to look like, and when two of them are the same name.
///
/// Both halves exist because tags are written by whoever ripped the file, and a shelf full of
/// "09. Elton John" and "12. Shocking Blue" is not a library — it is one artist per track, sorted
/// by the position that track happened to hold in somebody's compilation. The scanner reads the
/// artist out of the tag and nowhere else (see <see cref="LibraryScanner.ReadArtist"/>), so a
/// numbered artist is a numbered tag, not a folder name leaking in.
///
/// <see cref="LibraryOrganizer"/> repairs what is already stored; the scanner applies the same
/// rules to everything that arrives after, so the repair holds.
/// </summary>
public static class LibraryNaming
{
    /// <summary>
    /// Separators that a track index can be followed by. A bare space counts only when the
    /// number is zero-padded — see <see cref="StripIndexPrefix"/>.
    /// </summary>
    private const string Separators = ".-_)]#";

    /// <summary>Punctuation strong enough to mean "index" with no space after it.</summary>
    private const string StrongSeparators = "._";

    /// <summary>
    /// Drops a leading track number: "09. Elton John" is Elton John, "12 - Shocking Blue" is
    /// Shocking Blue.
    ///
    /// <b>Almost everything here is a rule about what <i>not</i> to strip</b>, because the
    /// failure is silent and permanent: "3 Doors Down", "10 Years", "30 Seconds to Mars" and
    /// "98 Degrees" all start with a number that is part of the name, and an over-eager strip
    /// files them under "Doors Down" and "Years" where nobody will look for them again. So:
    ///
    /// <list type="bullet">
    /// <item>at most three digits — a longer run is "1979" or "112", not an index;</item>
    /// <item>a separator is required, and a plain space only counts when the number is
    /// zero-padded ("09 Elton John" yes, "10 Years" no) — nobody names a band "09 Anything";</item>
    /// <item>whitespace has to appear somewhere in the separator unless it is a dot or an
    /// underscore, which keeps "5-Star" and "3-11 Porter" intact while still catching
    /// "07_Foo";</item>
    /// <item>what is left has to start with a letter, which is what saves "10.000 Maniacs"
    /// from becoming "000 Maniacs" and "5.1 Surround" from becoming "1 Surround".</item>
    /// </list>
    ///
    /// A name that is nothing but an index ("09.") is left exactly as it was: there is no
    /// artist in it to recover, and an empty name is worse than a silly one.
    /// </summary>
    /// <remarks>
    /// One pass, deliberately. "01 - 09. Elton John" — two taggers each having a go — is left
    /// alone, because the only way to catch it is to let the strip recurse past a remainder that
    /// starts with a digit, and that is the guard keeping "10.000 Maniacs" out of the bin.
    /// A doubled prefix is rare; "000 Maniacs" is forever.
    /// </remarks>
    public static string StripIndexPrefix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";

        var text = name.Trim();
        return StripOnce(text) ?? text;
    }

    private static string? StripOnce(string text)
    {
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;

        if (digits is 0 or > 3) return null;

        var padded = text[0] == '0';
        var i = digits;
        var space = false;
        var strongOnly = true;
        var punctuation = 0;

        while (i < text.Length && text[i] == ' ') { space = true; i++; }

        while (i < text.Length && Separators.Contains(text[i]))
        {
            if (!StrongSeparators.Contains(text[i])) strongOnly = false;
            punctuation++;
            i++;
        }

        while (i < text.Length && text[i] == ' ') { space = true; i++; }

        // No punctuation at all: only a zero-padded number followed by a space is an index.
        if (punctuation == 0 && !(padded && space)) return null;

        // Punctuation with no whitespace anywhere is a hyphenated name ("5-Star") unless the
        // punctuation is the sort a tagger uses and a band doesn't.
        if (punctuation > 0 && !space && !strongOnly) return null;

        var rest = text[i..];

        // A digit here means the number was part of the name all along.
        return rest.Length > 0 && char.IsLetter(rest[0]) ? rest : null;
    }

    /// <summary>
    /// The key two artist names have to share to be the same artist.
    ///
    /// Reuses <see cref="PlaylistImportService.Normalize"/> — case, accents and punctuation —
    /// because a second opinion about what counts as the same name is how a library ends up
    /// split down the middle, and then drops a leading "The". "The Black Eyed Peas" and
    /// "Black Eyed Peas" are one band with two taggers, and the library view shows them as two
    /// rows a long way apart.
    ///
    /// Only "The": "A Perfect Circle" and "An Horse" are names, and there is no widespread
    /// habit of dropping those the way there is with "The".
    /// </summary>
    public static string ArtistKey(string? name)
    {
        var cleaned = StripIndexPrefix(name);
        var key = PlaylistImportService.Normalize(cleaned);

        if (key.StartsWith("the ", StringComparison.Ordinal) && key.Length > 4)
        {
            key = key[4..];
        }

        // "!!!" is a band. Normalize keeps letters and digits, so a name made only of
        // punctuation normalises to nothing, and every such artist would share one key.
        return key.Length > 0 ? key : cleaned.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// What the library sorts by. "The Cowbells" belongs under C — the article is how the band
    /// writes its name, not where anybody looks for it.
    /// </summary>
    public static string SortName(string name) =>
        name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && name.Length > 4
            ? name[4..]
            : name;
}
