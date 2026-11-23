using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace TicketFlow.Events;

/// <summary>Shared validation for bounded event values.</summary>
public static class EventText
{
    private static ReadOnlySpan<byte> EventIdPrefix => "tf-events-v1\n"u8;

    /// <summary>UTC with whole-microsecond precision, the resolution durable stores (PostgreSQL) keep.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static DateTimeOffset? Instant(DateTimeOffset? value) => value is { } instant ? Instant(instant) : null;

    /// <summary>True for 1-<paramref name="maximumLength"/> characters of a-z, 0-9, '-', '_' or '.', starting with a letter or digit.</summary>
    public static bool IsToken(string? value, int maximumLength) => value is not null && IsToken(value.AsSpan(), maximumLength);

    internal static bool IsToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.Length == 0 || value.Length > maximumLength)
            return false;
        if (!(char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])))
            return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.'))
                return false;
        }
        return true;
    }

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
        var span = value.AsSpan();
        if (string.IsNullOrWhiteSpace(value) || span.Length > maximumLength || span.ContainsAnyInRange('\0', '\u001F')
            || span.ContainsAnyInRange('\u007F', '\u009F') || (span.ContainsAnyInRange('\uD800', '\uDFFF') && !Utf16.IsValid(span)))
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
        var maxBytes = EventIdPrefix.Length + Encoding.UTF8.GetMaxByteCount(source.Length) + 1 + Encoding.UTF8.GetMaxByteCount(type.Length);
        byte[]? rented = null;
        Span<byte> buffer = maxBytes <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            var pos = 0;
            EventIdPrefix.CopyTo(buffer);
            pos += EventIdPrefix.Length;
            pos += Encoding.UTF8.GetBytes(source, buffer[pos..]);
            buffer[pos++] = (byte)'\n';
            pos += Encoding.UTF8.GetBytes(type, buffer[pos..]);
            return HashToGuid(buffer[..pos]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static Guid EventId(Guid source, string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        var maxBytes = EventIdPrefix.Length + 36 + 1 + Encoding.UTF8.GetMaxByteCount(type.Length);
        byte[]? rented = null;
        Span<byte> buffer = maxBytes <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            var pos = 0;
            EventIdPrefix.CopyTo(buffer);
            pos += EventIdPrefix.Length;
            source.TryFormat(buffer[pos..], out var guidBytes, "D");
            pos += guidBytes;
            buffer[pos++] = (byte)'\n';
            pos += Encoding.UTF8.GetBytes(type, buffer[pos..]);
            return HashToGuid(buffer[..pos]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static Guid HashToGuid(ReadOnlySpan<byte> payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
