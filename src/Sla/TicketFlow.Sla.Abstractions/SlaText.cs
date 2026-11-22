using System.Text.Unicode;

namespace TicketFlow.Sla;

/// <summary>Shared validation for bounded SLA text and instants.</summary>
internal static class SlaText
{
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

    /// <summary>UTC with microsecond precision, matching PostgreSQL <c>timestamptz</c>.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static DateTimeOffset? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;

    /// <summary>A duration rounded down to whole microseconds.</summary>
    public static TimeSpan Micros(TimeSpan value) => TimeSpan.FromTicks(value.Ticks - value.Ticks % 10);

    public static T Defined<T>(T value, string parameterName) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(parameterName);
}
