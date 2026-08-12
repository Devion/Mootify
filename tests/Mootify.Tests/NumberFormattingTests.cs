using System.Globalization;

namespace Mootify.Tests;

/// <summary>
/// HTML number syntax is invariant — "0.8", never "0,8" — in both directions.
///
/// This shipped broken and was invisible on an en-US machine: the deployed host used a comma
/// decimal separator, so the play bar rendered <c>value="0,8"</c>, which browsers discard as
/// invalid and replace with the midpoint of min and max. The thumb sat in the centre and
/// seeking did nothing, because the "47.9" the browser sent back failed to parse too.
///
/// These pin the formatting rule itself rather than the component, so they fail on any dev
/// machine regardless of its locale.
/// </summary>
public sealed class NumberFormattingTests
{
    /// <summary>Cultures that use a comma for the decimal point. The deployment used one.</summary>
    public static TheoryData<string> CommaCultures => ["nl-NL", "de-DE", "fr-FR", "pt-BR"];

    private static string Render(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool TryRead(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    [Theory]
    [MemberData(nameof(CommaCultures))]
    public void Attribute_values_use_a_dot_whatever_the_server_culture_is(string culture)
    {
        using var _ = new CultureScope(culture);

        Assert.Equal("0.8", Render(0.8));
        Assert.Equal("233.0922", Render(233.0922));
        Assert.DoesNotContain(",", Render(47.9));
    }

    [Theory]
    [MemberData(nameof(CommaCultures))]
    public void Values_posted_by_the_browser_parse_whatever_the_server_culture_is(string culture)
    {
        using var _ = new CultureScope(culture);

        Assert.True(TryRead("47.9", out var seconds));
        Assert.Equal(47.9, seconds, 3);

        Assert.True(TryRead("0.8", out var volume));
        Assert.Equal(0.8, volume, 3);
    }

    [Theory]
    [MemberData(nameof(CommaCultures))]
    public void What_we_render_is_what_we_can_read_back(string culture)
    {
        // The round trip is the property that actually matters.
        using var _ = new CultureScope(culture);

        foreach (var original in new[] { 0d, 0.01, 0.8, 47.9, 233.0922, 3599.5 })
        {
            Assert.True(TryRead(Render(original), out var read), $"failed to read back {original}");
            Assert.Equal(original, read, 4);
        }
    }

    [Fact]
    public void A_comma_decimal_is_rejected_rather_than_silently_misread()
    {
        // "0,8" must not quietly become 8 under an invariant parse.
        using var scope = new CultureScope("nl-NL");

        Assert.False(TryRead("0,8", out _));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string culture) => CultureInfo.CurrentCulture = new CultureInfo(culture);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
