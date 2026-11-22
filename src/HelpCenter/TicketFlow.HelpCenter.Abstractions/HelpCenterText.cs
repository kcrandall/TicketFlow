using System.Text.Unicode;

namespace TicketFlow.HelpCenter;

/// <summary>Shared validation for bounded help center text.</summary>
public static class HelpCenterText
{
    /// <summary>Validates non-blank bounded single-line text without control characters and with well-formed UTF-16.</summary>
    public static string RequireLine(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (value.Length > maximumLength || value.AsSpan().ContainsAnyInRange('\0', '\u001F') || value.AsSpan().ContainsAnyInRange('\u007F', '\u009F') || !Utf16.IsValid(value))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>Validates non-blank bounded multi-line text: line feeds, carriage returns and tabs are allowed, other controls are not.</summary>
    public static string RequireMultiline(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (!IsMultiline(value, maximumLength))
            throw new ArgumentException($"Text must contain at most {maximumLength} characters, no control characters other than line breaks and tabs, and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>True when <paramref name="value"/> is bounded multi-line text (possibly empty).</summary>
    public static bool IsMultiline(string value, int maximumLength)
    {
        if (value.Length > maximumLength || !Utf16.IsValid(value))
            return false;
        foreach (var character in value)
            if (character is (< ' ' and not '\n' and not '\r' and not '\t') or (>= '\u007F' and <= '\u009F'))
                return false;
        return true;
    }

    /// <summary>A lowercase ASCII token: 1-<paramref name="maximumLength"/> characters of a-z, 0-9, '-', '_' or '.', starting with a letter or digit.</summary>
    public static bool IsTag(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.IsEmpty || value.Length > maximumLength || value[0] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            return false;
        foreach (var character in value)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
                return false;
        return true;
    }
}
