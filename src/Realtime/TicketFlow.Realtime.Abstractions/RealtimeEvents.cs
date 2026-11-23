using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>An agent (user principal) acting on a conversation. Visitors never see the agent's identifier.</summary>
public sealed record AgentActor(UserId Agent);

/// <summary>The conversation's visitor (live-chat contact).</summary>
public sealed record VisitorActor
{
    private VisitorActor()
    {
    }

    public static VisitorActor Instance { get; } = new();
}

/// <summary>The support side without a disclosed identity (API clients, or agents as seen by visitors).</summary>
public sealed record SupportActor
{
    private SupportActor()
    {
    }

    public static SupportActor Instance { get; } = new();
}

/// <summary>Who typed or read. Switch over every case without a discard arm.</summary>
public union RealtimeActor(AgentActor, VisitorActor, SupportActor);

/// <summary>A realtime event. Switch over every case without a discard arm.</summary>
public closed record RealtimeEvent(TicketScope Scope)
{
    public TicketScope Scope { get; init; } = Scope ?? throw new ArgumentNullException(nameof(Scope));

    /// <summary>The conversation the event is about, if any (used to authorize agent deliveries against the current conversation).</summary>
    public ConversationId? TargetConversation => this switch
    {
        ConversationChangedEvent changed => changed.Conversation,
        PresenceChangedEvent presence => presence.Conversation,
        ReplyLockChangedEvent replyLock => replyLock.Conversation,
        AgentStatusChangedEvent => null,
        ConversationAssignedEvent assigned => assigned.Conversation,
        QueueChangedEvent queue => queue.Conversation,
        TypingEvent typing => typing.Conversation,
        ReadReceiptEvent read => read.Conversation,
        VisitorMessageEvent message => message.Conversation,
        VisitorSessionEndedEvent => null,
    };
}

/// <summary>
/// A conversation outbox change as a notification: identifiers, revision, change kind and event summaries (sequence, kind, message
/// id, direction), never bodies, attribute values or masked fields. Clients refetch details through the Access-checked conversation
/// service. Note summaries are removed for subscribers without <c>note:read</c>.
/// </summary>
public sealed record ConversationChangedEvent : RealtimeEvent
{
    public ConversationChangedEvent(TicketScope scope, ConversationId conversation, InboxId inbox, long revision, ConversationChangeKind kind,
        IEnumerable<ConversationEventSummary> events, MessageId? message, MessageDeliveryState? delivery, DateTimeOffset occurredAt)
        : base(scope)
    {
        if (conversation == default || inbox == default)
            throw new ArgumentException("Conversation and inbox identifiers are required.");
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Events = [.. events ?? throw new ArgumentNullException(nameof(events))];
        if (Events.Count > ConversationChange.MaximumEvents || Events.Any(summary => summary is null))
            throw new ArgumentException($"At most {ConversationChange.MaximumEvents} event summaries are allowed.", nameof(events));
        Conversation = conversation;
        Inbox = inbox;
        Revision = revision;
        Kind = kind;
        Message = message;
        Delivery = delivery;
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public long Revision { get; }
    public ConversationChangeKind Kind { get; }
    public IReadOnlyList<ConversationEventSummary> Events { get; }
    public MessageId? Message { get; }
    public MessageDeliveryState? Delivery { get; }
    public DateTimeOffset OccurredAt { get; }

    public static ConversationChangedEvent From(ConversationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new(change.Scope, change.Conversation, change.Inbox, change.Revision, change.Kind, change.Events, change.Message, change.Delivery, change.OccurredAt);
    }

    /// <summary>A copy with only <paramref name="events"/>.</summary>
    public ConversationChangedEvent WithEvents(IEnumerable<ConversationEventSummary> events) =>
        new(Scope, Conversation, Inbox, Revision, Kind, events, Message, Delivery, OccurredAt);

    public bool Equals(ConversationChangedEvent? other) =>
        other is not null && Scope == other.Scope && Conversation == other.Conversation && Inbox == other.Inbox && Revision == other.Revision && Kind == other.Kind
        && Events.SequenceEqual(other.Events) && Message == other.Message && Delivery == other.Delivery && OccurredAt == other.OccurredAt;

    public override int GetHashCode() => HashCode.Combine(Conversation, Revision, Kind);

    public override string ToString() => $"ConversationChangedEvent {{ Conversation = {Conversation}, Revision = {Revision}, Kind = {Kind} }}";
}

/// <summary>An agent's activity on a conversation (mirrors routing collision activities without depending on routing).</summary>
public enum RealtimeActivity
{
    Viewing = 1,
    Composing = 2,
}

/// <summary>An agent's availability (mirrors routing agent status without depending on routing).</summary>
public enum RealtimeAgentStatus
{
    Online = 1,
    Busy = 2,
    Away = 3,
    Offline = 4,
}

/// <summary>An agent started, changed or ended (<see cref="Activity"/> <see langword="null"/>) presence on a conversation (E8).</summary>
public sealed record PresenceChangedEvent(TicketScope Scope, ConversationId Conversation, UserId User, RealtimeActivity? Activity, DateTimeOffset At) : RealtimeEvent(Scope);

/// <summary>A soft reply lock was acquired, renewed or released (<see cref="Holder"/> <see langword="null"/>) (E8).</summary>
public sealed record ReplyLockChangedEvent(TicketScope Scope, ConversationId Conversation, UserId? Holder, DateTimeOffset? ExpiresAt, DateTimeOffset At) : RealtimeEvent(Scope);

/// <summary>An agent's routing status changed (E8). Delivered on the agent's own topic only.</summary>
public sealed record AgentStatusChangedEvent(TicketScope Scope, UserId Agent, RealtimeAgentStatus Status, DateTimeOffset At) : RealtimeEvent(Scope);

/// <summary>The routing engine assigned a conversation to <see cref="Agent"/> at <see cref="Revision"/> (E8).</summary>
public sealed record ConversationAssignedEvent(TicketScope Scope, ConversationId Conversation, UserId Agent, long Revision, DateTimeOffset At) : RealtimeEvent(Scope);

public enum QueueChange
{
    /// <summary>An overflow rule moved the waiting conversation.</summary>
    Overflowed = 1,

    /// <summary>The conversation waited longer than its policy's maximum wait.</summary>
    WaitExceeded = 2,

    /// <summary>The conversation was queued for routing.</summary>
    Queued = 3,
}

/// <summary>A queue-level routing change of a waiting conversation (E8).</summary>
public sealed record QueueChangedEvent(TicketScope Scope, ConversationId Conversation, InboxId Inbox, QueueChange Change, DateTimeOffset At) : RealtimeEvent(Scope);

/// <summary>Ephemeral typing indicator (never stored).</summary>
public sealed record TypingEvent(TicketScope Scope, ConversationId Conversation, RealtimeActor Actor, bool Typing, DateTimeOffset At) : RealtimeEvent(Scope);

/// <summary>Read receipt: <see cref="Actor"/> has read the conversation up to <see cref="UpToSequence"/>.</summary>
public sealed record ReadReceiptEvent(TicketScope Scope, ConversationId Conversation, RealtimeActor Actor, long UpToSequence, DateTimeOffset At) : RealtimeEvent(Scope);

/// <summary>
/// A visitor-facing message of a live-chat conversation (an agent reply pushed to the visitor, or the visitor's own message echoed
/// to their other tabs). Delivered only to the session's visitor connections, never to agents. Customer content: never logged.
/// </summary>
public sealed record VisitorMessageEvent : RealtimeEvent
{
    public VisitorMessageEvent(TicketScope scope, VisitorSessionId session, ConversationId conversation, MessageId message, long sequence, MessageDirection direction,
        string body, MessageContentType contentType, IEnumerable<AttachmentRef>? attachments, DateTimeOffset at)
        : base(scope)
    {
        if (!session.IsInitialized || conversation == default || message == default)
            throw new ArgumentException("Session, conversation and message identifiers are required.");
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        if (!Enum.IsDefined(direction) || !Enum.IsDefined(contentType))
            throw new ArgumentOutOfRangeException(nameof(direction));
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length > ConversationMessageLimits.MaximumBodyBytes)
            throw new ArgumentException("The body exceeds message limits.", nameof(body));
        Session = session;
        Conversation = conversation;
        Message = message;
        Sequence = sequence;
        Direction = direction;
        Body = body;
        ContentType = contentType;
        Attachments = [.. attachments ?? []];
        if (Attachments.Count > ConversationMessageLimits.MaximumAttachments || Attachments.Any(attachment => attachment is null))
            throw new ArgumentException("Too many attachments.", nameof(attachments));
        At = at.ToUniversalTime();
    }

    public VisitorSessionId Session { get; }
    public ConversationId Conversation { get; }
    public MessageId Message { get; }
    public long Sequence { get; }
    public MessageDirection Direction { get; }
    public string Body { get; }
    public MessageContentType ContentType { get; }
    public IReadOnlyList<AttachmentRef> Attachments { get; }
    public DateTimeOffset At { get; }

    public override string ToString() => $"VisitorMessageEvent {{ Conversation = {Conversation}, Message = {Message}, <redacted> }}";
}

/// <summary>Why a visitor session stopped (pushed to the session's connections before they are closed by the host).</summary>
public enum VisitorSessionEnd
{
    Revoked = 1,
    Expired = 2,
}

/// <summary>A live-chat session ended; visitor connections should stop.</summary>
public sealed record VisitorSessionEndedEvent(TicketScope Scope, VisitorSessionId Session, VisitorSessionEnd Reason, DateTimeOffset At) : RealtimeEvent(Scope);

/// <summary>One event addressed to one topic. The event's scope must equal the topic's.</summary>
public sealed record RealtimePublication
{
    public RealtimePublication(RealtimeTopic topic, RealtimeEvent realtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(realtimeEvent);
        if (topic.Scope != realtimeEvent.Scope)
            throw new ArgumentException("The event belongs to a different scope than the topic.", nameof(realtimeEvent));
        Topic = topic;
        Event = realtimeEvent;
    }

    public RealtimeTopic Topic { get; }
    public RealtimeEvent Event { get; }

    public override string ToString() => $"RealtimePublication {{ Topic = {Topic}, Event = {Event.GetType().Name} }}";
}
