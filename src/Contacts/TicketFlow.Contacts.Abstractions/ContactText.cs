using System.Text.Unicode;

namespace TicketFlow.Contacts;

/// <summary>Shared validation for bounded contact tokens, free text and timestamps.</summary>
internal static class ContactText
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

    /// <summary>UTC with microsecond precision, matching PostgreSQL <c>timestamptz</c>.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
