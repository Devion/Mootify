using System.Text;

namespace Mootify.Services.Ideas;

/// <summary>
/// What came back from trying to turn what somebody typed into something safe to store.
/// <paramref name="Text"/> is non-null exactly when <paramref name="Error"/> is null.
/// </summary>
public sealed record IdeaTextResult(string? Text, string? Error)
{
    public bool Ok => Text is not null;

    public static IdeaTextResult Accepted(string text) => new(text, null);

    public static IdeaTextResult Rejected(string error) => new(null, error);
}

/// <summary>
/// The one door an idea comes in through, and the only place that decides what an idea may
/// contain.
///
/// Rendering is already safe on its own — Razor escapes interpolated text, and nothing in the
/// Ideabox ever becomes a <c>MarkupString</c> — so this is not the escaping. It is the layer
/// under it, and it exists because "we escape on output" is a promise about every future
/// rendering site, including the ones nobody has written yet (a log line, a notification body, a
/// CSV export, a terminal).
///
/// The rule is therefore deliberately blunt: <b>printable ASCII and newlines, nothing else</b>.
/// That is not about <c>&lt;script&gt;</c>, which escaping handles; it is about the characters
/// that change what a correctly-escaped string <i>means</i> —
///
/// - <c>U+202E</c> RIGHT-TO-LEFT OVERRIDE and friends, which reverse the text after them, so a
///   message can render as the opposite of what is stored;
/// - <c>U+200B</c>/<c>U+200D</c> zero-width characters, which are invisible in every list they
///   appear in and survive a copy-paste;
/// - homoglyphs — a Cyrillic "а" reading as a Latin "a" in a name somebody is being asked to trust;
/// - C0/C1 control characters, which mean things to terminals and to nothing else.
///
/// The cost is that "café" and emoji are refused. For a suggestion box on a household music
/// server that is a fair trade, and the refusal says so in words rather than silently dropping
/// characters — a sanitiser that quietly mangles what somebody wrote is its own kind of wrong.
/// </summary>
public static class IdeaText
{
    /// <summary>Long enough for a paragraph, short enough that the admin's list stays a list.</summary>
    public const int MaxLength = 500;

    /// <summary>Blank lines are fine; a wall of them is somebody testing the box.</summary>
    private const int MaxConsecutiveNewlines = 2;

    /// <summary>Everything from space to tilde. The printable half of ASCII, and all of it.</summary>
    private static bool IsAllowed(char c) => c is >= ' ' and <= '~';

    /// <summary>
    /// Normalises whitespace, then refuses anything that isn't printable ASCII.
    ///
    /// Order matters: normalising first means a tab or a CRLF is fixed rather than reported as an
    /// illegal character, and the person is only ever told about characters they would have to
    /// actually remove.
    /// </summary>
    public static IdeaTextResult Validate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return IdeaTextResult.Rejected("There's nothing in there yet.");

        // Cheap pre-check on the raw length. Without it a megabyte of pasted text is normalised
        // character by character before being refused for being too long.
        if (raw.Length > MaxLength * 4)
        {
            return IdeaTextResult.Rejected($"That's much too long — keep it under {MaxLength} characters.");
        }

        var normalised = NormaliseWhitespace(raw);

        if (FirstDisallowed(normalised) is { } bad)
        {
            return IdeaTextResult.Rejected(
                $"The Ideabox only takes plain text — no accents, emoji or symbols. " +
                $"Character {bad.Position} (U+{(int)bad.Character:X4}) isn't allowed.");
        }

        if (normalised.Length == 0) return IdeaTextResult.Rejected("There's nothing in there yet.");

        if (normalised.Length > MaxLength)
        {
            return IdeaTextResult.Rejected(
                $"That's {normalised.Length} characters — keep it under {MaxLength}.");
        }

        return IdeaTextResult.Accepted(normalised);
    }

    /// <summary>
    /// CRLF and CR become LF, tabs become spaces, trailing spaces come off every line, and a run
    /// of blank lines is capped. What's left is one representation of "the same message", so two
    /// people pasting from two editors store the same thing.
    /// </summary>
    private static string NormaliseWhitespace(string raw)
    {
        var text = new StringBuilder(raw.Length);
        var newlineRun = 0;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            // Consume CRLF as one newline rather than two.
            if (c == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n') i++;
                c = '\n';
            }

            if (c == '\n')
            {
                newlineRun++;
                if (newlineRun <= MaxConsecutiveNewlines) text.Append('\n');
                continue;
            }

            newlineRun = 0;
            text.Append(c == '\t' ? ' ' : c);
        }

        return TrimLines(text.ToString());
    }

    /// <summary>
    /// Trailing whitespace per line, and blank lines off both ends. Done as a pass over the whole
    /// string because a message is small and this stays obvious.
    /// </summary>
    private static string TrimLines(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.TrimEnd())).Trim('\n', ' ');

    /// <summary>
    /// The first character a person would have to delete, 1-based so the number matches what they
    /// can count on screen. The character itself is reported as a code point, never echoed —
    /// putting the offending character back into the message is how a sanitiser becomes the
    /// injection.
    /// </summary>
    private static (int Position, char Character)? FirstDisallowed(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && !IsAllowed(text[i])) return (i + 1, text[i]);
        }

        return null;
    }
}
