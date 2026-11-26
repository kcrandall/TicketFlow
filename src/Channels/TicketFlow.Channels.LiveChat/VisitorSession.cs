using TicketFlow.Core;
using TicketFlow.Realtime;

namespace TicketFlow.Channels.LiveChat;

public enum VisitorVerificationMethod
{
    /// <summary>HMAC-SHA256 of the user identifier with the inbox secret, computed by the application's backend.</summary>
    Hmac = 1,

    /// <summary>A signed token accepted by the registered <see cref="IVisitorTokenValidator"/>.</summary>
    SignedToken = 2,
}

/// <summary>A verified visitor identity: becomes a verified <c>ExternalUser</c> contact identity (namespace, value). Personal data: never logged.</summary>
public sealed record VerifiedVisitorIdentity
{
    public VerifiedVisitorIdentity(string @namespace, string value, VisitorVerificationMethod method, DateTimeOffset verifiedAt)
    {
        Namespace = LiveChatText.RequireToken(@namespace, 64, nameof(@namespace));
        Value = LiveChatText.RequireLine(value, 256, nameof(value));
        if (!Enum.IsDefined(method))
            throw new ArgumentOutOfRangeException(nameof(method));
        Method = method;
        VerifiedAt = LiveChatText.Instant(verifiedAt);
    }

    public string Namespace { get; }
    public string Value { get; }
    public VisitorVerificationMethod Method { get; }
    public DateTimeOffset VerifiedAt { get; }

    public override string ToString() => $"VerifiedVisitorIdentity {{ Method = {Method}, <redacted> }}";
}

/// <summary>
/// A live-chat visitor session. Only SHA-256 hashes of the session token are kept (<see cref="TokenHash"/> and, during the rotation
/// grace window, <see cref="PreviousTokenHash"/> until <see cref="PreviousTokenValidUntil"/>). <see cref="VisitorId"/> is the anonymous
/// visitor identifier (the claimed <c>WidgetVisitor</c> identity and the conversation thread key). Display name, pre-chat answers and
/// identity are personal data: never logged.
/// </summary>
public sealed record VisitorSession
{
    public const int TokenHashLength = 32;
    public const int MaximumPrechatAnswers = LiveChatWidget.MaximumPrechatFields;

    public VisitorSession(
        VisitorSessionId id,
        string configurationId,
        InboxId inbox,
        string visitorId,
        ReadOnlyMemory<byte> tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset tokenIssuedAt,
        DateTimeOffset expiresAt,
        ReadOnlyMemory<byte>? previousTokenHash = null,
        DateTimeOffset? previousTokenValidUntil = null,
        VerifiedVisitorIdentity? identity = null,
        string? displayName = null,
        IReadOnlyDictionary<string, string>? prechat = null,
        bool revoked = false)
    {
        Id = id.IsInitialized ? id : throw new ArgumentException("A session identifier is required.", nameof(id));
        ConfigurationId = TicketScope.RequireIdentifier(configurationId, nameof(configurationId));
        Inbox = inbox == default ? throw new ArgumentException("An inbox identifier is required.", nameof(inbox)) : inbox;
        VisitorId = TicketScope.RequireIdentifier(visitorId, nameof(visitorId));
        if (tokenHash.Length != TokenHashLength)
            throw new ArgumentException("Token hashes are 32 bytes.", nameof(tokenHash));
        TokenHash = tokenHash.ToArray();
        if (previousTokenHash.HasValue != previousTokenValidUntil.HasValue || previousTokenHash is { Length: not TokenHashLength })
            throw new ArgumentException("A previous token hash (32 bytes) and its validity end are supplied together.", nameof(previousTokenHash));
        PreviousTokenHash = previousTokenHash is { } previousHash ? new ReadOnlyMemory<byte>(previousHash.ToArray()) : (ReadOnlyMemory<byte>?)null;
        PreviousTokenValidUntil = previousTokenValidUntil is { } until ? LiveChatText.Instant(until) : null;
        CreatedAt = LiveChatText.Instant(createdAt);
        TokenIssuedAt = LiveChatText.Instant(tokenIssuedAt);
        ExpiresAt = LiveChatText.Instant(expiresAt);
        if (ExpiresAt <= CreatedAt || TokenIssuedAt < CreatedAt)
            throw new ArgumentException("A session expires after it is created and tokens are issued after creation.", nameof(expiresAt));
        Identity = identity;
        DisplayName = LiveChatText.OptionalLine(displayName, 256, nameof(displayName));
        var answers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in prechat ?? new Dictionary<string, string>(StringComparer.Ordinal))
            answers[LiveChatText.RequireToken(key, LiveChatPrechatField.MaximumKeyLength, nameof(prechat))] = LiveChatText.RequireLine(value, LiveChatPrechatField.MaximumAnswerLength, nameof(prechat));
        if (answers.Count > MaximumPrechatAnswers)
            throw new ArgumentException($"At most {MaximumPrechatAnswers} pre-chat answers are allowed.", nameof(prechat));
        Prechat = answers;
        Revoked = revoked;
    }

    public VisitorSessionId Id { get; }
    public string ConfigurationId { get; }
    public InboxId Inbox { get; }
    public string VisitorId { get; }
    public ReadOnlyMemory<byte> TokenHash { get; }
    public ReadOnlyMemory<byte>? PreviousTokenHash { get; }
    public DateTimeOffset? PreviousTokenValidUntil { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset TokenIssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public VerifiedVisitorIdentity? Identity { get; }
    public string? DisplayName { get; }

    /// <summary>Pre-chat answers by field key (ordinal order).</summary>
    public IReadOnlyDictionary<string, string> Prechat { get; }

    public bool Revoked { get; }

    public bool IsLive(DateTimeOffset now) => !Revoked && now < ExpiresAt;

    public VisitorSession With(
        ReadOnlyMemory<byte>? tokenHash = null,
        DateTimeOffset? tokenIssuedAt = null,
        DateTimeOffset? expiresAt = null,
        (ReadOnlyMemory<byte> Hash, DateTimeOffset ValidUntil)? previous = null,
        VerifiedVisitorIdentity? identity = null,
        string? displayName = null,
        IReadOnlyDictionary<string, string>? prechat = null,
        bool? revoked = null) =>
        new(Id, ConfigurationId, Inbox, VisitorId, tokenHash ?? TokenHash, CreatedAt, tokenIssuedAt ?? TokenIssuedAt, expiresAt ?? ExpiresAt,
            previous is { } p ? p.Hash : PreviousTokenHash, previous is { } q ? q.ValidUntil : PreviousTokenValidUntil,
            identity ?? Identity, displayName ?? DisplayName, prechat ?? Prechat, revoked ?? Revoked);

    public bool Equals(VisitorSession? other) =>
        other is not null && Id == other.Id && string.Equals(ConfigurationId, other.ConfigurationId, StringComparison.Ordinal) && Inbox == other.Inbox && string.Equals(VisitorId, other.VisitorId
, StringComparison.Ordinal) && TokenHash.Span.SequenceEqual(other.TokenHash.Span)
        && (PreviousTokenHash is null ? other.PreviousTokenHash is null : other.PreviousTokenHash is { } theirs && PreviousTokenHash.Value.Span.SequenceEqual(theirs.Span))
        && PreviousTokenValidUntil == other.PreviousTokenValidUntil && CreatedAt == other.CreatedAt && TokenIssuedAt == other.TokenIssuedAt && ExpiresAt == other.ExpiresAt
        && Identity == other.Identity && string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) && Prechat.SequenceEqual(other.Prechat) && Revoked == other.Revoked;

    public override int GetHashCode() => HashCode.Combine(Id, ExpiresAt, Revoked);

    public override string ToString() => "VisitorSession { <redacted> }";
}

/// <summary>A session with its full scope and revision (first revision is 1).</summary>
public sealed record StoredVisitorSession(TicketScope Scope, VisitorSession Session, long Revision);

/// <summary>A conversation linked to a session (created from the session's messages) and the visitor's read cursor (0 = nothing read).</summary>
public sealed record VisitorConversationLink(VisitorSessionId Session, ConversationId Conversation, DateTimeOffset LinkedAt, long ReadSequence);
