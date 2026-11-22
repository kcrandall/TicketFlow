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
    public static VisitorSessionId New() => new("vs_" + RealtimeText.RandomToken(16));

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
        var output = new char[(data.Length * 8 + 4) / 5];
        int buffer = 0, bits = 0, index = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                output[index++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }
        if (bits > 0)
            output[index++] = Alphabet[(buffer << (5 - bits)) & 31];
        return new string(output, 0, index);
    }
}
