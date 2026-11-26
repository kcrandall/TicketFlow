using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>An incoming message from the contact. <see cref="ExternalId"/> (the provider message id) makes recording idempotent per inbox.</summary>
public sealed record IncomingMessageDraft(
    string Body,
    MessageContentType ContentType = MessageContentType.PlainText,
    IReadOnlyList<AttachmentRef>? Attachments = null,
    string? ExternalId = null,
    MessageId? Id = null)
{
    public override string ToString() => "IncomingMessageDraft { <redacted> }";
}

/// <summary>An outgoing reply. It is recorded as Pending and dispatched by the channel layer from the outbox.</summary>
public sealed record OutgoingMessageDraft(
    string Body,
    MessageContentType ContentType = MessageContentType.PlainText,
    IReadOnlyList<AttachmentRef>? Attachments = null,
    MessageId? Id = null)
{
    public override string ToString() => "OutgoingMessageDraft { <redacted> }";
}

/// <summary>A private note.</summary>
public sealed record NoteDraft(string Body, IReadOnlyList<AttachmentRef>? Attachments = null, MessageId? Id = null)
{
    public override string ToString() => "NoteDraft { <redacted> }";
}

/// <summary>Input for starting a conversation. A <see langword="null"/> identifier is generated.</summary>
public sealed record StartConversationRequest(
    InboxId Inbox,
    ContactId Contact,
    ChannelThreadRef? ChannelThread = null,
    ConversationPriority Priority = ConversationPriority.None,
    IReadOnlyList<LabelId>? Labels = null,
    IReadOnlyDictionary<string, ConversationAttributeValue>? Attributes = null,
    TeamId? Team = null,
    UserId? Assignee = null,
    IncomingMessageDraft? InitialMessage = null,
    ConversationId? Id = null);

/// <summary>
/// A channel delivery report, correlated either by conversation and message identifier or by inbox and external (provider) message
/// identifier. <see cref="ProviderMessageId"/> records the provider identifier for an outgoing message that has none yet.
/// </summary>
public sealed record DeliveryStatusUpdate
{
    private DeliveryStatusUpdate(ConversationId? conversation, MessageId? message, InboxId? inbox, string? externalId, MessageDeliveryState state,
        string? failureCode, string? providerMessageId)
    {
        if (!Enum.IsDefined(state) || state is MessageDeliveryState.Pending or MessageDeliveryState.Received)
            throw new ArgumentOutOfRangeException(nameof(state), "Delivery reports move messages to Sent, Delivered, Read or Failed.");
        Conversation = conversation;
        Message = message;
        Inbox = inbox;
        ExternalId = ConversationMessageLimits.ExternalId(externalId, nameof(externalId));
        State = state;
        FailureCode = failureCode;
        ProviderMessageId = ConversationMessageLimits.ExternalId(providerMessageId, nameof(providerMessageId));
        _ = new MessageDelivery(state, DateTimeOffset.UnixEpoch, failureCode);
    }

    public ConversationId? Conversation { get; }
    public MessageId? Message { get; }
    public InboxId? Inbox { get; }
    public string? ExternalId { get; }
    public MessageDeliveryState State { get; }
    public string? FailureCode { get; }
    public string? ProviderMessageId { get; }

    public static DeliveryStatusUpdate ByMessage(ConversationId conversation, MessageId message, MessageDeliveryState state, string? failureCode = null, string? providerMessageId = null)
    {
        if (conversation == default || message == default)
            throw new ArgumentException("Conversation and message identifiers are required.", conversation == default ? nameof(conversation) : nameof(message));
        return new(conversation, message, null, null, state, failureCode, providerMessageId);
    }

    public static DeliveryStatusUpdate ByExternalId(InboxId inbox, string externalId, MessageDeliveryState state, string? failureCode = null)
    {
        if (inbox == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(inbox));
        ArgumentNullException.ThrowIfNull(externalId);
        return new(null, null, inbox, externalId, state, failureCode, null);
    }

    public override string ToString() => $"DeliveryStatusUpdate {{ State = {State} }}";
}

/// <summary>
/// The command applied (or, when <see cref="Replayed"/>, was already applied: a duplicate inbound message or a stale delivery report).
/// <see cref="Revision"/> is the conversation revision afterwards; <see cref="Events"/> the events appended by this command (for a
/// replayed inbound message, the existing message event).
/// </summary>
public sealed record ConversationApplied(ConversationId Conversation, long Revision, IReadOnlyList<TimelineEvent> Events, bool Replayed = false);

/// <summary>The conversation (or correlated message) does not exist in the actor's scope.</summary>
public sealed record ConversationNotFound
{
    private ConversationNotFound()
    {
    }

    public static ConversationNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match.</summary>
public sealed record ConversationConflict(long CurrentRevision);

public enum ConversationRefusal
{
    /// <summary>Access denied the operation.</summary>
    Forbidden = 1,

    /// <summary>The input is invalid; see <see cref="ConversationRefused.Errors"/>.</summary>
    Invalid = 2,

    /// <summary>The conversation is closed and accepts no further changes.</summary>
    Closed = 3,

    /// <summary>A store limit or contract rule refused the write, or concurrent writers kept winning.</summary>
    Rejected = 4,
}

/// <summary>The operation was refused. <see cref="Errors"/> are non-sensitive messages (no bodies, addresses or values).</summary>
public sealed record ConversationRefused(ConversationRefusal Reason, IReadOnlyList<string> Errors)
{
    public static ConversationRefused Forbidden { get; } = new(ConversationRefusal.Forbidden, []);
    public static ConversationRefused Closed { get; } = new(ConversationRefusal.Closed, ["The conversation is closed."]);

    public static ConversationRefused Invalid(string error) => new(ConversationRefusal.Invalid, [error]);
}

/// <summary>Outcome of a conversation command. Switch over every case without a discard arm.</summary>
public union ConversationCommandResult(ConversationApplied, ConversationNotFound, ConversationConflict, ConversationRefused);

/// <summary>The channel thread as shown to a principal; <see cref="ExternalThreadId"/> is <see langword="null"/> when masked.</summary>
public sealed record ChannelThreadView(string Channel, string? ExternalThreadId, bool Masked, bool Revealable)
{
    public override string ToString() => $"ChannelThreadView {{ Channel = {Channel}, Masked = {Masked} }}";
}

/// <summary>An attribute as shown to a principal. When <see cref="Masked"/>, <see cref="Value"/> is <see langword="null"/>.</summary>
public sealed record ConversationAttributeView(string Key, ConversationAttributeType Type, bool Sensitive, ConversationAttributeValue? Value, bool Masked, bool Revealable)
{
    public override string ToString() => $"ConversationAttributeView {{ Key = {Key}, Masked = {Masked} }}";
}

/// <summary>
/// A server-side masked projection of a conversation. Masked values are removed, not obfuscated; <see cref="MaskedFields"/> lists every
/// masked path (ordered). Attributes without a definition are omitted. Contacts are referenced by identifier only.
/// </summary>
public sealed record ConversationView(
    ConversationId Id,
    long Revision,
    InboxId Inbox,
    ContactId Contact,
    ConversationStatus Status,
    ConversationPriority Priority,
    UserId? Assignee,
    TeamId? Team,
    IReadOnlyList<LabelId> Labels,
    IReadOnlyList<ConversationAttributeView> Attributes,
    IReadOnlyList<UserId> Participants,
    ChannelThreadView? ChannelThread,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? WaitingSince,
    DateTimeOffset? FirstReplyAt,
    DateTimeOffset? LastIncomingAt,
    DateTimeOffset? LastOutgoingAt,
    long LastSequence,
    IReadOnlyList<MaskedField> MaskedFields)
{
    public override string ToString() => $"ConversationView {{ Id = {Id}, Revision = {Revision} }}";
}

public union ConversationReadResult(ConversationView, ConversationNotFound, ConversationRefused);

/// <summary>A timeline event as shown to a principal: masked attribute values and external identifiers are removed.</summary>
public sealed record TimelineEventView(TimelineEvent Event, IReadOnlyList<MaskedField> MaskedFields);

/// <summary>
/// One page of a timeline. <see cref="NextAfterSequence"/> continues the read (<see langword="null"/> at the end). Notes are present
/// only when <see cref="NotesIncluded"/> (the principal holds <c>note:read</c>), so sequences visible to others may skip numbers.
/// </summary>
public sealed record TimelinePage(ConversationId Conversation, IReadOnlyList<TimelineEventView> Events, long? NextAfterSequence, bool NotesIncluded);

public union TimelineReadResult(TimelinePage, ConversationNotFound, ConversationRefused);

/// <summary>One page of masked conversations; <see cref="NextCursor"/> is an opaque token bound to the query, <see langword="null"/> at the end.</summary>
public sealed record ConversationListPage(IReadOnlyList<ConversationView> Conversations, string? NextCursor);

public union ConversationListResult(ConversationListPage, ConversationRefused);

/// <summary>
/// Access-checked conversation commands and reads. Every read and mutation authorizes through <see cref="IAccessEvaluator"/>; list
/// queries put <see cref="IAccessQueryPlanner"/> filters into the store query; every view is masked server-side. Existence and
/// revision fences are checked before authorization decisions on stored records. Commands without an expected revision apply to the
/// current revision and retry concurrent-write conflicts; commands with one are fenced on it.
/// </summary>
public interface IConversationService
{
    /// <summary>Requires <c>conversation:create</c> on the new conversation. The inbox must exist and be enabled; the contact must exist and not be merged.</summary>
    Task<ConversationCommandResult> StartAsync(PrincipalRef actor, StartConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:ingest</c>. Idempotent by external id per inbox; reopens pending, snoozed and resolved conversations; refused when closed.</summary>
    Task<ConversationCommandResult> AppendIncomingAsync(PrincipalRef actor, ConversationId id, IncomingMessageDraft message, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:reply</c>. Records a Pending outgoing message and an outbox change for channel dispatch.</summary>
    Task<ConversationCommandResult> ReplyAsync(PrincipalRef actor, ConversationId id, OutgoingMessageDraft message, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>note:write</c>.</summary>
    Task<ConversationCommandResult> AddNoteAsync(PrincipalRef actor, ConversationId id, NoteDraft note, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:assign</c>; <see langword="null"/> unassigns.</summary>
    Task<ConversationCommandResult> AssignAsync(PrincipalRef actor, ConversationId id, UserId? assignee, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:assign</c> on the current and resulting conversation; the team must exist.</summary>
    Task<ConversationCommandResult> SetTeamAsync(PrincipalRef actor, ConversationId id, TeamId? team, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:resolve</c>. Closed is terminal; snoozing requires a future instant.</summary>
    Task<ConversationCommandResult> ChangeStatusAsync(PrincipalRef actor, ConversationId id, ConversationStatus status, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Shorthand for <see cref="ChangeStatusAsync"/> with <see cref="SnoozedStatus"/>.</summary>
    Task<ConversationCommandResult> SnoozeAsync(PrincipalRef actor, ConversationId id, DateTimeOffset until, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:manage</c>.</summary>
    Task<ConversationCommandResult> SetPriorityAsync(PrincipalRef actor, ConversationId id, ConversationPriority priority, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:manage</c> on the current and resulting conversation.</summary>
    Task<ConversationCommandResult> UpdateLabelsAsync(PrincipalRef actor, ConversationId id, IEnumerable<LabelId> add, IEnumerable<LabelId> remove, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:manage</c> on the current and resulting conversation. A <see langword="null"/> value removes the attribute; values must match their definitions.</summary>
    Task<ConversationCommandResult> UpdateAttributesAsync(PrincipalRef actor, ConversationId id, IReadOnlyDictionary<string, ConversationAttributeValue?> changes, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:manage</c>.</summary>
    Task<ConversationCommandResult> UpdateParticipantsAsync(PrincipalRef actor, ConversationId id, IEnumerable<UserId> add, IEnumerable<UserId> remove, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:ingest</c>. Idempotent and monotonic: stale or repeated reports are <see cref="ConversationApplied.Replayed"/>.</summary>
    Task<ConversationCommandResult> UpdateDeliveryStatusAsync(PrincipalRef actor, DeliveryStatusUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:manage</c>. Appends a feature-owned <see cref="ExtensionEvent"/>.</summary>
    Task<ConversationCommandResult> AppendExtensionAsync(PrincipalRef actor, ConversationId id, ExtensionEvent extension, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:delete</c> (sensitive, audited by Access). Deletes the conversation and its timeline.</summary>
    Task<ConversationCommandResult> DeleteAsync(PrincipalRef actor, ConversationId id, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:read</c>.</summary>
    Task<ConversationReadResult> GetAsync(PrincipalRef actor, ConversationId id, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:read</c>; notes only with <c>note:read</c>. <paramref name="limit"/> is 1-200.</summary>
    Task<TimelineReadResult> ReadTimelineAsync(PrincipalRef actor, ConversationId id, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:read</c>; rows are filtered by the planned Access filter inside the store query.</summary>
    Task<ConversationListResult> ListAsync(PrincipalRef actor, ConversationListQuery query, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>conversation:ingest</c>: the newest conversation of the inbox with this channel thread that is not closed.</summary>
    Task<ConversationReadResult> FindByThreadAsync(PrincipalRef actor, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default);
}

/// <summary>Access-checked attribute definition management. Reads require <c>conversation:read</c>; writes <c>conversation-attribute:manage</c>.</summary>
public interface IConversationAttributeDefinitionService
{
    Task<TicketResult<IReadOnlyList<StoredConversationAttributeDefinition>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredConversationAttributeDefinition>> CreateAsync(PrincipalRef actor, ConversationAttributeDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Changes the display name or sensitivity; the type is immutable.</summary>
    Task<TicketResult<StoredConversationAttributeDefinition>> UpdateAsync(PrincipalRef actor, ConversationAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Values stored under a deleted definition stay stored but are omitted from views.</summary>
    Task<TicketResult<StoredConversationAttributeDefinition>> DeleteAsync(PrincipalRef actor, string key, long expectedRevision, CancellationToken cancellationToken = default);
}
