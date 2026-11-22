using System.Text.Unicode;

namespace TicketFlow.Workspaces;

/// <summary>Shared validation for bounded workspace tokens and free text.</summary>
internal static class WorkspaceText
{
    /// <summary>Validates a lowercase token: starts with a-z, then a-z, 0-9, '-', '_' or '.'.</summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.IsEmpty || value.Length > maximumLength || value[0] is not (>= 'a' and <= 'z'))
            return false;
        foreach (var character in value)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
                return false;
        return true;
    }

    public static string RequireToken(string value, int maximumLength, string parameterName, string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"{description} must be 1-{maximumLength} characters of a-z, 0-9, '-', '_' or '.', starting with a letter.", parameterName);
        return value;
    }

    /// <summary>Validates non-blank bounded single-line text without control characters and with well-formed UTF-16.</summary>
    public static string RequireText(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (value.Length > maximumLength || value.AsSpan().ContainsAnyInRange('\0', '\u001F') || value.AsSpan().ContainsAnyInRange('\u007F', '\u009F') || !Utf16.IsValid(value))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>Validates non-blank bounded multi-line text: line feeds, carriage returns and tabs are allowed, other controls are not.</summary>
    public static string RequireMessage(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        var invalid = value.Length > maximumLength || !Utf16.IsValid(value);
        foreach (var character in value)
            invalid |= character is (< ' ' and not '\n' and not '\r' and not '\t') or (>= '\u007F' and <= '\u009F');
        if (invalid)
            throw new ArgumentException($"Messages must contain at most {maximumLength} characters, no control characters other than line breaks and tabs, and well-formed Unicode.", parameterName);
        return value;
    }
}
