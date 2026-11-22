using System.Text.Unicode;

namespace TicketFlow.Automation;

/// <summary>Shared validation for bounded automation text and instants.</summary>
internal static class AutomationText
{
    /// <summary>Validates non-blank bounded single-line text without control characters and with well-formed UTF-16.</summary>
    public static string RequireText(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (value.Length > maximumLength || HasControl(value, allowLineBreaks: false) || !Utf16.IsValid(value))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    public static string? OptionalText(string? value, int maximumLength, string parameterName) =>
        value is null ? null : RequireText(value, maximumLength, parameterName);

    /// <summary>Validates multi-line content (tab, CR and LF allowed) of at most <paramref name="maximumLength"/> UTF-16 units.</summary>
    public static string RequireContent(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > maximumLength || HasControl(value, allowLineBreaks: true) || !Utf16.IsValid(value))
            throw new ArgumentException($"Content must contain 1-{maximumLength} characters, no control characters other than tab/CR/LF and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>A lowercase token: 1-<paramref name="maximumLength"/> characters of a-z, 0-9, '-', '_' or '.', starting with a letter.</summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.Length is 0 || value.Length > maximumLength || value[0] is not (>= 'a' and <= 'z'))
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
            throw new ArgumentException($"{description} are 1-{maximumLength} characters of a-z, 0-9, '-', '_' or '.', starting with a letter.", parameterName);
        return value;
    }

    /// <summary>UTC with microsecond precision, matching PostgreSQL <c>timestamptz</c>.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static DateTimeOffset? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;

    public static T Defined<T>(T value, string parameterName) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(parameterName);

    /// <summary>Snapshots a bounded, distinct list preserving first-seen order.</summary>
    public static IReadOnlyList<T> Distinct<T>(IEnumerable<T> items, int maximum, Func<T, bool> valid, string parameterName, bool allowEmpty = true)
    {
        ArgumentNullException.ThrowIfNull(items, parameterName);
        var seen = new HashSet<T>();
        var snapshot = new List<T>();
        foreach (var item in items)
        {
            if (!valid(item))
                throw new ArgumentException("Items must be initialized and valid.", parameterName);
            if (seen.Add(item))
            {
                snapshot.Add(item);
                if (snapshot.Count > maximum)
                    throw new ArgumentException($"At most {maximum} items are allowed.", parameterName);
            }
        }
        if (!allowEmpty && snapshot.Count == 0)
            throw new ArgumentException("At least one item is required.", parameterName);
        return snapshot.AsReadOnly();
    }

    private static bool HasControl(string value, bool allowLineBreaks)
    {
        foreach (var character in value)
        {
            if (allowLineBreaks && character is '\t' or '\r' or '\n')
                continue;
            if (character is <= '\u001F' or (>= '\u007F' and <= '\u009F'))
                return true;
        }
        return false;
    }
}
