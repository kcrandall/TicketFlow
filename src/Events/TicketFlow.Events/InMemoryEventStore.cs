using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// Development-only <see cref="IEventLog"/> and <see cref="IEventSubscriptionStore"/>: process-local, bounded (total retained events),
/// not durable. Implements the contracts under one lock.
/// </summary>
public sealed class InMemoryEventStore(TimeProvider? time = null, int maximumEvents = 1_000_000) : IEventLog, IEventSubscriptionStore
{
    private readonly Lock gate = new();
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Dictionary<TicketScope, ScopeLog> logs = [];
    private readonly Dictionary<(TicketScope, string), long> checkpoints = [];
    private readonly Dictionary<(TicketScope, EventSubscriptionId), Entry> subscriptions = [];
    private int retained;

    private sealed class ScopeLog
    {
        public long Head;
        public readonly SortedList<long, StoredIntegrationEvent> Events = [];
        public readonly HashSet<Guid> Ids = [];
    }

    private sealed class Entry(StoredEventSubscription subscription, EventDeliveryState state)
    {
        public StoredEventSubscription Subscription = subscription;
        public EventDeliveryState State = state;
        public long Fence;
        public DateTimeOffset? LeaseUntil;
        public readonly SortedList<long, EventDeadLetter> DeadLetters = [];
    }

    public Task<EventAppendResult> AppendAsync(TicketScope scope, IReadOnlyList<IntegrationEventDraft> drafts, DateTimeOffset recordedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IEventLog.RequireDrafts(drafts);
        recordedAt = EventText.Instant(recordedAt);
        lock (gate)
        {
            var log = Log(scope);
            var appended = new List<StoredIntegrationEvent>();
            var seen = new HashSet<Guid>();
            var duplicates = 0;
            foreach (var draft in drafts)
            {
                if (log.Ids.Contains(draft.Event.Id) || !seen.Add(draft.Event.Id))
                {
                    duplicates++;
                    continue;
                }
                appended.Add(new StoredIntegrationEvent(scope, log.Head + appended.Count + 1, draft.Event, draft.Resource, recordedAt));
            }
            if (retained + appended.Count > maximumEvents)
                throw new InvalidOperationException("The in-memory event store is full.");
            foreach (var stored in appended)
            {
                log.Events.Add(stored.Sequence, stored);
                log.Ids.Add(stored.Event.Id);
            }
            log.Head += appended.Count;
            retained += appended.Count;
            return Task.FromResult(new EventAppendResult(appended, duplicates));
        }
    }

    public Task<IReadOnlyList<StoredIntegrationEvent>> ReadAsync(TicketScope scope, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventLog.MaximumRead);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredIntegrationEvent>>(logs.TryGetValue(scope, out var log)
                ? [.. log.Events.Values.Where(stored => stored.Sequence > afterSequence).Take(limit)]
                : []);
    }

    public Task<EventLogHead> GetHeadAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(Head(scope));
    }

    public Task<int> PruneAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventLog.MaximumPrune);
        lock (gate)
        {
            var victims = logs.Values.SelectMany(log => log.Events.Values.Where(stored => stored.RecordedAt < olderThan).Select(stored => (log, stored)))
                .OrderBy(pair => pair.stored.RecordedAt).ThenBy(pair => pair.stored.Sequence).Take(limit).ToList();
            foreach (var (log, stored) in victims)
            {
                log.Events.Remove(stored.Sequence);
                log.Ids.Remove(stored.Event.Id);
            }
            retained -= victims.Count;
            return Task.FromResult(victims.Count);
        }
    }

    public Task<long?> GetCheckpointAsync(TicketScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IEventLog.RequireCheckpointName(name);
        lock (gate)
            return Task.FromResult(checkpoints.TryGetValue((scope, name), out var value) ? value : (long?)null);
    }

    public Task<bool> SetCheckpointAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IEventLog.RequireCheckpointName(name);
        lock (gate)
        {
            var exists = checkpoints.TryGetValue((scope, name), out var current);
            if (exists ? expected != current : expected is not null)
                return Task.FromResult(false);
            checkpoints[(scope, name)] = value;
            return Task.FromResult(true);
        }
    }

    public Task<StoredEventSubscription?> GetAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(subscriptions.TryGetValue((scope, Require(id)), out var entry) ? entry.Subscription : null);
    }

    public Task<IReadOnlyList<StoredEventSubscription>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredEventSubscription>>([.. subscriptions.Values.Where(entry => entry.Subscription.Scope == scope)
                .Select(entry => entry.Subscription).OrderBy(stored => stored.Subscription.Id.Value, StringComparer.Ordinal)]);
    }

    public Task<EventWriteResult<StoredEventSubscription>> CreateAsync(TicketScope scope, EventSubscription subscription, long cursor, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        lock (gate)
        {
            if (subscriptions.ContainsKey((scope, subscription.Id)))
                return Task.FromResult<EventWriteResult<StoredEventSubscription>>(new EventWriteRejected(EventWriteRejection.Duplicate));
            if (subscriptions.Values.Count(entry => entry.Subscription.Scope == scope) >= IEventSubscriptionStore.MaximumSubscriptionsPerWorkspace)
                return Task.FromResult<EventWriteResult<StoredEventSubscription>>(new EventWriteRejected(EventWriteRejection.LimitReached));
            var stored = new StoredEventSubscription(scope, subscription, 1, EventText.Instant(createdAt));
            subscriptions[(scope, subscription.Id)] = new Entry(stored, new EventDeliveryState(cursor, 0, EventText.Instant(createdAt), null, null));
            return Task.FromResult<EventWriteResult<StoredEventSubscription>>(new EventWriteApplied<StoredEventSubscription>(stored));
        }
    }

    public Task<EventWriteResult<StoredEventSubscription>> UpdateAsync(TicketScope scope, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(subscription);
        lock (gate)
        {
            if (!subscriptions.TryGetValue((scope, subscription.Id), out var entry))
                return Task.FromResult<EventWriteResult<StoredEventSubscription>>(EventWriteNotFound.Instance);
            if (entry.Subscription.Revision != expectedRevision)
                return Task.FromResult<EventWriteResult<StoredEventSubscription>>(new EventWriteConflict(entry.Subscription.Revision));
            entry.Subscription = entry.Subscription with { Subscription = subscription, Revision = expectedRevision + 1 };
            return Task.FromResult<EventWriteResult<StoredEventSubscription>>(new EventWriteApplied<StoredEventSubscription>(entry.Subscription));
        }
    }

    public Task<EventWriteResult<StoredEventSubscription>> DeleteAsync(TicketScope scope, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            if (!subscriptions.TryGetValue((scope, Require(id)), out var entry))
                return Task.FromResult<EventWriteResult<StoredEventSubscription>>(EventWriteNotFound.Instance);
            if (entry.Subscription.Revision != expectedRevision)
                return Task.FromResult<EventWriteResult<StoredEventSubscription>>(new EventWriteConflict(entry.Subscription.Revision));
            subscriptions.Remove((scope, id));
            return Task.FromResult<EventWriteResult<StoredEventSubscription>>(new EventWriteApplied<StoredEventSubscription>(entry.Subscription));
        }
    }

    public Task<EventDeliveryState?> GetStateAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(subscriptions.TryGetValue((scope, Require(id)), out var entry) ? entry.State : null);
    }

    public Task<IReadOnlyList<EventDeliveryClaim>> ClaimAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventSubscriptionStore.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > IEventSubscriptionStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease));
        lock (gate)
        {
            var claims = new List<EventDeliveryClaim>();
            foreach (var entry in subscriptions.Values.Where(entry => entry.Subscription.Subscription.Enabled && entry.State.AvailableAt <= now
                         && (entry.LeaseUntil is null || entry.LeaseUntil <= now) && Head(entry.Subscription.Scope).Last > entry.State.Cursor)
                     .OrderBy(entry => entry.State.AvailableAt).Take(limit))
            {
                entry.Fence++;
                entry.LeaseUntil = EventText.Instant(now + lease);
                claims.Add(new EventDeliveryClaim(entry.Subscription, entry.State, entry.Fence, entry.LeaseUntil.Value));
            }
            return Task.FromResult<IReadOnlyList<EventDeliveryClaim>>(claims);
        }
    }

    public Task<bool> CompleteAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, DateTimeOffset? deliveredAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            if (!Leased(scope, id, fence, out var entry))
                return Task.FromResult(false);
            entry.State = new EventDeliveryState(Math.Max(cursor, entry.State.Cursor), 0, entry.State.AvailableAt, null, EventText.Instant(deliveredAt) ?? entry.State.LastDeliveredAt);
            entry.LeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> FailAsync(TicketScope scope, EventSubscriptionId id, long fence, int attempts, DateTimeOffset retryAt, string error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IEventSubscriptionStore.RequireError(error);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        lock (gate)
        {
            if (!Leased(scope, id, fence, out var entry))
                return Task.FromResult(false);
            entry.State = entry.State with { Attempts = attempts, AvailableAt = EventText.Instant(retryAt), LastError = error };
            entry.LeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeadLetterAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, IReadOnlyList<EventDeadLetter> letters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(letters);
        lock (gate)
        {
            if (!Leased(scope, id, fence, out var entry))
                return Task.FromResult(false);
            foreach (var letter in letters)
                entry.DeadLetters[letter.Sequence] = letter with { DeadLetteredAt = EventText.Instant(letter.DeadLetteredAt) };
            entry.State = new EventDeliveryState(Math.Max(cursor, entry.State.Cursor), 0, entry.State.AvailableAt, letters.Count > 0 ? letters[0].Error : null, entry.State.LastDeliveredAt);
            entry.LeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReplayAsync(TicketScope scope, EventSubscriptionId id, long afterSequence, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        lock (gate)
        {
            if (!subscriptions.TryGetValue((scope, Require(id)), out var entry))
                return Task.FromResult(false);
            foreach (var sequence in entry.DeadLetters.Keys.Where(sequence => sequence > afterSequence).ToList())
                entry.DeadLetters.Remove(sequence);
            entry.State = new EventDeliveryState(afterSequence, 0, EventText.Instant(now), null, entry.State.LastDeliveredAt);
            entry.Fence++;
            entry.LeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<EventDeadLetter>> ListDeadLettersAsync(TicketScope scope, EventSubscriptionId id, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventSubscriptionStore.MaximumDeadLetterRead);
        lock (gate)
            return Task.FromResult<IReadOnlyList<EventDeadLetter>>(subscriptions.TryGetValue((scope, Require(id)), out var entry)
                ? [.. entry.DeadLetters.Values.Where(letter => letter.Sequence > afterSequence).Take(limit)]
                : []);
    }

    public Task<int> PruneDeadLettersAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventLog.MaximumPrune);
        lock (gate)
        {
            var victims = subscriptions.Values.SelectMany(entry => entry.DeadLetters.Values.Where(letter => letter.DeadLetteredAt < olderThan).Select(letter => (entry, letter)))
                .OrderBy(pair => pair.letter.DeadLetteredAt).Take(limit).ToList();
            foreach (var (entry, letter) in victims)
                entry.DeadLetters.Remove(letter.Sequence);
            return Task.FromResult(victims.Count);
        }
    }

    private bool Leased(TicketScope scope, EventSubscriptionId id, long fence, out Entry entry)
    {
        if (!subscriptions.TryGetValue((scope, Require(id)), out entry!))
            return false;
        return entry.Fence == fence && entry.LeaseUntil is { } until && until > time.GetUtcNow();
    }

    private ScopeLog Log(TicketScope scope)
    {
        if (!logs.TryGetValue(scope, out var log))
            logs[scope] = log = new ScopeLog();
        return log;
    }

    private EventLogHead Head(TicketScope scope) =>
        logs.TryGetValue(scope, out var log) ? new EventLogHead(log.Events.Count > 0 ? log.Events.Keys[0] : null, log.Head) : new EventLogHead(null, 0);

    private static EventSubscriptionId Require(EventSubscriptionId id) => id.IsInitialized ? id : throw new ArgumentException("A subscription identifier is required.", nameof(id));
}
