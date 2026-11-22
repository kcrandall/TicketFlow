using System.Text.Unicode;

namespace TicketFlow.Routing;

/// <summary>Shared validation for bounded routing tokens, names, durations and timestamps.</summary>
internal static class RoutingText
{
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

    public static string RequireText(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-blank value is required.", parameterName);
        if (value.Length > maximumLength || value.AsSpan().ContainsAnyInRange('\0', '\u001F') || value.AsSpan().ContainsAnyInRange('\u007F', '\u009F')
            || !Utf16.IsValid(value))
            throw new ArgumentException($"Values must contain at most {maximumLength} characters, no control characters and well-formed Unicode.", parameterName);
        return value;
    }

    /// <summary>A positive whole-second duration of at most <paramref name="maximum"/>.</summary>
    public static TimeSpan RequireDuration(TimeSpan value, TimeSpan maximum, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > maximum || value.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(parameterName, $"Durations are whole seconds, positive and at most {maximum}.");
        return value;
    }

    /// <summary>UTC with microsecond precision, matching PostgreSQL <c>timestamptz</c>.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static DateTimeOffset? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;
}
