using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>Persisted outbox change kinds. Never renumber.</summary>
public enum ConversationChangeKind
{
    /// <summary>A conversation was created (with its first events, if any).</summary>
    Started = 1,

    /// <summary>Events were appended to the timeline.</summary>
    EventsAppended = 2,

    /// <summary>A message's delivery state or external identifier changed (no timeline event, no revision change).</summary>
    DeliveryUpdated = 3,

    /// <summary>The conversation and its timeline were deleted.</summary>
    Deleted = 4,
}

/// <summary>An appended timeline event as seen by outbox consumers: identifiers only, never bodies.</summary>
public sealed record ConversationEventSummary
{
    public ConversationEventSummary(long sequence, ConversationEventKind kind, MessageId? message = null, MessageDirection? direction = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (message == default(MessageId) || (direction is { } value && !Enum.IsDefined(value)))
            throw new ArgumentException("Message references must be initialized.", message == default(MessageId) ? nameof(message) : nameof(direction));
        if ((kind == ConversationEventKind.Message) != (direction is not null) || (kind is ConversationEventKind.Message or ConversationEventKind.Note) != (message is not null))
            throw new ArgumentException("Message summaries carry a message and direction; note summaries a message; others neither.", nameof(kind));
        Sequence = sequence;
        Kind = kind;
        Message = message;
        Direction = direction;
    }

    public long Sequence { get; }
    public ConversationEventKind Kind { get; }
    public MessageId? Message { get; }
    public MessageDirection? Direction { get; }

    public static ConversationEventSummary From(TimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);
        return timelineEvent.Event switch
        {
            MessageEvent message => new(timelineEvent.Sequence, ConversationEventKind.Message, message.Id, message.Direction),
            NoteEvent note => new(timelineEvent.Sequence, ConversationEventKind.Note, note.Id),
            AssignmentChanged or TeamChanged or StatusChanged or PriorityChanged or LabelsChanged or AttributeChanged or ParticipantChanged
                or ContactChanged or SystemEvent or ExtensionEvent => new(timelineEvent.Sequence, timelineEvent.Event.Kind),
        };
    }
}

/// <summary>
/// A domain change appended to the outbox in the same transaction as the conversation write. Identifiers only — consumers (realtime,
/// routing, SLA, automation, reporting, channel dispatch) read details through trusted store access. <see cref="Revision"/> is the
/// conversation revision after the write (for <see cref="ConversationChangeKind.Deleted"/>, the deleted revision).
/// </summary>
public sealed record ConversationChange
{
    public const int MaximumEvents = ConversationWrite.MaximumEvents;

    public ConversationChange(Guid id, TicketScope scope, ConversationId conversation, InboxId inbox, long revision, ConversationChangeKind kind,
        IEnumerable<ConversationEventSummary> events, MessageId? message, MessageDeliveryState? delivery, DateTimeOffset occurredAt)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A change identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default || inbox == default)
            throw new ArgumentException("Conversation and inbox identifiers are required.", conversation == default ? nameof(conversation) : nameof(inbox));
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(events);
        Events = events is IReadOnlyList<ConversationEventSummary> { Count: 0 } ? [] : [.. events];
        if (Events.Count > MaximumEvents)
            throw new ArgumentException($"A change summarizes at most {MaximumEvents} events.", nameof(events));
        for (var i = 0; i < Events.Count; i++)
            if (Events[i] is null)
                throw new ArgumentException($"A change summarizes at most {MaximumEvents} events.", nameof(events));
        if ((kind == ConversationChangeKind.DeliveryUpdated) != (message is not null && delivery is not null) || message == default(MessageId)
            || (delivery is { } state && !Enum.IsDefined(state)))
            throw new ArgumentException("Delivery changes, and only they, name a message and delivery state.", nameof(kind));
        if ((kind == ConversationChangeKind.EventsAppended && Events.Count == 0)
            || (kind is ConversationChangeKind.DeliveryUpdated or ConversationChangeKind.Deleted && Events.Count > 0))
            throw new ArgumentException("Appended changes summarize at least one event; delivery and delete changes none.", nameof(events));
        Id = id;
        Scope = scope;
        Conversation = conversation;
        Inbox = inbox;
        Revision = revision;
        Kind = kind;
        Message = message;
        Delivery = delivery;
        OccurredAt = ConversationText.Instant(occurredAt);
    }

    public Guid Id { get; }
    public TicketScope Scope { get; }
    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public long Revision { get; }
    public ConversationChangeKind Kind { get; }
    public IReadOnlyList<ConversationEventSummary> Events { get; }
    public MessageId? Message { get; }
    public MessageDeliveryState? Delivery { get; }
    public DateTimeOffset OccurredAt { get; }

    /// <summary>Outgoing messages in this change that the channel layer must dispatch.</summary>
    public IReadOnlyList<MessageId> OutgoingMessages
    {
        get
        {
            List<MessageId>? outgoing = null;
            for (var i = 0; i < Events.Count; i++)
                if (Events[i].Direction == MessageDirection.Outgoing)
                    (outgoing ??= []).Add(Events[i].Message!.Value);
            return outgoing is null ? [] : outgoing;
        }
    }

    public bool Equals(ConversationChange? other) =>
        other is not null && Id == other.Id && Scope == other.Scope && Conversation == other.Conversation && Inbox == other.Inbox && Revision == other.Revision
        && Kind == other.Kind && Events.SequenceEqual(other.Events) && Message == other.Message && Delivery == other.Delivery && OccurredAt == other.OccurredAt;

    public override int GetHashCode() => HashCode.Combine(Id, Revision, Kind);

    public override string ToString() => $"ConversationChange {{ Id = {Id}, Kind = {Kind}, Conversation = {Conversation} }}";
}

/// <summary>A committed outbox entry; <see cref="Sequence"/> is store-wide, positive and increasing in commit order per conversation.</summary>
public sealed record ConversationOutboxEntry(long Sequence, ConversationChange Change);

/// <summary>
/// A leased claim of one entry for one consumer. <see cref="Fence"/> increases with every claim of the entry by the consumer;
/// acknowledge, release and dead-letter succeed only with the current fence while the lease is held.
/// </summary>
public sealed record ConversationOutboxClaim(ConversationOutboxEntry Entry, string Consumer, long Fence, int Attempt, DateTimeOffset LeaseUntil);

/// <summary>
/// Durable, multi-consumer outbox of <see cref="ConversationChange"/>s. Contract:
/// <list type="bullet">
/// <item>Entries are appended only inside conversation writes (<see cref="IConversationStore"/>), atomically with them.</item>
/// <item>Each registered consumer gets its own delivery of every entry committed after its registration.</item>
/// <item><see cref="ClaimAsync"/> returns available deliveries (not completed, retry time reached, lease free or expired) in entry
/// order and leases them; a delivery is claimable only when the consumer has no earlier pending delivery of the same conversation, so
/// each consumer sees a conversation's changes in order. Concurrent claimers never receive the same delivery under a live lease.</item>
/// <item><see cref="AcknowledgeAsync"/> completes, <see cref="ReleaseAsync"/> schedules a retry and <see cref="DeadLetterAsync"/> parks a
/// delivery (unblocking later entries of the conversation); each returns <see langword="false"/> when the fence is stale or the lease
/// expired (acknowledge and release only).</item>
/// <item><see cref="PruneAsync"/> deletes entries older than a cutoff whose deliveries are all completed or dead-lettered.</item>
/// </list>
/// </summary>
public interface IConversationOutbox
{
    public const int MaximumConsumers = 32;
    public const int MaximumConsumerLength = 64;
    public const int MaximumClaim = 100;
    public const int MaximumPrune = 10_000;

    /// <summary>The longest lease a claim may take.</summary>
    public static readonly TimeSpan MaximumLease = TimeSpan.FromHours(1);

    /// <summary>Registers <paramref name="consumer"/> (a lowercase token); idempotent. At most <see cref="MaximumConsumers"/> consumers.</summary>
    Task RegisterConsumerAsync(string consumer, CancellationToken cancellationToken = default);

    /// <summary>Registered consumers in ordinal order.</summary>
    Task<IReadOnlyList<string>> ListConsumersAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationOutboxClaim>> ClaimAsync(string consumer, int limit, TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> AcknowledgeAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(string consumer, long sequence, long fence, DateTimeOffset retryAt, CancellationToken cancellationToken = default);

    Task<bool> DeadLetterAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges a batch of claimed deliveries for <paramref name="consumer"/> and returns how many succeeded.</summary>
    async Task<int> AcknowledgeBatchAsync(string consumer, IReadOnlyList<(long Sequence, long Fence)> claims, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var count = 0;
        for (var i = 0; i < claims.Count; i++)
            if (await AcknowledgeAsync(consumer, claims[i].Sequence, claims[i].Fence, cancellationToken).ConfigureAwait(false))
                count++;
        return count;
    }

    /// <summary>Releases a batch of claimed deliveries for <paramref name="consumer"/> and returns how many succeeded.</summary>
    async Task<int> ReleaseBatchAsync(string consumer, IReadOnlyList<(long Sequence, long Fence, DateTimeOffset RetryAt)> claims, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var count = 0;
        for (var i = 0; i < claims.Count; i++)
            if (await ReleaseAsync(consumer, claims[i].Sequence, claims[i].Fence, claims[i].RetryAt, cancellationToken).ConfigureAwait(false))
                count++;
        return count;
    }

    /// <summary>Dead-letters a batch of claimed deliveries for <paramref name="consumer"/> and returns how many succeeded.</summary>
    async Task<int> DeadLetterBatchAsync(string consumer, IReadOnlyList<(long Sequence, long Fence)> claims, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var count = 0;
        for (var i = 0; i < claims.Count; i++)
            if (await DeadLetterAsync(consumer, claims[i].Sequence, claims[i].Fence, cancellationToken).ConfigureAwait(false))
                count++;
        return count;
    }

    Task<int> PruneAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default);

    /// <summary>Validates a consumer name: 1-64 characters of a-z, 0-9, '-', '_' or '.', starting with a letter.</summary>
    public static string RequireConsumer(string consumer, string parameterName = "consumer") =>
        ConversationText.RequireDottedName(consumer, MaximumConsumerLength, parameterName, "Consumer names");
}

/// <summary>
/// A durable outbox consumer registered by a later feature (realtime, routing, SLA, automation, reporting, channel dispatch).
/// <see cref="HandleAsync"/> must be idempotent: an entry is redelivered when the lease expires before acknowledgement.
/// </summary>
public interface IConversationOutboxHandler
{
    /// <summary>The consumer name (1-64 characters of a-z, 0-9, '-', '_' or '.', starting with a letter), unique per handler.</summary>
    string Consumer { get; }

    Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional batch-capable <see cref="IConversationOutboxHandler"/>. When a handler implements this interface,
/// <c>ConversationOutboxDispatcher</c> passes all eligible claimed entries of one run in sequence order to
/// <see cref="HandleBatchAsync"/> so consumers (such as the event projector) can combine work into fewer durable writes.
/// <see cref="HandleBatchAsync"/> must be idempotent for every entry in the batch.
/// </summary>
public interface IConversationOutboxBatchHandler : IConversationOutboxHandler
{
    Task HandleBatchAsync(IReadOnlyList<ConversationOutboxEntry> entries, CancellationToken cancellationToken = default);
}
