using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace TicketFlow.Channels;

/// <summary>Shared validation for bounded channel text. Error messages never contain the validated value.</summary>
public static class ChannelText
{
    /// <summary>Maximum external (provider) identifier length, matching conversation external ids.</summary>
    public const int MaximumExternalIdLength = 1024;

    private static readonly SearchValues<char> ControlChars = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F" +
        "\u007F\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E\u008F" +
        "\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E\u009F");

    private static readonly SearchValues<char> BodyInvalidControls = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u000B\u000C\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F" +
        "\u007F\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E\u008F" +
        "\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E\u009F");

    private static readonly SearchValues<char> TokenChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>True when the text has no C0/C1 control characters and is well-formed UTF-16.</summary>
    public static bool IsClean(ReadOnlySpan<char> value) =>
        !value.ContainsAny(ControlChars) && (!value.ContainsAnyInRange('\uD800', '\uDFFF') || Utf16.IsValid(value));

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
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength) =>
        !value.IsEmpty && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' && !value.ContainsAnyExcept(TokenChars);

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
        var trimmed = providerCode.AsSpan().Trim();
        Span<char> buffer = stackalloc char[66];
        var len = 0;
        var take = Math.Min(trimmed.Length, 64);
        for (var i = 0; i < take; i++)
        {
            var lower = char.ToLowerInvariant(trimmed[i]);
            buffer[len++] = lower is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' ? lower : '-';
        }
        if (len == 0 || buffer[0] is not (>= 'a' and <= 'z'))
        {
            var copyLen = Math.Min(len, 62);
            buffer[..copyLen].CopyTo(buffer[2..]);
            buffer[0] = 'e';
            buffer[1] = '-';
            len = copyLen + 2;
        }
        return new string(buffer[..len]);
    }

    /// <summary>
    /// Normalizes a message reference for storage and comparison: trims whitespace and removes one enclosing pair of angle brackets
    /// (<c>&lt;id@host&gt;</c> → <c>id@host</c>). Returns <see langword="null"/> when nothing usable remains.
    /// </summary>
    public static string? NormalizeReference(string? reference)
    {
        if (reference is null)
            return null;
        var span = reference.AsSpan().Trim();
        if (span.Length >= 2 && span[0] == '<' && span[^1] == '>')
            span = span[1..^1].Trim();
        if (span.Length == 0 || span.Length > MaximumExternalIdLength || !IsClean(span))
            return null;
        return span.Length == reference.Length ? reference : span.ToString();
    }

    private static string? NormalizeReference(ReadOnlySpan<char> span)
    {
        span = span.Trim();
        if (span.Length >= 2 && span[0] == '<' && span[^1] == '>')
            span = span[1..^1].Trim();
        return span.Length == 0 || span.Length > MaximumExternalIdLength || !IsClean(span) ? null : span.ToString();
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
        HashSet<string>? seen = null;
        var span = header.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            var openRel = span[index..].IndexOf('<');
            if (openRel < 0)
                break;
            var open = index + openRel;
            var closeRel = span[(open + 1)..].IndexOf('>');
            if (closeRel < 0)
                break;
            var close = open + 1 + closeRel;
            if (NormalizeReference(span[open..(close + 1)]) is { } reference)
            {
                bool isNew;
                if (seen is not null)
                    isNew = seen.Add(reference);
                else if (references.Count >= 8)
                {
                    seen = new HashSet<string>(references, StringComparer.Ordinal);
                    isNew = seen.Add(reference);
                }
                else
                    isNew = !references.Contains(reference, StringComparer.Ordinal);
                if (isNew)
                    references.Add(reference);
            }
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
        if (value.Length == 0 || maximumUtf8Bytes <= 0)
            return string.Empty;
        var span = value.AsSpan();
        if (!span.ContainsAny(BodyInvalidControls)
            && (!span.ContainsAnyInRange('\uD800', '\uDFFF') || Utf16.IsValid(span))
            && (span.Length <= maximumUtf8Bytes / 3 || Encoding.UTF8.GetByteCount(span) <= maximumUtf8Bytes))
            return value;

        var builder = new StringBuilder(Math.Min(value.Length, maximumUtf8Bytes));
        Span<char> encoded = stackalloc char[2];
        var bytes = 0;
        var index = 0;
        while (index < span.Length)
        {
            var status = Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed);
            var current = status == OperationStatus.Done ? rune : Rune.ReplacementChar;
            index += Math.Max(consumed, 1);
            if (current.Value is (< ' ' and not '\t' and not '\n' and not '\r') or (>= 0x7F and <= 0x9F))
                current = new Rune(' ');
            var size = current.Utf8SequenceLength;
            if (bytes + size > maximumUtf8Bytes)
                break;
            bytes += size;
            var written = current.EncodeToUtf16(encoded);
            builder.Append(encoded[..written]);
        }
        return builder.ToString();
    }
}
