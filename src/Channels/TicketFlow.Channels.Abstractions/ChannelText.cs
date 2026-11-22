using System.Text;
using System.Text.Unicode;

namespace TicketFlow.Channels;

/// <summary>Shared validation for bounded channel text. Error messages never contain the validated value.</summary>
public static class ChannelText
{
    /// <summary>Maximum external (provider) identifier length, matching conversation external ids.</summary>
    public const int MaximumExternalIdLength = 1024;

    /// <summary>True when the text has no C0/C1 control characters and is well-formed UTF-16.</summary>
    public static bool IsClean(ReadOnlySpan<char> value) =>
        !value.ContainsAnyInRange('\0', '\u001F') && !value.ContainsAnyInRange('\u007F', '\u009F') && Utf16.IsValid(value);

    /// <summary>Validates non-blank bounded text without control characters.</summary>
    public static string RequireText(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (value.Length > maximumLength || !IsClean(value))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    public static string? OptionalText(string? value, int maximumLength, string parameterName) =>
        value is null ? null : RequireText(value, maximumLength, parameterName);

    /// <summary>A lowercase token: starts with a-z, then a-z, 0-9, '-' or '_'.</summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.IsEmpty || value.Length > maximumLength || value[0] is not (>= 'a' and <= 'z'))
            return false;
        foreach (var character in value)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        return true;
    }

    public static string RequireToken(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"Tokens must be 1-{maximumLength} characters of a-z, 0-9, '-' or '_', starting with a letter.", parameterName);
        return value;
    }

    /// <summary>
    /// Converts a provider failure code (for example <c>30007</c> or <c>customer_service_window_closed</c>) into a conversation failure
    /// code token: lowercased, other characters replaced by '-', prefixed with <c>e-</c> when it does not start with a letter, at most
    /// 64 characters. Returns <paramref name="fallback"/> for blank input.
    /// </summary>
    public static string FailureCode(string? providerCode, string fallback = "failed")
    {
        if (string.IsNullOrWhiteSpace(providerCode))
            return fallback;
        var builder = new StringBuilder(Math.Min(providerCode.Length, 64) + 2);
        foreach (var character in providerCode.Trim())
        {
            var lower = char.ToLowerInvariant(character);
            builder.Append(lower is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' ? lower : '-');
            if (builder.Length >= 64)
                break;
        }
        if (builder.Length == 0 || builder[0] is not (>= 'a' and <= 'z'))
            builder.Insert(0, "e-");
        return builder.Length > 64 ? builder.ToString(0, 64) : builder.ToString();
    }

    /// <summary>
    /// Normalizes a message reference for storage and comparison: trims whitespace and removes one enclosing pair of angle brackets
    /// (<c>&lt;id@host&gt;</c> → <c>id@host</c>). Returns <see langword="null"/> when nothing usable remains.
    /// </summary>
    public static string? NormalizeReference(string? reference)
    {
        if (reference is null)
            return null;
        var value = reference.Trim();
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            value = value[1..^1].Trim();
        return value.Length == 0 || value.Length > MaximumExternalIdLength || !IsClean(value) ? null : value;
    }

    /// <summary>
    /// Splits an email <c>References</c> or <c>In-Reply-To</c> header value into normalized references (oldest first), at most
    /// <paramref name="maximum"/> (the newest are kept).
    /// </summary>
    public static IReadOnlyList<string> ParseReferenceHeader(string? header, int maximum = 32)
    {
        if (string.IsNullOrWhiteSpace(header))
            return [];
        var references = new List<string>();
        var index = 0;
        while (index < header.Length)
        {
            var open = header.IndexOf('<', index);
            if (open < 0)
                break;
            var close = header.IndexOf('>', open + 1);
            if (close < 0)
                break;
            if (NormalizeReference(header[open..(close + 1)]) is { } reference && !references.Contains(reference, StringComparer.Ordinal))
                references.Add(reference);
            index = close + 1;
        }
        return references.Count > maximum ? references[^maximum..] : references;
    }

    /// <summary>
    /// Makes provider text acceptable as a conversation body: control characters other than tab, CR and LF become spaces, invalid
    /// UTF-16 is replaced, and text longer than <paramref name="maximumUtf8Bytes"/> is cut at a character boundary.
    /// </summary>
    public static string SanitizeBody(string value, int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        var bytes = 0;
        foreach (var rune in EnumerateRunes(value))
        {
            var current = rune.Value is (< ' ' and not '\t' and not '\n' and not '\r') or (>= 0x7F and <= 0x9F) ? new Rune(' ') : rune;
            var size = current.Utf8SequenceLength;
            if (bytes + size > maximumUtf8Bytes)
                break;
            bytes += size;
            builder.Append(current.ToString());
        }
        return builder.ToString();
    }

    private static IEnumerable<Rune> EnumerateRunes(string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            var status = Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed);
            yield return status == System.Buffers.OperationStatus.Done ? rune : Rune.ReplacementChar;
            index += Math.Max(consumed, 1);
        }
    }
}
