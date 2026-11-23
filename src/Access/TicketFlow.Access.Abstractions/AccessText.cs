using System.Buffers;
using System.Text.Unicode;

namespace TicketFlow.Access;

/// <summary>Shared validation for bounded access-control tokens and free text.</summary>
internal static class AccessText
{
    private static readonly SearchValues<char> TokenChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_.");
    private static readonly SearchValues<char> FieldSegmentChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_");
    private static readonly SearchValues<char> ControlChars = CreateControlChars();

    /// <summary>Validates a lowercase token: starts with a-z, then a-z, 0-9, '-', '_' or '.'.</summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength) =>
        !value.IsEmpty && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' && !value.ContainsAnyExcept(TokenChars);

    /// <summary>Validates a lowercase field-path segment: starts with a-z, then a-z, 0-9, '-' or '_'.</summary>
    public static bool IsFieldSegment(ReadOnlySpan<char> value, int maximumLength) =>
        !value.IsEmpty && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' && !value.ContainsAnyExcept(FieldSegmentChars);

    public static string RequireToken(string value, int maximumLength, string parameterName, string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"{description} must be 1-{maximumLength} characters of a-z, 0-9, '-', '_' or '.', starting with a letter.", parameterName);
        return value;
    }

    public static bool IsValidText(string? value, int maximumLength, bool allowEmpty = false)
    {
        if (value is null)
            return false;
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            return false;
        var span = value.AsSpan();
        return span.Length <= maximumLength && !span.ContainsAny(ControlChars) && (!span.ContainsAnyInRange('\uD800', '\uDFFF') || Utf16.IsValid(span));
    }

    /// <summary>Validates bounded free text without control characters and with well-formed UTF-16.</summary>
    public static string RequireText(string value, int maximumLength, string parameterName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        var span = value.AsSpan();
        if (span.Length > maximumLength || span.ContainsAny(ControlChars) || (span.ContainsAnyInRange('\uD800', '\uDFFF') && !Utf16.IsValid(span)))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    private static SearchValues<char> CreateControlChars()
    {
        Span<char> chars = stackalloc char[65];
        var index = 0;
        for (var c = '\0'; c <= '\u001F'; c++)
            chars[index++] = c;
        for (var c = '\u007F'; c <= '\u009F'; c++)
            chars[index++] = c;
        return SearchValues.Create(chars);
    }
}
