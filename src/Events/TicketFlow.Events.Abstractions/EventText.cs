using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace TicketFlow.Events;

/// <summary>Shared validation for bounded event values.</summary>
public static class EventText
{
    /// <summary>UTC with whole-microsecond precision, the resolution durable stores (PostgreSQL) keep.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static DateTimeOffset? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;

    /// <summary>True for 1-<paramref name="maximumLength"/> characters of a-z, 0-9, '-', '_' or '.', starting with a letter or digit.</summary>
    public static bool IsToken(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0]))
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.');

    public static string RequireToken(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!IsToken(value, maximumLength))
            throw new ArgumentException($"Values must be 1-{maximumLength} characters of a-z, 0-9, '-', '_' or '.', starting with a letter or digit.", parameterName);
        return value;
    }

    /// <summary>Bounded free text: not blank, at most <paramref name="maximumLength"/> characters, no control characters, well-formed UTF-16.</summary>
    public static string RequireText(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.AsSpan().ContainsAnyInRange('\0', '\u001F')
            || value.AsSpan().ContainsAnyInRange('\u007F', '\u009F') || !Utf16.IsValid(value))
            throw new ArgumentException($"Values must be non-blank, at most {maximumLength} characters, without control characters and well-formed.", parameterName);
        return value;
    }

    /// <summary>
    /// A deterministic event identifier (an RFC 9562 version 8 UUID) for one source and event type: SHA-256 over
    /// <c>"tf-events-v1\n" + source + "\n" + type</c>. Redelivered or replayed sources therefore map to the same event id.
    /// </summary>
    public static Guid EventId(string source, string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(type);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("tf-events-v1\n" + source + "\n" + type), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static Guid EventId(Guid source, string type) => EventId(source.ToString("D"), type);
}
