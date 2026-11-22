using System.Text;
using System.Text.Unicode;

namespace TicketFlow.Conversations;

/// <summary>Shared validation for bounded conversation tokens, free text, bodies and timestamps.</summary>
internal static class ConversationText
{
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
        var segments = value.Split('.');
        if (value.Length > maximumLength || segments.Length > 8 || segments.Any(segment => !IsToken(segment, 64)))
            throw new ArgumentException($"{description} must be 1-8 dot-separated lowercase tokens and at most {maximumLength} characters.", parameterName);
        return value;
    }

    /// <summary>True when the text has no C0/C1 control characters and is well-formed UTF-16.</summary>
    public static bool IsClean(ReadOnlySpan<char> value) =>
        !value.ContainsAnyInRange('\0', '\u001F') && !value.ContainsAnyInRange('\u007F', '\u009F') && Utf16.IsValid(value);

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
        if (value.Length > maximumUtf8Bytes || !Utf16.IsValid(value))
            throw new ArgumentException($"Bodies must be well-formed Unicode of at most {maximumUtf8Bytes} UTF-8 bytes.", parameterName);
        foreach (var character in value)
            if (character is (< ' ' and not '\t' and not '\n' and not '\r') or (>= '\u007F' and <= '\u009F'))
                throw new ArgumentException("Bodies cannot contain control characters other than tab and line breaks.", parameterName);
        if (Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
            throw new ArgumentException($"Bodies must be well-formed Unicode of at most {maximumUtf8Bytes} UTF-8 bytes.", parameterName);
        return value;
    }

    /// <summary>UTC with microsecond precision, matching PostgreSQL <c>timestamptz</c>.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static DateTimeOffset? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;
}
