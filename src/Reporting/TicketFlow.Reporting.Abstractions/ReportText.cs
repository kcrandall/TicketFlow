using System.Text.Unicode;

namespace TicketFlow.Reporting;

/// <summary>Shared validation for bounded reporting values.</summary>
public static class ReportText
{
    /// <summary>UTC with whole-microsecond precision, the resolution durable stores (PostgreSQL) keep.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    /// <summary>True for 1-<paramref name="maximumLength"/> characters of a-z, 0-9, '-', '_' or '.', starting with a letter or digit.</summary>
    public static bool IsToken(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && Utf16.IsValid(value) && (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0]))
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.');

    public static string RequireToken(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"Values must be 1-{maximumLength} characters of a-z, 0-9, '-', '_' or '.', starting with a letter or digit.", parameterName);
        return value;
    }
}
