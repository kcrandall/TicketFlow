using System.Buffers;
using System.Text.Unicode;

namespace TicketFlow.Workspaces;

/// <summary>Shared validation for bounded workspace tokens and free text.</summary>
internal static class WorkspaceText
{
    private static readonly SearchValues<char> TokenChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_.");
    private static readonly SearchValues<char> ControlChars = CreateControlChars(allowLineBreaksAndTabs: false);
    private static readonly SearchValues<char> MessageDisallowedControlChars = CreateControlChars(allowLineBreaksAndTabs: true);

    /// <summary>Validates a lowercase token: starts with a-z, then a-z, 0-9, '-', '_' or '.'.</summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength) =>
        !value.IsEmpty && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' && !value.ContainsAnyExcept(TokenChars);

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
        var span = value.AsSpan();
        if (span.Length > maximumLength || span.ContainsAny(ControlChars) || (span.ContainsAnyInRange('\uD800', '\uDFFF') && !Utf16.IsValid(span)))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>Validates non-blank bounded multi-line text: line feeds, carriage returns and tabs are allowed, other controls are not.</summary>
    public static string RequireMessage(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        var span = value.AsSpan();
        if (span.Length > maximumLength || span.ContainsAny(MessageDisallowedControlChars) || (span.ContainsAnyInRange('\uD800', '\uDFFF') && !Utf16.IsValid(span)))
            throw new ArgumentException($"Messages must contain at most {maximumLength} characters, no control characters other than line breaks and tabs, and well-formed Unicode.", parameterName);
        return value;
    }

    private static SearchValues<char> CreateControlChars(bool allowLineBreaksAndTabs)
    {
        Span<char> chars = stackalloc char[65];
        var index = 0;
        for (var c = '\0'; c <= '\u001F'; c++)
            if (!allowLineBreaksAndTabs || c is not ('\n' or '\r' or '\t'))
                chars[index++] = c;
        for (var c = '\u007F'; c <= '\u009F'; c++)
            chars[index++] = c;
        return SearchValues.Create(chars[..index]);
    }
}
