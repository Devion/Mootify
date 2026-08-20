using Mootify.Services.Library;

namespace Mootify.Tests;

/// <summary>
/// Almost every case here is a name that must be left alone. Stripping too eagerly is the
/// expensive mistake: "3 Doors Down" filed under "Doors Down" is an artist nobody finds again,
/// and nothing in the app will ever notice it happened.
/// </summary>
public sealed class LibraryNamingTests
{
    [Theory]
    // The reason this exists: compilations ripped with the position in the artist field.
    [InlineData("09. Elton John", "Elton John")]
    [InlineData("12. Shocking Blue", "Shocking Blue")]
    [InlineData("12 - Shocking Blue", "Shocking Blue")]
    [InlineData("07_Foo Fighters", "Foo Fighters")]
    [InlineData("3. Blondie", "Blondie")]
    [InlineData("01) Blondie", "Blondie")]
    // Zero-padding is what makes a bare space safe to treat as a separator.
    [InlineData("09 Elton John", "Elton John")]
    // Two taggers have each had a go. Left alone: catching it costs the guard that keeps
    // "10.000 Maniacs" whole, and that is the worse trade.
    [InlineData("01 - 09. Elton John", "01 - 09. Elton John")]
    // Numbers that are part of the name.
    [InlineData("3 Doors Down", "3 Doors Down")]
    [InlineData("10 Years", "10 Years")]
    [InlineData("30 Seconds to Mars", "30 Seconds to Mars")]
    [InlineData("98 Degrees", "98 Degrees")]
    [InlineData("112", "112")]
    [InlineData("1979", "1979")]
    // A digit after the separator means the number was never an index.
    [InlineData("10.000 Maniacs", "10.000 Maniacs")]
    [InlineData("5.1 Surround", "5.1 Surround")]
    // A hyphen with no space is how names are spelled, not how tracks are numbered.
    [InlineData("5-Star", "5-Star")]
    [InlineData("3-11 Porter", "3-11 Porter")]
    // Nothing left to keep, so keep what there was.
    [InlineData("09.", "09.")]
    [InlineData("", "")]
    public void Index_prefixes_go_and_nothing_else_does(string input, string expected) =>
        Assert.Equal(expected, LibraryNaming.StripIndexPrefix(input));

    [Theory]
    [InlineData("The Black Eyed Peas", "Black Eyed Peas")]
    [InlineData("BLACK EYED PEAS", "Black Eyed Peas")]
    [InlineData("09. Black Eyed Peas", "the black eyed peas")]
    [InlineData("Beyoncé", "Beyonce")]
    [InlineData("Guns N' Roses", "Guns N Roses")]
    public void Two_spellings_of_one_artist_share_a_key(string a, string b) =>
        Assert.Equal(LibraryNaming.ArtistKey(a), LibraryNaming.ArtistKey(b));

    [Theory]
    [InlineData("Blondie", "Blur")]
    [InlineData("The Cure", "The Doors")]
    // Only "The" is folded. "A Perfect Circle" is a name, not an article and a name.
    [InlineData("A Perfect Circle", "Perfect Circle")]
    public void Different_artists_do_not(string a, string b) =>
        Assert.NotEqual(LibraryNaming.ArtistKey(a), LibraryNaming.ArtistKey(b));

    [Fact]
    public void A_name_made_only_of_punctuation_still_gets_its_own_key()
    {
        // "!!!" is a band, and Normalize keeps letters and digits — so every such name would
        // otherwise normalise to nothing and they would all merge into one artist.
        Assert.NotEqual(LibraryNaming.ArtistKey("!!!"), LibraryNaming.ArtistKey("???"));
        Assert.Equal(LibraryNaming.ArtistKey("!!!"), LibraryNaming.ArtistKey("!!!"));
    }

    [Theory]
    [InlineData("The Cowbells", "Cowbells")]
    [InlineData("Cowbells", "Cowbells")]
    [InlineData("The", "The")]
    public void Sorting_ignores_a_leading_article(string name, string expected) =>
        Assert.Equal(expected, LibraryNaming.SortName(name));
}
