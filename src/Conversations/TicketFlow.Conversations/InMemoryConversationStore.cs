using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Development-only conversation store and outbox. State is process-local, lost on restart and never shared between processes; it
/// is an example of the <see cref="IConversationStore"/> and <see cref="IConversationOutbox"/> contracts, not durable storage. Every
/// operation is serialized under one lock and lists scan the scope, so it is intended for small development data sets.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore, IConversationOutbox
{
    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly Dictionary<(TicketScope Scope, ConversationId Id), StoredConversation> conversations = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Id), List<TimelineEvent>> timelines = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation, MessageId Message), StoredMessage> messages = [];
    private readonly Dictionary<(TicketScope Scope, InboxId Inbox, string ExternalId), (ConversationId Conversation, MessageId Message)> externalIds = [];
    private readonly SortedDictionary<long, ConversationOutboxEntry> entries = [];
    private readonly SortedDictionary<string, SortedDictionary<long, Delivery>> deliveries = new(StringComparer.Ordinal);
    private long nextSequence;
    private int eventCount;

    public InMemoryConversationStore(TimeProvider? time = null, int maximumConversations = 10_000, int maximumEvents = 200_000, int maximumOutboxEntries = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConversations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEvents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutboxEntries, 1);
        this.time = time ?? TimeProvider.System;
        MaximumConversations = maximumConversations;
        MaximumEvents = maximumEvents;
        MaximumOutboxEntries = maximumOutboxEntries;
    }

    public int MaximumConversations { get; }
    public int MaximumEvents { get; }
    public int MaximumOutboxEntries { get; }

    public Task<StoredConversation?> GetAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        lock (gate)
            return Task.FromResult(conversations.GetValueOrDefault((scope, id)));
    }

    public Task<ConversationWriteResult> WriteAsync(TicketScope scope, ConversationWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(write);
        RequireChangeScope(scope, write.Change);
        lock (gate)
            return Task.FromResult(Write(scope, write));
    }

    private ConversationWriteResult Write(TicketScope scope, ConversationWrite write)
    {
        var conversation = write.Conversation;
        var key = (scope, conversation.Id);
        var exists = conversations.TryGetValue(key, out var existing);
        if (write.ExpectedRevision is null)
        {
            if (exists)
                return new ConversationWriteConflict(existing!.Revision);
            if (conversations.Count >= MaximumConversations)
                return new ConversationWriteRejected(ConversationWriteRejection.CapacityExceeded);
        }
        else
        {
            if (!exists)
                return ConversationWriteNotFound.Instance;
            if (existing!.Revision != write.ExpectedRevision)
                return new ConversationWriteConflict(existing.Revision);
            if (existing.Conversation.Inbox != conversation.Inbox || !Equals(existing.Conversation.ChannelThread, conversation.ChannelThread))
                throw new ArgumentException("The inbox and channel thread of a conversation are immutable.", nameof(write));
        }
        var head = existing?.Conversation.LastSequence ?? 0;
        if ((write.FirstSequence ?? conversation.LastSequence + 1) != head + 1 || (write.Events.Count == 0 && conversation.LastSequence != head))
            return new ConversationWriteConflict(existing?.Revision ?? 0);
        if (eventCount + write.Events.Count > MaximumEvents || (write.Change is not null && entries.Count >= MaximumOutboxEntries))
            return new ConversationWriteRejected(ConversationWriteRejection.CapacityExceeded);

        var newMessages = new List<StoredMessage>();
        foreach (var item in write.Events)
        {
            if (item.Event.Value is not MessageEvent message)
                continue;
            if (messages.ContainsKey((scope, conversation.Id, message.Id)))
                return new ConversationWriteRejected(ConversationWriteRejection.DuplicateMessageId);
            if (message.ExternalId is { } external && externalIds.TryGetValue((scope, conversation.Inbox, external), out var holder))
                return new ConversationDuplicateMessage(messages[(scope, holder.Conversation, holder.Message)]);
            newMessages.Add(new StoredMessage(scope, conversation.Id, conversation.Inbox, message.Id, item.Sequence, message.Direction, message.ExternalId, message.Delivery, 1));
        }

        var stored = new StoredConversation(scope, conversation, checked((existing?.Revision ?? 0) + 1));
        conversations[key] = stored;
        if (!timelines.TryGetValue(key, out var timeline))
            timelines[key] = timeline = [];
        timeline.AddRange(write.Events);
        eventCount += write.Events.Count;
        foreach (var message in newMessages)
        {
            messages[(scope, conversation.Id, message.Message)] = message;
            if (message.ExternalId is { } external)
                externalIds[(scope, conversation.Inbox, external)] = (conversation.Id, message.Message);
        }
        var entry = write.Change is null ? null : Append(write.Change);
        return new ConversationWriteApplied(stored, write.Events, entry);
    }

    public Task<ConversationWriteResult> DeleteAsync(TicketScope scope, ConversationId id, long expectedRevision, ConversationChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        ArgumentNullException.ThrowIfNull(change);
        RequireChangeScope(scope, change);
        if (change.Kind != ConversationChangeKind.Deleted || change.Conversation != id)
            throw new ArgumentException("The change must describe this delete.", nameof(change));
        lock (gate)
        {
            if (!conversations.TryGetValue((scope, id), out var existing))
                return Task.FromResult<ConversationWriteResult>(ConversationWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Task.FromResult<ConversationWriteResult>(new ConversationWriteConflict(existing.Revision));
            if (entries.Count >= MaximumOutboxEntries)
                return Task.FromResult<ConversationWriteResult>(new ConversationWriteRejected(ConversationWriteRejection.CapacityExceeded));
            conversations.Remove((scope, id));
            if (timelines.Remove((scope, id), out var timeline))
                eventCount -= timeline.Count;
            foreach (var message in messages.Values.Where(message => message.Scope == scope && message.Conversation == id).ToList())
            {
                messages.Remove((scope, id, message.Message));
                if (message.ExternalId is { } external)
                    externalIds.Remove((scope, message.Inbox, external));
            }
            return Task.FromResult<ConversationWriteResult>(new ConversationWriteApplied(existing, [], Append(change)));
        }
    }

    public Task<IReadOnlyList<TimelineEvent>> ReadTimelineAsync(TicketScope scope, ConversationId id, long afterSequence, int limit, bool includeNotes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationStore.MaximumTimelineRead);
        lock (gate)
        {
            if (!timelines.TryGetValue((scope, id), out var timeline))
                return Task.FromResult<IReadOnlyList<TimelineEvent>>([]);
            IReadOnlyList<TimelineEvent> page = [.. timeline
                .Where(item => item.Sequence > afterSequence && (includeNotes || item.Event.Kind != ConversationEventKind.Note))
                .Take(limit)
                .Select(item => item.Event.Value is MessageEvent message && messages.TryGetValue((scope, id, message.Id), out var current)
                    ? item.WithEvent(message.WithDelivery(current.Delivery, current.ExternalId))
                    : item)];
            return Task.FromResult(page);
        }
    }

    public Task<StoredMessage?> GetMessageAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        if (message == default)
            throw new ArgumentException("A message identifier is required.", nameof(message));
        lock (gate)
            return Task.FromResult(messages.GetValueOrDefault((scope, conversation, message)));
    }

    public Task<StoredMessage?> FindMessageByExternalIdAsync(TicketScope scope, InboxId inbox, string externalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (inbox == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(inbox));
        ArgumentNullException.ThrowIfNull(externalId);
        lock (gate)
            return Task.FromResult(externalIds.TryGetValue((scope, inbox, externalId), out var holder) ? messages[(scope, holder.Conversation, holder.Message)] : null);
    }

    public Task<MessageDeliveryWriteResult> UpdateDeliveryAsync(TicketScope scope, MessageDeliveryWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(write);
        RequireChangeScope(scope, write.Change);
        lock (gate)
        {
            if (!conversations.ContainsKey((scope, write.Conversation)) || !messages.TryGetValue((scope, write.Conversation, write.Message), out var current))
                return Task.FromResult<MessageDeliveryWriteResult>(ConversationWriteNotFound.Instance);
            if (current.DeliveryRevision != write.ExpectedDeliveryRevision)
                return Task.FromResult<MessageDeliveryWriteResult>(new MessageDeliveryConflict(current));
            if (write.Change.Inbox != current.Inbox)
                throw new ArgumentException("The change must name the message's inbox.", nameof(write));
            var externalId = current.ExternalId;
            if (write.ExternalId is { } external && external != current.ExternalId)
            {
                if (current.ExternalId is not null)
                    throw new ArgumentException("A message's external identifier cannot be replaced.", nameof(write));
                if (externalIds.TryGetValue((scope, current.Inbox, external), out var holder))
                    return Task.FromResult<MessageDeliveryWriteResult>(new ConversationDuplicateMessage(messages[(scope, holder.Conversation, holder.Message)]));
                externalId = external;
            }
            if (entries.Count >= MaximumOutboxEntries)
                throw new InvalidOperationException("The in-memory conversation outbox is full.");
            var updated = current with { Delivery = write.Delivery, ExternalId = externalId, DeliveryRevision = checked(current.DeliveryRevision + 1) };
            messages[(scope, write.Conversation, write.Message)] = updated;
            if (externalId is not null && current.ExternalId is null)
                externalIds[(scope, current.Inbox, externalId)] = (write.Conversation, write.Message);
            return Task.FromResult<MessageDeliveryWriteResult>(new MessageDeliveryApplied(updated, Append(write.Change)));
        }
    }

    public Task<StoredConversation?> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(thread);
        lock (gate)
            return Task.FromResult(conversations.Values
                .Where(stored => stored.Scope == scope && stored.Conversation.Inbox == inbox && Equals(stored.Conversation.ChannelThread, thread)
                    && stored.Conversation.Status.Kind != ConversationStatusKind.Closed)
                .OrderByDescending(stored => stored.Conversation.CreatedAt)
                .ThenByDescending(stored => stored.Id.Value, StringComparer.Ordinal)
                .FirstOrDefault());
    }

    public Task<ConversationPage> ListAsync(TicketScope scope, ConversationListQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            var rows = conversations.Values
                .Where(stored => stored.Scope == scope && query.Matches(stored.Conversation) && query.IsAfterCursor(stored.Conversation)
                    && AccessFilterEvaluator.Matches(filter, ConversationAccess.Resource(stored)))
                .OrderByDescending(stored => stored.Conversation.LastActivityAt)
                .ThenByDescending(stored => stored.Id.Value, StringComparer.Ordinal)
                .Take(query.Limit + 1)
                .ToList();
            if (rows.Count <= query.Limit)
                return Task.FromResult(new ConversationPage(rows, null));
            var page = rows[..query.Limit];
            return Task.FromResult(new ConversationPage(page, new ConversationListCursor(page[^1].Conversation.LastActivityAt, page[^1].Id)));
        }
    }

    public Task RegisterConsumerAsync(string consumer, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        lock (gate)
        {
            if (!deliveries.ContainsKey(consumer))
            {
                if (deliveries.Count >= IConversationOutbox.MaximumConsumers)
                    throw new InvalidOperationException($"At most {IConversationOutbox.MaximumConsumers} outbox consumers can be registered.");
                deliveries[consumer] = [];
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListConsumersAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<string>>([.. deliveries.Keys]);
    }

    public Task<IReadOnlyList<ConversationOutboxClaim>> ClaimAsync(string consumer, int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        RequireClaim(limit, lease);
        lock (gate)
        {
            if (!deliveries.TryGetValue(consumer, out var pending))
                return Task.FromResult<IReadOnlyList<ConversationOutboxClaim>>([]);
            var now = time.GetUtcNow();
            var blocked = new HashSet<(TicketScope, ConversationId)>();
            var claims = new List<ConversationOutboxClaim>();
            foreach (var (sequence, delivery) in pending)
            {
                if (delivery.State != DeliveryState.Pending || !blocked.Add(delivery.Conversation))
                    continue;
                if (delivery.AvailableAt > now || delivery.LeaseUntil > now)
                    continue;
                delivery.Fence = checked(delivery.Fence + 1);
                delivery.Attempts = checked(delivery.Attempts + 1);
                delivery.LeaseUntil = now + lease;
                claims.Add(new ConversationOutboxClaim(entries[sequence], consumer, delivery.Fence, delivery.Attempts, delivery.LeaseUntil.Value));
                if (claims.Count == limit)
                    break;
            }
            return Task.FromResult<IReadOnlyList<ConversationOutboxClaim>>(claims);
        }
    }

    public Task<bool> AcknowledgeAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default) =>
        Complete(consumer, sequence, fence, requireLease: true, delivery => delivery.State = DeliveryState.Acknowledged);

    public Task<bool> ReleaseAsync(string consumer, long sequence, long fence, DateTimeOffset retryAt, CancellationToken cancellationToken = default) =>
        Complete(consumer, sequence, fence, requireLease: true, delivery =>
        {
            delivery.LeaseUntil = null;
            delivery.AvailableAt = retryAt;
        });

    public Task<bool> DeadLetterAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default) =>
        Complete(consumer, sequence, fence, requireLease: false, delivery => delivery.State = DeliveryState.DeadLettered);

    public Task<int> PruneAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationOutbox.MaximumPrune);
        lock (gate)
        {
            var doomed = entries.Values
                .Where(entry => entry.Change.OccurredAt < olderThan
                    && deliveries.Values.All(byConsumer => !byConsumer.TryGetValue(entry.Sequence, out var delivery) || delivery.State != DeliveryState.Pending))
                .Take(limit)
                .Select(entry => entry.Sequence)
                .ToList();
            foreach (var sequence in doomed)
            {
                entries.Remove(sequence);
                foreach (var byConsumer in deliveries.Values)
                    byConsumer.Remove(sequence);
            }
            return Task.FromResult(doomed.Count);
        }
    }

    private Task<bool> Complete(string consumer, long sequence, long fence, bool requireLease, Action<Delivery> apply)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        lock (gate)
        {
            if (!deliveries.TryGetValue(consumer, out var pending) || !pending.TryGetValue(sequence, out var delivery)
                || delivery.State != DeliveryState.Pending || delivery.Fence == 0 || delivery.Fence != fence
                || (requireLease && !(delivery.LeaseUntil > time.GetUtcNow())))
                return Task.FromResult(false);
            apply(delivery);
            return Task.FromResult(true);
        }
    }

    private ConversationOutboxEntry Append(ConversationChange change)
    {
        var entry = new ConversationOutboxEntry(++nextSequence, change);
        entries.Add(entry.Sequence, entry);
        var now = time.GetUtcNow();
        foreach (var byConsumer in deliveries.Values)
            byConsumer.Add(entry.Sequence, new Delivery((change.Scope, change.Conversation), now));
        return entry;
    }

    internal static void RequireClaim(int limit, TimeSpan lease)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationOutbox.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > IConversationOutbox.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most one hour.");
    }

    private static void RequireChangeScope(TicketScope scope, ConversationChange? change)
    {
        if (change is not null && change.Scope != scope)
            throw new ArgumentException("The outbox change must carry the write scope.", nameof(change));
    }

    private static void RequireId(ConversationId id)
    {
        if (id == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(id));
    }

    private enum DeliveryState
    {
        Pending,
        Acknowledged,
        DeadLettered,
    }

    private sealed class Delivery((TicketScope, ConversationId) conversation, DateTimeOffset availableAt)
    {
        public (TicketScope, ConversationId) Conversation { get; } = conversation;
        public DeliveryState State { get; set; }
        public long Fence { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset AvailableAt { get; set; } = availableAt;
        public DateTimeOffset? LeaseUntil { get; set; }
    }
}

/// <summary>Development-only, process-local conversation attribute definition store.</summary>
public sealed class InMemoryConversationAttributeDefinitionStore : IConversationAttributeDefinitionStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, string Key), StoredConversationAttributeDefinition> definitions = [];

    public InMemoryConversationAttributeDefinitionStore(int maximumEntries = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        MaximumEntries = maximumEntries;
    }

    public int MaximumEntries { get; }

    public Task<StoredConversationAttributeDefinition?> GetAsync(TicketScope scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ConversationAttributeDefinition.RequireKey(key, nameof(key));
        lock (gate)
            return Task.FromResult(definitions.GetValueOrDefault((scope, key)));
    }

    public Task<IReadOnlyList<StoredConversationAttributeDefinition>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredConversationAttributeDefinition>>([.. definitions.Values.Where(entry => entry.Scope == scope)
                .OrderBy(entry => entry.Definition.Key, StringComparer.Ordinal)]);
    }

    public Task<ConversationAttributeDefinitionWriteResult> CreateAsync(TicketScope scope, ConversationAttributeDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        lock (gate)
        {
            if (definitions.TryGetValue((scope, definition.Key), out var existing))
                return Result(new ConversationWriteConflict(existing.Revision));
            if (definitions.Count >= MaximumEntries
                || definitions.Keys.Count(key => key.Scope == scope) >= IConversationAttributeDefinitionStore.MaximumDefinitionsPerWorkspace)
                return Result(new ConversationWriteRejected(ConversationWriteRejection.CapacityExceeded));
            var stored = new StoredConversationAttributeDefinition(scope, definition, 1);
            definitions.Add((scope, definition.Key), stored);
            return Result(new ConversationAttributeDefinitionApplied(stored));
        }
    }

    public Task<ConversationAttributeDefinitionWriteResult> UpdateAsync(TicketScope scope, ConversationAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!definitions.TryGetValue((scope, definition.Key), out var existing))
                return Result(ConversationWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new ConversationWriteConflict(existing.Revision));
            if (existing.Definition.Type != definition.Type)
                return Result(new ConversationWriteRejected(ConversationWriteRejection.TypeChanged));
            var stored = new StoredConversationAttributeDefinition(scope, definition, checked(existing.Revision + 1));
            definitions[(scope, definition.Key)] = stored;
            return Result(new ConversationAttributeDefinitionApplied(stored));
        }
    }

    public Task<ConversationAttributeDefinitionWriteResult> DeleteAsync(TicketScope scope, string key, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ConversationAttributeDefinition.RequireKey(key, nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!definitions.TryGetValue((scope, key), out var existing))
                return Result(ConversationWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new ConversationWriteConflict(existing.Revision));
            definitions.Remove((scope, key));
            return Result(new ConversationAttributeDefinitionApplied(existing));
        }
    }

    private static Task<ConversationAttributeDefinitionWriteResult> Result(ConversationAttributeDefinitionWriteResult result) => Task.FromResult(result);
}
