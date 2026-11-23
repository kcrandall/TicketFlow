using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Channels.LiveChat;

public enum LiveChatRefusal
{
    /// <summary>The session token is malformed, unknown, expired, revoked or rotated out.</summary>
    Unauthenticated = 1,

    /// <summary>The request's Origin is not an allowed origin of the widget.</summary>
    OriginNotAllowed = 2,

    /// <summary>No such widget, or no such conversation linked to the session.</summary>
    NotFound = 3,

    Invalid = 4,

    /// <summary>Required pre-chat fields are unanswered.</summary>
    PrechatRequired = 5,

    /// <summary>The widget requires a verified identity before messages.</summary>
    IdentityRequired = 6,

    /// <summary>The identity proof was rejected (or the method is disabled or unconfigured).</summary>
    IdentityRejected = 7,

    /// <summary>The inbox is disabled, or the feature (deflection, attachments) is off for the widget.</summary>
    Unavailable = 8,

    /// <summary>A store limit or concurrent writers refused the write; retry.</summary>
    Rejected = 9,

    TooLarge = 10,

    UnsupportedMediaType = 11,

    /// <summary>Access denied the principal (administration and agent reads).</summary>
    Forbidden = 12,

    /// <summary>The expected revision did not match.</summary>
    Conflict = 13,
}

/// <summary>A refused live-chat operation. <see cref="Errors"/> are non-sensitive.</summary>
public sealed record LiveChatRefused(LiveChatRefusal Reason, IReadOnlyList<string> Errors)
{
    public static LiveChatRefused Of(LiveChatRefusal reason, string? error = null) => new(reason, error is null ? [] : [error]);
}

/// <summary>What the widget needs to render: theme, pre-chat form, greeting, business-hours state and away message, features.</summary>
public sealed record LiveChatWidgetState(
    string ConfigurationId,
    string? ThemeRef,
    IReadOnlyList<LiveChatPrechatField> PrechatFields,
    string? GreetingMessage,
    bool Online,
    string? AwayMessage,
    bool DeflectionEnabled,
    LiveChatAttachmentPolicy? Attachments,
    bool RequireVerifiedIdentity,
    bool AllowHmac,
    bool AllowSignedToken);

/// <summary>
/// An authenticated visitor session, obtainable only from <see cref="ILiveChatVisitorService"/> (start or token authentication).
/// Visitor operations re-check that the session is still live.
/// </summary>
public sealed record VisitorSessionContext
{
    internal VisitorSessionContext(StoredVisitorSession session, bool rotationRecommended)
    {
        Session = session;
        RotationRecommended = rotationRecommended;
    }

    public StoredVisitorSession Session { get; }
    public TicketScope Scope => Session.Scope;

    /// <summary>The token is older than <see cref="LiveChatOptions.TokenRotationInterval"/>; call <see cref="ILiveChatVisitorService.RotateTokenAsync"/>.</summary>
    public bool RotationRecommended { get; }

    public override string ToString() => "VisitorSessionContext { <redacted> }";
}

/// <summary>A new session: the token is returned exactly once.</summary>
public sealed record VisitorSessionGrant(VisitorSessionContext Context, string Token, LiveChatWidgetState Widget)
{
    public override string ToString() => "VisitorSessionGrant { <redacted> }";
}

/// <summary>
/// A rotated token (the previous one stays valid for the grace window). <see cref="Token"/> is <see langword="null"/> when rotation was
/// skipped because the session's token had already been rotated recently (for example by another tab); keep the current token.
/// </summary>
public sealed record VisitorTokenRotated(VisitorSessionContext Context, string? Token)
{
    public override string ToString() => "VisitorTokenRotated { <redacted> }";
}

/// <summary>A visitor message. <see cref="ClientMessageId"/> (1-64 printable ASCII) makes retries idempotent per session.</summary>
public sealed record VisitorMessageDraft(string ClientMessageId, string? Text, IReadOnlyList<string>? AttachmentRefs = null)
{
    public override string ToString() => "VisitorMessageDraft { <redacted> }";
}

public sealed record VisitorMessageAccepted(ConversationId Conversation, MessageId Message, long Sequence, bool StartedConversation, bool Replayed);

/// <summary>A visitor-visible message (incoming or outgoing; never notes or system events). Customer content: never logged.</summary>
public sealed record VisitorMessageView(
    ConversationId Conversation,
    MessageId Message,
    long Sequence,
    MessageDirection Direction,
    string Body,
    MessageContentType ContentType,
    IReadOnlyList<AttachmentRef> Attachments,
    DateTimeOffset At,
    MessageDeliveryState Delivery)
{
    public override string ToString() => $"VisitorMessageView {{ Message = {Message}, Sequence = {Sequence} }}";
}

/// <summary>Messages after a cursor; <see cref="NextAfterSequence"/> continues the read (<see langword="null"/> at the end).</summary>
public sealed record VisitorMessagePage(ConversationId Conversation, IReadOnlyList<VisitorMessageView> Messages, long? NextAfterSequence, long ReadSequence);

public sealed record VisitorConversationView(ConversationId Conversation, bool Closed, long LastSequence, long ReadSequence, DateTimeOffset LinkedAt, DateTimeOffset LastActivityAt);

/// <summary>A pre-chat answer as shown to an agent; <see cref="Value"/> is <see langword="null"/> when masked.</summary>
public sealed record LiveChatPrechatAnswerView(string Key, string Label, string? Value, bool Masked)
{
    public override string ToString() => $"LiveChatPrechatAnswerView {{ Key = {Key}, Masked = {Masked} }}";
}

/// <summary>The live-chat visitor behind a conversation, masked for the reading agent.</summary>
public sealed record LiveChatVisitorView(
    string VisitorId,
    bool Verified,
    string? IdentityNamespace,
    string? IdentityValue,
    string? DisplayName,
    IReadOnlyList<LiveChatPrechatAnswerView> Prechat,
    DateTimeOffset SessionCreatedAt,
    IReadOnlyList<MaskedField> MaskedFields)
{
    public override string ToString() => "LiveChatVisitorView { <redacted> }";
}
