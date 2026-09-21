using System.Globalization;

namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Unicode code point helpers used for the text limits and for bounded error summaries.
/// Code points are counted independently of UTF-8 byte length and UTF-16 code units, so a
/// surrogate pair counts once while a base character plus a combining mark counts twice.
/// </summary>
internal static class TextMetrics
{
    /// <summary>Gets a value indicating whether the text contains more than <paramref name="max"/> code points.</summary>
    public static bool ExceedsCodepoints(ReadOnlySpan<char> text, int max)
    {
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
            count++;
            if (count > max)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates the bounded error summary: the first <paramref name="maxCodepoints"/> code points
    /// followed by an ellipsis when the original text was longer. Returns null for null input.
    /// </summary>
    public static string? CreateSummary(string? text, int maxCodepoints)
    {
        if (text is null)
        {
            return null;
        }

        if (!ExceedsCodepoints(text, maxCodepoints))
        {
            return text;
        }

        var taken = 0;
        var index = 0;
        while (index < text.Length && taken < maxCodepoints)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? 2
                : 1;
            taken++;
        }

        return string.Concat(text.AsSpan(0, index).ToString(), "\u2026");
    }
}

/// <summary>
/// Bounded accumulator for XML element text. It keeps at most the summary code point count and
/// only tracks whether the text exceeds the text limit, the summary was truncated and the text
/// is entirely whitespace. Large element text is therefore never materialized.
/// </summary>
internal sealed class TextAccumulator
{
    private readonly int _maxTextCodepoints;
    private readonly int _maxStoredCodepoints;
    private readonly char[] _chars;
    private int _length;
    private int _storedCodepoints;
    private int _codepoints;
    private bool _overLimit;
    private bool _dropped;
    private bool _allWhitespace = true;
    private bool _pendingHighSurrogate;
    private bool _pendingStoredHighSurrogate;

    public TextAccumulator(int maxTextCodepoints, int maxSummaryCodepoints)
    {
        _maxTextCodepoints = maxTextCodepoints;
        _maxStoredCodepoints = maxSummaryCodepoints;
        _chars = new char[(maxSummaryCodepoints * 2) + 2];
    }

    /// <summary>Gets a value indicating whether the whole text is whitespace (an empty text is whitespace).</summary>
    public bool IsAllWhitespace => _allWhitespace;

    /// <summary>Gets a value indicating whether the text exceeds the code point limit.</summary>
    public bool OverLimit => _overLimit;

    /// <summary>Gets the stored text, which is the full text when it fits the summary bound.</summary>
    public string Text => new(_chars, 0, _length);

    /// <summary>Gets the bounded error summary with a truncation marker when needed.</summary>
    public string Summary => _dropped ? string.Concat(Text, "\u2026") : Text;

    /// <summary>Appends a chunk of element text.</summary>
    public void Append(ReadOnlySpan<char> chunk)
    {
        for (var index = 0; index < chunk.Length; index++)
        {
            var current = chunk[index];
            _allWhitespace &= char.IsWhiteSpace(current);

            if (_pendingHighSurrogate)
            {
                _pendingHighSurrogate = false;
                if (!char.IsLowSurrogate(current))
                {
                    CountCodepoint();
                }
            }
            else if (char.IsHighSurrogate(current))
            {
                _pendingHighSurrogate = true;
                CountCodepoint();
            }
            else
            {
                CountCodepoint();
            }

            Store(current);
        }
    }

    private void CountCodepoint()
    {
        _codepoints++;
        if (_codepoints > _maxTextCodepoints)
        {
            _overLimit = true;
        }
    }

    private void Store(char current)
    {
        if (_pendingStoredHighSurrogate)
        {
            // The high surrogate was stored with capacity reserved for its low half.
            _chars[_length++] = current;
            _pendingStoredHighSurrogate = false;
            return;
        }

        if (char.IsLowSurrogate(current))
        {
            // A low surrogate without a stored high surrogate cannot be paired; drop it.
            _dropped = true;
            return;
        }

        if (_storedCodepoints >= _maxStoredCodepoints)
        {
            _dropped = true;
            return;
        }

        _storedCodepoints++;
        _chars[_length++] = current;
        _pendingStoredHighSurrogate = char.IsHighSurrogate(current);
    }
}

/// <summary>
/// Integer parsing helpers shared by both format parsers.
/// </summary>
internal static class IntegerParsing
{
    /// <summary>Parses an invariant-culture integer, allowing surrounding whitespace.</summary>
    public static bool TryParse(string value, out long result) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Converts a decimal seconds value to milliseconds, rounding half away from zero when the
    /// value is finer than one millisecond. Rejects negative and non-storable values.
    /// </summary>
    public static bool TryParseSecondsToMilliseconds(string value, out long milliseconds)
    {
        milliseconds = 0;
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        if (seconds < 0 || seconds > MaxSeconds)
        {
            return false;
        }

        var rounded = Math.Round(seconds * 1000m, 0, MidpointRounding.AwayFromZero);
        if (rounded > long.MaxValue)
        {
            return false;
        }

        milliseconds = (long)rounded;
        return true;
    }

    // Keeps the multiplication inside the decimal range while values beyond the storable
    // millisecond range are still rejected before and after rounding.
    private static readonly decimal MaxSeconds = ((decimal)long.MaxValue / 1000m) + 1m;
}
