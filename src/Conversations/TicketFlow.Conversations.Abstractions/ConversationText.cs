using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace TicketFlow.Conversations;

/// <summary>Shared validation for bounded conversation tokens, free text, bodies and timestamps.</summary>
internal static class ConversationText
{
    private static readonly SearchValues<char> TokenChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_");
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

    /// <summary>A lowercase token: starts with a-z, then a-z, 0-9, '-' or '_'.</summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength) =>
        !value.IsEmpty && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' && !value.ContainsAnyExcept(TokenChars);

    public static string RequireToken(string value, int maximumLength, string parameterName, string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"{description} must be 1-{maximumLength} characters of a-z, 0-9, '-' or '_', starting with a letter.", parameterName);
        return value;
    }

    /// <summary>A dotted name of 1-8 tokens such as <c>sla.breached</c>, at most <paramref name="maximumLength"/> characters.</summary>
    public static string RequireDottedName(string value, int maximumLength, string parameterName, string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > maximumLength)
            throw new ArgumentException($"{description} must be 1-8 dot-separated lowercase tokens and at most {maximumLength} characters.", parameterName);
        var span = value.AsSpan();
        var segments = 0;
        foreach (var range in span.Split('.'))
        {
            segments++;
            if (segments > 8 || !IsToken(span[range], 64))
                throw new ArgumentException($"{description} must be 1-8 dot-separated lowercase tokens and at most {maximumLength} characters.", parameterName);
        }
        return value;
    }

    /// <summary>True when the text has no C0/C1 control characters and is well-formed UTF-16.</summary>
    public static bool IsClean(ReadOnlySpan<char> value) =>
        !value.ContainsAny(ControlChars) && (!value.ContainsAnyInRange('\uD800', '\uDFFF') || Utf16.IsValid(value));

    /// <summary>Validates bounded free text without control characters and with well-formed UTF-16.</summary>
    public static string RequireText(string value, int maximumLength, string parameterName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (value.Length > maximumLength || !IsClean(value))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    public static string? OptionalText(string? value, int maximumLength, string parameterName) =>
        value is null ? null : RequireText(value, maximumLength, parameterName);

    /// <summary>
    /// Validates a message or note body: at most <paramref name="maximumUtf8Bytes"/> UTF-8 bytes, well-formed UTF-16 and no control
    /// characters other than tab, line feed and carriage return. Bodies may be empty only when <paramref name="allowEmpty"/>.
    /// </summary>
    public static string RequireBody(string value, int maximumUtf8Bytes, string parameterName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank body is required.", parameterName);
        var span = value.AsSpan();
        if (span.Length > maximumUtf8Bytes || (span.ContainsAnyInRange('\uD800', '\uDFFF') && !Utf16.IsValid(span)))
            throw new ArgumentException($"Bodies must be well-formed Unicode of at most {maximumUtf8Bytes} UTF-8 bytes.", parameterName);
        if (span.ContainsAny(BodyInvalidControls))
            throw new ArgumentException("Bodies cannot contain control characters other than tab and line breaks.", parameterName);
        if (span.Length > maximumUtf8Bytes / 3 && Encoding.UTF8.GetByteCount(span) > maximumUtf8Bytes)
            throw new ArgumentException($"Bodies must be well-formed Unicode of at most {maximumUtf8Bytes} UTF-8 bytes.", parameterName);
        return value;
    }

    /// <summary>UTC with microsecond precision, matching PostgreSQL <c>timestamptz</c>.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static DateTimeOffset? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;
}
