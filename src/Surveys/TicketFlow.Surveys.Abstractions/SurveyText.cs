using System.Text.Unicode;

namespace TicketFlow.Surveys;

/// <summary>Shared validation for bounded survey text.</summary>
public static class SurveyText
{
    /// <summary>Validates non-blank bounded single-line text without control characters and with well-formed UTF-16.</summary>
    public static string RequireLine(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.AsSpan().ContainsAnyInRange('\0', '\u001F')
            || value.AsSpan().ContainsAnyInRange('\u007F', '\u009F') || !Utf16.IsValid(value))
            throw new ArgumentException($"Values must be non-blank, contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>True when <paramref name="value"/> is non-blank bounded multi-line text (line breaks and tabs allowed).</summary>
    public static bool IsMultiline(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || !Utf16.IsValid(value))
            return false;
        foreach (var character in value)
            if (character is (< ' ' and not '\n' and not '\r' and not '\t') or (>= '\u007F' and <= '\u009F'))
                return false;
        return true;
    }
}
