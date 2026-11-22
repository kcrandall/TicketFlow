using System.Text.Unicode;

namespace TicketFlow.Channels.LiveChat;

/// <summary>Bounded text validation for live-chat inputs (no control characters other than tab/CR/LF where noted, well-formed UTF-16).</summary>
public static class LiveChatText
{
    /// <summary>UTC, truncated to whole microseconds (the precision durable stores keep).</summary>
    public static DateTimeOffset Instant(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    public static string RequireLine(string? value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Trim().Length == 0 || value.Length > maximumLength || !IsClean(value, allowNewLines: false))
            throw new ArgumentException($"The value must be 1-{maximumLength} characters without control characters.", parameterName);
        return value;
    }

    public static string? OptionalLine(string? value, int maximumLength, string parameterName) => value is null ? null : RequireLine(value, maximumLength, parameterName);

    public static string RequireMessage(string? value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Trim().Length == 0 || value.Length > maximumLength || !IsClean(value, allowNewLines: true))
            throw new ArgumentException($"The value must be 1-{maximumLength} characters without control characters.", parameterName);
        return value;
    }

    /// <summary>1-<paramref name="maximumLength"/> characters of a-z, 0-9, '-' or '_', starting with a letter.</summary>
    public static bool IsToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.IsEmpty || value.Length > maximumLength || value[0] is not (>= 'a' and <= 'z'))
            return false;
        foreach (var character in value)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        return true;
    }

    public static string RequireToken(string? value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"The value must be 1-{maximumLength} characters of a-z, 0-9, '-' or '_', starting with a letter.", parameterName);
        return value;
    }

    /// <summary>Printable ASCII without spaces (client message ids, media types).</summary>
    public static bool IsPrintableAscii(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.IsEmpty || value.Length > maximumLength)
            return false;
        foreach (var character in value)
            if (character is < '!' or > '~')
                return false;
        return true;
    }

    public static bool IsClean(ReadOnlySpan<char> value, bool allowNewLines)
    {
        foreach (var character in value)
        {
            if (allowNewLines && character is '\t' or '\r' or '\n')
                continue;
            if (character <= '\u001F' || character is >= '\u007F' and <= '\u009F')
                return false;
        }
        return Utf16.IsValid(value);
    }
}
