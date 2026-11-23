using System.Security.Cryptography;
using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>
/// Validated opaque identifier of a live-chat visitor session (unique per partition). It identifies, never authenticates: a
/// session is proven only by its secret token. <c>default</c> is uninitialized and rejected where an identifier is required.
/// </summary>
public readonly record struct VisitorSessionId
{
    private readonly string? value;

    public VisitorSessionId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The VisitorSessionId is uninitialized.");

    public bool IsInitialized => value is not null;

    /// <summary>A new random identifier (<c>vs_</c> + 128 random bits, base32 lowercase).</summary>
    public static VisitorSessionId New()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[3 + 26];
        chars[0] = 'v';
        chars[1] = 's';
        chars[2] = '_';
        var written = RealtimeText.EncodeBase32(random, chars[3..]);
        return new VisitorSessionId(new string(chars[..(3 + written)]));
    }

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Bounded text and random token helpers shared by realtime packages.</summary>
public static class RealtimeText
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>A lowercase RFC 4648 base32 (unpadded) rendering of <paramref name="bytes"/> cryptographically random bytes.</summary>
    public static string RandomToken(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, 64);
        Span<byte> buffer = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Base32(buffer);
    }

    /// <summary>Lowercase unpadded base32.</summary>
    public static string Base32(ReadOnlySpan<byte> data)
    {
        var len = (data.Length * 8 + 4) / 5;
        Span<char> output = len <= 256 ? stackalloc char[len] : new char[len];
        var written = EncodeBase32(data, output);
        return new string(output[..written]);
    }

    internal static int EncodeBase32(ReadOnlySpan<byte> data, Span<char> output)
    {
        int buffer = 0, bits = 0, index = 0;
        for (var i = 0; i < data.Length; i++)
        {
            buffer = (buffer << 8) | data[i];
            bits += 8;
            while (bits >= 5)
            {
                output[index++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }
        if (bits > 0)
            output[index++] = Alphabet[(buffer << (5 - bits)) & 31];
        return index;
    }
}
