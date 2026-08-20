using Mootify.Services.Ideas;

namespace Mootify.Tests;

/// <summary>
/// The Ideabox's input rule, which is the security-relevant half of that feature.
///
/// Escaping is not what these tests are about — Razor escapes, and nothing renders an idea as
/// markup. These are about the characters that are still dangerous <i>after</i> correct escaping:
/// a right-to-left override reverses everything after it, a zero-width joiner is invisible in
/// every list it appears in, and a control character means something to a terminal reading a log
/// line. A correctly-escaped U+202E is still a U+202E.
///
/// Every awkward character below is built from its code point rather than pasted in — see the
/// comment on the theory for why.
/// </summary>
public sealed class IdeaTextTests
{
    private static string Accept(string? raw)
    {
        var result = IdeaText.Validate(raw);
        Assert.True(result.Ok, result.Error);
        return result.Text!;
    }

    private static string Reject(string? raw)
    {
        var result = IdeaText.Validate(raw);
        Assert.False(result.Ok, $"expected a refusal, got {result.Text}");
        return result.Error!;
    }

    // ---- what gets through -----------------------------------------------

    [Fact]
    public void Ordinary_text_is_kept_exactly()
    {
        Assert.Equal(
            "Shuffle should remember where it was.",
            Accept("Shuffle should remember where it was."));
    }

    [Fact]
    public void Punctuation_and_symbols_that_are_ascii_are_fine()
    {
        // Deliberately the characters somebody would expect a "sanitiser" to eat. They are
        // printable ASCII, so they are stored as typed and escaped at render time like anything
        // else — stripping them would mangle half the messages anybody actually writes.
        const string message = "Add <b>bold</b> & \"quotes\" -- 100% of the time (n=1) {a:b} #tag";
        Assert.Equal(message, Accept(message));
    }

    [Fact]
    public void Paragraphs_survive()
    {
        Assert.Equal("One.\n\nTwo.", Accept("One.\n\nTwo."));
    }

    // ---- normalisation ----------------------------------------------------

    [Fact]
    public void Windows_line_endings_become_one_newline_each()
    {
        // Otherwise a message pasted from Notepad counts double against the length limit and
        // stores a different string than the same message typed anywhere else.
        Assert.Equal("One\nTwo", Accept("One\r\nTwo"));
        Assert.Equal("One\nTwo", Accept("One\rTwo"));
    }

    [Fact]
    public void Tabs_become_spaces_rather_than_a_refusal()
    {
        Assert.Equal("a b", Accept("a\tb"));
    }

    [Fact]
    public void Trailing_whitespace_and_stray_blank_lines_are_trimmed()
    {
        Assert.Equal("a\nb", Accept("  \n\na   \nb   \n\n  "));
    }

    [Fact]
    public void A_run_of_blank_lines_is_capped()
    {
        Assert.Equal("a\n\nb", Accept("a\n\n\n\n\n\nb"));
    }

    // ---- what gets refused ------------------------------------------------

    [Theory]
    [InlineData(0x202E, "right-to-left override: reverses everything after it")]
    [InlineData(0x200B, "zero-width space: invisible in every list it appears in")]
    [InlineData(0x200D, "zero-width joiner")]
    [InlineData(0x0000, "NUL")]
    [InlineData(0x001B, "ESC: the start of a terminal escape sequence")]
    [InlineData(0x00A0, "non-breaking space: looks like a space, is not one")]
    [InlineData(0x0430, "Cyrillic a: a homoglyph for the Latin one")]
    [InlineData(0x1F600, "an emoji")]
    public void Anything_outside_printable_ascii_is_refused(int codePoint, string what)
    {
        // Code points rather than pasted characters. Half of these are invisible and one is a
        // NUL, so a source file containing them literally is a file no diff, grep or review can
        // show honestly — which is the same argument the feature itself makes.
        var smuggled = char.ConvertFromUtf32(codePoint);

        var error = Reject($"please add{smuggled} more cowbell");

        // Reported as a code point and never echoed back. Putting the offending character into
        // the error message is how a sanitiser becomes the thing it was guarding against.
        //
        // Ordinal, deliberately: xunit's default comparison is culture-sensitive, and a culture
        // comparison treats zero-width characters as ignorable — so it finds one in every string,
        // including this one, at position 0. That is exactly the class of bug the rule exists for.
        Assert.True(error.Contains("U+"), $"{what}: {error}");
        Assert.DoesNotContain(smuggled, error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_accented_word_is_refused_with_the_position_to_fix()
    {
        // "cafe" with an e-acute is four characters and the fourth is the one to delete, so that
        // is the number somebody can count to on their own screen.
        var error = Reject("caf" + char.ConvertFromUtf32(0x00E9) + " mode");
        Assert.Contains("Character 4", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\n")]
    public void Nothing_is_not_an_idea(string? raw) => Reject(raw);

    [Fact]
    public void Too_long_is_refused_and_says_how_long_it_was()
    {
        var error = Reject(new string('a', IdeaText.MaxLength + 1));

        Assert.Contains((IdeaText.MaxLength + 1).ToString(), error);
        Assert.Contains(IdeaText.MaxLength.ToString(), error);
    }

    [Fact]
    public void Exactly_the_limit_is_allowed()
    {
        Assert.Equal(IdeaText.MaxLength, Accept(new string('a', IdeaText.MaxLength)).Length);
    }

    [Fact]
    public void A_megabyte_of_pasted_text_is_refused_without_being_normalised_first()
    {
        // The pre-check exists so a huge paste costs a length comparison rather than a
        // character-by-character pass that ends in the same refusal anyway.
        Reject(new string('a', 1_000_000));
    }

    [Fact]
    public void The_length_limit_is_measured_after_normalising()
    {
        // 100 "ab" joined by CRLF is 398 characters raw and 299 once the CRs come out. A check
        // that ran before normalising would refuse messages that fit.
        var raw = string.Join("\r\n", Enumerable.Repeat("ab", 100));
        Assert.Equal(299, Accept(raw).Length);
    }
}
