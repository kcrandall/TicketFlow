using System.Text.Unicode;

namespace TicketFlow.Assist;

/// <summary>Shared validation for bounded assistant text.</summary>
public static class AssistText
{
    /// <summary>
    /// Validates bounded multi-line text: line feeds, carriage returns and tabs are allowed, other control characters are not, and the
    /// text must be well-formed UTF-16. Blank text is refused unless <paramref name="allowEmpty"/>.
    /// </summary>
    public static string RequireText(string value, int maximumLength, string parameterName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (!IsText(value, maximumLength))
            throw new ArgumentException($"Text must contain at most {maximumLength} characters, no control characters other than line breaks and tabs, and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>Like <see cref="RequireText"/> but returns <see langword="null"/> for <see langword="null"/>.</summary>
    public static string? OptionalText(string? value, int maximumLength, string parameterName) =>
        value is null ? null : RequireText(value, maximumLength, parameterName);

    /// <summary>True when <paramref name="value"/> is bounded multi-line text (possibly empty).</summary>
    public static bool IsText(string value, int maximumLength)
    {
        if (value.Length > maximumLength || !Utf16.IsValid(value))
            return false;
        foreach (var character in value)
            if (character is (< ' ' and not '\n' and not '\r' and not '\t') or (>= '\u007F' and <= '\u009F'))
                return false;
        return true;
    }

    /// <summary>
    /// A lowercase ASCII token: 1-<paramref name="maximumLength"/> characters of a-z, 0-9, '-', '_', '.', ':' or '/', starting with a
    /// letter or digit. Used for model identifiers, schema names and intent labels.
    /// </summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.IsEmpty || value.Length > maximumLength || value[0] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            return false;
        foreach (var character in value)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or ':' or '/' or '@'))
                return false;
        return true;
    }

    public static string RequireToken(string value, int maximumLength, string parameterName, string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"{description} are 1-{maximumLength} lowercase ASCII letters, digits, '-', '_', '.', ':', '/' or '@', starting with a letter or digit.", parameterName);
        return value;
    }

    /// <summary>Truncates to at most <paramref name="maximumLength"/> UTF-16 units without splitting a surrogate pair.</summary>
    public static string Truncate(string value, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= maximumLength)
            return value;
        var length = maximumLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
            length--;
        return value[..length];
    }
}
