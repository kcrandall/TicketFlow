using System.Security.Cryptography;
using System.Text;
using TicketFlow.Core;

namespace TicketFlow.Channels.LiveChat;

/// <summary>HMAC identity verification (the application backend signs the user identifier with the inbox secret).</summary>
public sealed record HmacVisitorIdentification(string UserId, string Signature, string? DisplayName = null)
{
    public override string ToString() => "HmacVisitorIdentification { <redacted> }";
}

/// <summary>A signed identity token (for example an IdentityFlow token) checked by <see cref="IVisitorTokenValidator"/>.</summary>
public sealed record SignedTokenVisitorIdentification(string Token)
{
    public override string ToString() => "SignedTokenVisitorIdentification { <redacted> }";
}

/// <summary>How a visitor proves its identity. Switch over every case without a discard arm.</summary>
public union VisitorIdentification(HmacVisitorIdentification, SignedTokenVisitorIdentification);

/// <summary>
/// HMAC-SHA256 identity hashes: <c>hex(HMAC-SHA256(inboxSecret, UTF-8(userId)))</c>, lowercase (uppercase accepted). Secrets are
/// 32-256 bytes, resolved by the application and never stored by TicketFlow.
/// </summary>
public static class VisitorIdentityHmac
{
    public static string Compute(ReadOnlySpan<byte> secret, string userId)
    {
        RequireSecret(secret);
        ArgumentNullException.ThrowIfNull(userId);
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(userId)));
    }

    /// <summary>Constant-time verification of <paramref name="signature"/> (64 hex characters).</summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string userId, string? signature)
    {
        RequireSecret(secret);
        ArgumentNullException.ThrowIfNull(userId);
        if (signature is null || signature.Length != 64)
            return false;
        Span<byte> provided = stackalloc byte[32];
        if (Convert.FromHexString(signature, provided, out _, out var written) != System.Buffers.OperationStatus.Done || written != 32)
            return false;
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(userId), expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private static void RequireSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length is < 32 or > 256)
            throw new ArgumentException("Identity secrets are 32-256 bytes.", nameof(secret));
    }
}

/// <summary>Resolves the HMAC identity secret of a widget configuration (application-owned; never stored by TicketFlow).</summary>
public interface ILiveChatIdentitySecretResolver
{
    /// <summary>The secret (32-256 bytes), or <see langword="null"/> when HMAC verification is not configured.</summary>
    Task<ReadOnlyMemory<byte>?> ResolveAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default);
}

/// <summary>A token accepted: the verified subject in an identity namespace (for example the issuer).</summary>
public sealed record VisitorTokenAccepted
{
    public VisitorTokenAccepted(string @namespace, string subject, string? displayName = null)
    {
        Namespace = LiveChatText.RequireToken(@namespace, 64, nameof(@namespace));
        Subject = LiveChatText.RequireLine(subject, 256, nameof(subject));
        DisplayName = LiveChatText.OptionalLine(displayName, 256, nameof(displayName));
    }

    public string Namespace { get; }
    public string Subject { get; }
    public string? DisplayName { get; }

    public override string ToString() => "VisitorTokenAccepted { <redacted> }";
}

public sealed record VisitorTokenRejected
{
    private VisitorTokenRejected()
    {
    }

    public static VisitorTokenRejected Instance { get; } = new();
}

/// <summary>Outcome of signed-token validation. Switch over every case without a discard arm.</summary>
public union VisitorTokenValidation(VisitorTokenAccepted, VisitorTokenRejected);

/// <summary>
/// Validates signed visitor identity tokens (signature, issuer, audience, expiry). The IdentityFlow adapter (E11) implements it;
/// applications may supply their own. Implementations must not log tokens.
/// </summary>
public interface IVisitorTokenValidator
{
    Task<VisitorTokenValidation> ValidateAsync(TicketScope scope, string configurationId, string token, CancellationToken cancellationToken = default);
}
