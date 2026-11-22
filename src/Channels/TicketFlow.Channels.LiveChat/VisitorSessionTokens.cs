using System.Security.Cryptography;
using System.Text;
using TicketFlow.Realtime;

namespace TicketFlow.Channels.LiveChat;

/// <summary>A freshly issued session token (shown once to the visitor) and the hash to store.</summary>
public sealed record IssuedVisitorToken(string Token, ReadOnlyMemory<byte> Hash)
{
    public override string ToString() => "IssuedVisitorToken { <redacted> }";
}

/// <summary>
/// Session token format <c>tfvs1.{sessionId}.{secret}</c>: the secret is 256 random bits (lowercase base32). Stores keep only
/// SHA-256 over the secret; verification compares hashes in constant time. The session id locates the record; it never authenticates.
/// </summary>
public static class VisitorSessionTokens
{
    public const string Prefix = "tfvs1.";
    public const int MaximumTokenLength = 400;
    private const int SecretBytes = 32;

    public static IssuedVisitorToken Issue(VisitorSessionId session)
    {
        if (!session.IsInitialized || session.Value.Contains('.'))
            throw new ArgumentException("Session identifiers used in tokens cannot contain '.'.", nameof(session));
        var secret = RealtimeText.RandomToken(SecretBytes);
        return new IssuedVisitorToken($"{Prefix}{session.Value}.{secret}", Hash(secret));
    }

    /// <summary>Splits a token into its session id and secret hash, or <see langword="null"/> when malformed.</summary>
    public static (VisitorSessionId Session, byte[] SecretHash)? Parse(string? token)
    {
        if (token is null || token.Length > MaximumTokenLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var rest = token.AsSpan(Prefix.Length);
        var dot = rest.LastIndexOf('.');
        if (dot <= 0 || dot == rest.Length - 1)
            return null;
        var id = rest[..dot].ToString();
        var secret = rest[(dot + 1)..].ToString();
        if (id.Contains('.') || secret.Length != 52 || !secret.All(character => character is (>= 'a' and <= 'z') or (>= '2' and <= '7')))
            return null;
        try
        {
            return (new VisitorSessionId(id), Hash(secret));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="secretHash"/> matches the session's current token hash, or its previous hash within the rotation grace
    /// window. Both comparisons always run (constant time).
    /// </summary>
    public static bool Verify(VisitorSession session, ReadOnlySpan<byte> secretHash, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        var current = CryptographicOperations.FixedTimeEquals(session.TokenHash.Span, secretHash);
        var previousHash = session.PreviousTokenHash ?? session.TokenHash;
        var previous = CryptographicOperations.FixedTimeEquals(previousHash.Span, secretHash);
        var previousLive = session.PreviousTokenHash is not null && session.PreviousTokenValidUntil is { } until && now < until;
        return current | (previous & previousLive);
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));
}
