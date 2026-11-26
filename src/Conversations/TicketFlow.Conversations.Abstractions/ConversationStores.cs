using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// One atomic conversation write: create (<see cref="ExpectedRevision"/> <see langword="null"/>) or compare-and-set replace the
/// snapshot, append timeline <see cref="Events"/> (contiguous sequences continuing the stored head and ending at
/// <see cref="Conversation.LastSequence"/>), register a message row for every <see cref="MessageEvent"/> and append the optional
/// outbox <see cref="Change"/>.
/// </summary>
public sealed record ConversationWrite
{
    public const int MaximumEvents = 16;

    public ConversationWrite(Conversation conversation, long? expectedRevision, IEnumerable<TimelineEvent>? events = null, ConversationChange? change = null)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        Events = events is null || events is IReadOnlyList<TimelineEvent> { Count: 0 } ? [] : [.. events];
        if (Events.Count > MaximumEvents)
            throw new ArgumentException($"A write appends at most {MaximumEvents} events.", nameof(events));
        for (var index = 0; index < Events.Count; index++)
        {
            if (Events[index] is null)
                throw new ArgumentException($"A write appends at most {MaximumEvents} events.", nameof(events));
            if (Events[index].Conversation != conversation.Id)
                throw new ArgumentException("Events must belong to the written conversation.", nameof(events));
            if (index > 0 && Events[index].Sequence != Events[index - 1].Sequence + 1)
                throw new ArgumentException("Event sequences must be contiguous.", nameof(events));
        }
        if (Events.Count > 0 && Events[^1].Sequence != conversation.LastSequence)
            throw new ArgumentException("The conversation head must equal the last appended sequence.", nameof(events));
        if (expectedRevision is null && Events.Count > 0 && Events[0].Sequence != 1)
            throw new ArgumentException("A created conversation's timeline starts at sequence 1.", nameof(events));
        if (expectedRevision is null && Events.Count == 0 && conversation.LastSequence != 0)
            throw new ArgumentException("A created conversation without events has an empty timeline.", nameof(conversation));
        if (Events.Count > 1)
        {
            for (var i = 0; i < Events.Count; i++)
            {
                if (Events[i].Event.Value is not MessageEvent left)
                    continue;
                for (var j = i + 1; j < Events.Count; j++)
                    if (Events[j].Event.Value is MessageEvent right && left.Id == right.Id)
                        throw new ArgumentException("Message identifiers must be distinct.", nameof(events));
            }
        }
        if (change is not null && (change.Conversation != conversation.Id || change.Inbox != conversation.Inbox))
            throw new ArgumentException("The change must describe the written conversation.", nameof(change));
        Conversation = conversation;
        ExpectedRevision = expectedRevision;
        Change = change;
    }

    public Conversation Conversation { get; }
    public long? ExpectedRevision { get; }
    public IReadOnlyList<TimelineEvent> Events { get; }
    public ConversationChange? Change { get; }

    /// <summary>The first appended sequence, or <see langword="null"/> when no events are appended.</summary>
    public long? FirstSequence => Events.Count == 0 ? null : Events[0].Sequence;
}

/// <summary>A message's mutable delivery projection. <see cref="DeliveryRevision"/> starts at 1 and fences delivery updates.</summary>
public sealed record StoredMessage(
    TicketScope Scope,
    ConversationId Conversation,
    InboxId Inbox,
    MessageId Message,
    long Sequence,
    MessageDirection Direction,
    string? ExternalId,
    MessageDelivery Delivery,
    long DeliveryRevision)
{
    public override string ToString() => $"StoredMessage {{ Conversation = {Conversation}, Message = {Message}, State = {Delivery.State} }}";
}

/// <summary>Compare-and-set update of one message's delivery state and (when not yet set) external identifier, with its outbox change.</summary>
public sealed record MessageDeliveryWrite
{
    public MessageDeliveryWrite(ConversationId conversation, MessageId message, long expectedDeliveryRevision, MessageDelivery delivery, string? externalId, ConversationChange change)
    {
        if (conversation == default || message == default)
            throw new ArgumentException("Conversation and message identifiers are required.", conversation == default ? nameof(conversation) : nameof(message));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedDeliveryRevision, 1);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind != ConversationChangeKind.DeliveryUpdated || change.Conversation != conversation || change.Message != message || change.Delivery != delivery.State)
            throw new ArgumentException("The change must describe this delivery update.", nameof(change));
        Conversation = conversation;
        Message = message;
        ExpectedDeliveryRevision = expectedDeliveryRevision;
        Delivery = delivery;
        ExternalId = ConversationMessageLimits.ExternalId(externalId, nameof(externalId));
        Change = change;
    }

    public ConversationId Conversation { get; }
    public MessageId Message { get; }
    public long ExpectedDeliveryRevision { get; }
    public MessageDelivery Delivery { get; }
    public string? ExternalId { get; }
    public ConversationChange Change { get; }
}

/// <summary>The write committed. <see cref="Entry"/> is the appended outbox entry, if any.</summary>
public sealed record ConversationWriteApplied(StoredConversation Conversation, IReadOnlyList<TimelineEvent> Events, ConversationOutboxEntry? Entry);

/// <summary>The conversation (or message) does not exist in the scope. Nothing was written.</summary>
public sealed record ConversationWriteNotFound
{
    private ConversationWriteNotFound()
    {
    }

    public static ConversationWriteNotFound Instance { get; } = new();
}

/// <summary>The revision did not match, or a create found an existing conversation. Nothing was written.</summary>
public sealed record ConversationWriteConflict(long CurrentRevision);

/// <summary>An external message identifier is already recorded in the inbox (by <see cref="Existing"/>). Nothing was written.</summary>
public sealed record ConversationDuplicateMessage(StoredMessage Existing);

public enum ConversationWriteRejection
{
    /// <summary>A store capacity limit would be exceeded.</summary>
    CapacityExceeded = 1,

    /// <summary>An attribute definition update tried to change the type.</summary>
    TypeChanged = 2,

    /// <summary>A message identifier is already used in the conversation.</summary>
    DuplicateMessageId = 3,
}

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record ConversationWriteRejected(ConversationWriteRejection Reason);

/// <summary>Outcome of a conversation write or delete. Switch over every case without a discard arm.</summary>
public union ConversationWriteResult(ConversationWriteApplied, ConversationWriteNotFound, ConversationWriteConflict, ConversationDuplicateMessage, ConversationWriteRejected);

/// <summary>The delivery update committed.</summary>
public sealed record MessageDeliveryApplied(StoredMessage Message, ConversationOutboxEntry Entry);

/// <summary>The delivery revision did not match; <see cref="Current"/> is the stored projection. Nothing was written.</summary>
public sealed record MessageDeliveryConflict(StoredMessage Current);

/// <summary>Outcome of a delivery update. Switch over every case without a discard arm.</summary>
public union MessageDeliveryWriteResult(MessageDeliveryApplied, ConversationWriteNotFound, MessageDeliveryConflict, ConversationDuplicateMessage);

/// <summary>Keyset position: the last row's activity time and identifier (descending order).</summary>
public sealed record ConversationListCursor
{
    public ConversationListCursor(DateTimeOffset lastActivityAt, ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        LastActivityAt = ConversationText.Instant(lastActivityAt);
        Conversation = conversation;
    }

    public DateTimeOffset LastActivityAt { get; }
    public ConversationId Conversation { get; }
}

/// <summary>
/// A bounded list query ordered by last activity descending, then identifier descending (ordinal). Every supplied criterion must
/// match. <see cref="Unassigned"/> selects conversations without an assignee and cannot be combined with <see cref="Assignee"/>.
/// </summary>
public sealed record ConversationListQuery
{
    public const int MaximumLimit = 100;
    public const int DefaultLimit = 25;

    public ConversationListQuery(
        InboxId? inbox = null,
        ConversationStatusKind? status = null,
        UserId? assignee = null,
        bool unassigned = false,
        TeamId? team = null,
        LabelId? label = null,
        ContactId? contact = null,
        int limit = DefaultLimit,
        ConversationListCursor? after = null)
    {
        if (inbox == default(InboxId) || assignee == default(UserId) || team == default(TeamId) || label == default(LabelId) || contact == default(ContactId))
            throw new ArgumentException("Criteria must be initialized when supplied.", inbox == default(InboxId) ? nameof(inbox) : assignee == default(UserId) ? nameof(assignee) : team == default(TeamId) ? nameof(team) : label == default(LabelId) ? nameof(label) : nameof(contact));
        if (status is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (unassigned && assignee is not null)
            throw new ArgumentException("Unassigned cannot be combined with an assignee.", nameof(unassigned));
        if (limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limits are 1-{MaximumLimit}.");
        Inbox = inbox;
        Status = status;
        Assignee = assignee;
        Unassigned = unassigned;
        Team = team;
        Label = label;
        Contact = contact;
        Limit = limit;
        After = after;
    }

    public InboxId? Inbox { get; }
    public ConversationStatusKind? Status { get; }
    public UserId? Assignee { get; }
    public bool Unassigned { get; }
    public TeamId? Team { get; }
    public LabelId? Label { get; }
    public ContactId? Contact { get; }
    public int Limit { get; }
    public ConversationListCursor? After { get; }

    public ConversationListQuery WithAfter(ConversationListCursor? after) => new(Inbox, Status, Assignee, Unassigned, Team, Label, Contact, Limit, after);

    /// <summary>True when <paramref name="conversation"/> satisfies every criterion (not the cursor).</summary>
    public bool Matches(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return (Inbox is null || conversation.Inbox == Inbox) && (Status is null || conversation.Status.Kind == Status)
            && (Assignee is null || conversation.Assignee == Assignee) && (!Unassigned || conversation.Assignee is null)
            && (Team is null || conversation.Team == Team) && (Label is null || conversation.Labels.Contains(Label.Value))
            && (Contact is null || conversation.Contact == Contact);
    }

    /// <summary>True when <paramref name="conversation"/> sorts after <see cref="After"/> (or there is no cursor).</summary>
    public bool IsAfterCursor(Conversation conversation) =>
        After is not { } cursor || conversation.LastActivityAt < cursor.LastActivityAt
        || (conversation.LastActivityAt == cursor.LastActivityAt && string.CompareOrdinal(conversation.Id.Value, cursor.Conversation.Value) < 0);
}

/// <summary>One page; <see cref="Next"/> is <see langword="null"/> at the end.</summary>
public sealed record ConversationPage(IReadOnlyList<StoredConversation> Conversations, ConversationListCursor? Next);

/// <summary>
/// Durable conversation storage. Contract:
/// <list type="bullet">
/// <item>Every operation is confined to exactly one <see cref="TicketScope"/>; records never cross scopes.</item>
/// <item><see cref="WriteAsync"/> is atomic: the snapshot write is fenced on the expected revision (create requires absence and
/// starts at revision 1; updates increment by one), the first appended sequence must equal the stored head plus one, message rows
/// are registered, and the outbox change is appended with deliveries for every registered consumer — all or nothing.</item>
/// <item>Sequences are gapless and unique per conversation; the timeline is append-only.</item>
/// <item>A message's external identifier is unique per (scope, inbox): a write that would record a duplicate returns
/// <see cref="ConversationDuplicateMessage"/> (inbound idempotency, safe under concurrency).</item>
/// <item><see cref="UpdateDeliveryAsync"/> is fenced on the message's delivery revision and does not change the conversation
/// revision or timeline; the timeline returns messages with their current delivery state and external identifier.</item>
/// <item><see cref="ListAsync"/> applies the supplied <see cref="AccessFilter"/> (over <see cref="ConversationAccess.Resource(StoredConversation)"/>)
/// inside the bounded keyset query, never by post-filtering an unbounded result; untranslatable filters return no rows.</item>
/// <item><see cref="DeleteAsync"/> removes the conversation, its timeline and message rows and appends the change.</item>
/// </list>
/// </summary>
public interface IConversationStore
{
    public const int MaximumTimelineRead = 200;

    Task<StoredConversation?> GetAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken = default);

    /// <summary>Loads conversations by identifier in one scope; missing identifiers are omitted.</summary>
    async Task<IReadOnlyDictionary<ConversationId, StoredConversation>> GetManyAsync(TicketScope scope, IReadOnlyCollection<ConversationId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ids);
        var result = new Dictionary<ConversationId, StoredConversation>(ids.Count);
        foreach (var id in ids)
            if (!result.ContainsKey(id) && await GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } stored)
                result[id] = stored;
        return result;
    }

    Task<ConversationWriteResult> WriteAsync(TicketScope scope, ConversationWrite write, CancellationToken cancellationToken = default);

    Task<ConversationWriteResult> DeleteAsync(TicketScope scope, ConversationId id, long expectedRevision, ConversationChange change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Timeline events with a sequence greater than <paramref name="afterSequence"/>, ascending, at most <paramref name="limit"/>
    /// (1-<see cref="MaximumTimelineRead"/>); notes are skipped inside the query unless <paramref name="includeNotes"/>.
    /// </summary>
    Task<IReadOnlyList<TimelineEvent>> ReadTimelineAsync(TicketScope scope, ConversationId id, long afterSequence, int limit, bool includeNotes, CancellationToken cancellationToken = default);

    Task<StoredMessage?> GetMessageAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default);

    Task<StoredMessage?> FindMessageByExternalIdAsync(TicketScope scope, InboxId inbox, string externalId, CancellationToken cancellationToken = default);

    Task<MessageDeliveryWriteResult> UpdateDeliveryAsync(TicketScope scope, MessageDeliveryWrite write, CancellationToken cancellationToken = default);

    /// <summary>The most recently created conversation in <paramref name="inbox"/> with this thread reference that is not closed.</summary>
    Task<StoredConversation?> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default);

    Task<ConversationPage> ListAsync(TicketScope scope, ConversationListQuery query, AccessFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>The definition write was applied; for deletes, the removed state.</summary>
public sealed record ConversationAttributeDefinitionApplied(StoredConversationAttributeDefinition Value);

/// <summary>Outcome of a definition write. Switch over every case without a discard arm.</summary>
public union ConversationAttributeDefinitionWriteResult(ConversationAttributeDefinitionApplied, ConversationWriteNotFound, ConversationWriteConflict, ConversationWriteRejected);

/// <summary>
/// Workspace-scoped conversation attribute definitions keyed by attribute key: creates start at revision 1, updates and deletes are
/// compare-and-set, the type cannot change (<see cref="ConversationWriteRejection.TypeChanged"/>) and at most
/// <see cref="MaximumDefinitionsPerWorkspace"/> definitions exist per scope (checked atomically).
/// </summary>
public interface IConversationAttributeDefinitionStore
{
    public const int MaximumDefinitionsPerWorkspace = 256;

    Task<StoredConversationAttributeDefinition?> GetAsync(TicketScope scope, string key, CancellationToken cancellationToken = default);

    /// <summary>Definitions ordered by key (ordinal).</summary>
    Task<IReadOnlyList<StoredConversationAttributeDefinition>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<ConversationAttributeDefinitionWriteResult> CreateAsync(TicketScope scope, ConversationAttributeDefinition definition, CancellationToken cancellationToken = default);

    Task<ConversationAttributeDefinitionWriteResult> UpdateAsync(TicketScope scope, ConversationAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default);

    Task<ConversationAttributeDefinitionWriteResult> DeleteAsync(TicketScope scope, string key, long expectedRevision, CancellationToken cancellationToken = default);
}
