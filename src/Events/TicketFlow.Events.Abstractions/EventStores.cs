using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>Appended events and the number of drafts whose event id already existed (ignored).</summary>
public sealed record EventAppendResult(IReadOnlyList<StoredIntegrationEvent> Appended, int Duplicates);

/// <summary>The retained range of a workspace log: <see cref="First"/> is <see langword="null"/> when nothing is retained; <see cref="Last"/> is the head (0 when empty).</summary>
public sealed record EventLogHead(long? First, long Last);

/// <summary>
/// The durable integration event log. Contract:
/// <list type="bullet">
/// <item>Every scoped operation is confined to one <see cref="TicketScope"/>.</item>
/// <item><see cref="AppendAsync"/> is atomic: drafts whose (scope, event id) already exist — or repeat within the call — are ignored, the
/// rest get gapless sequences (head + 1, head + 2, …) in draft order. Concurrent appenders to one scope serialize, so sequences are never
/// duplicated or skipped and per-source order is preserved.</item>
/// <item><see cref="ReadAsync"/> returns retained events after a sequence in ascending order.</item>
/// <item><see cref="PruneAsync"/> deletes, store-wide, events recorded before a cutoff (oldest first); heads never move backwards, so
/// sequences are never reused.</item>
/// <item>Checkpoints are named per scope, compare-and-set (<c>expected</c> <see langword="null"/> means absent).</item>
/// </list>
/// </summary>
public interface IEventLog
{
    public const int MaximumAppend = 100;
    public const int MaximumRead = 500;
    public const int MaximumPrune = 10_000;
    public const int MaximumCheckpointNameLength = 64;

    Task<EventAppendResult> AppendAsync(TicketScope scope, IReadOnlyList<IntegrationEventDraft> drafts, DateTimeOffset recordedAt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredIntegrationEvent>> ReadAsync(TicketScope scope, long afterSequence, int limit, CancellationToken cancellationToken = default);

    Task<EventLogHead> GetHeadAsync(TicketScope scope, CancellationToken cancellationToken = default);

    /// <summary>Deletes at most <paramref name="limit"/> (1-10000) events recorded before <paramref name="olderThan"/>, across all scopes.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default);

    Task<long?> GetCheckpointAsync(TicketScope scope, string name, CancellationToken cancellationToken = default);

    Task<bool> SetCheckpointAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default);

    /// <summary>Validates an append batch: 1-<see cref="MaximumAppend"/> non-null drafts.</summary>
    public static void RequireDrafts(IReadOnlyList<IntegrationEventDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count is 0 or > MaximumAppend || drafts.Any(draft => draft is null))
            throw new ArgumentException($"Appends carry 1-{MaximumAppend} drafts.", nameof(drafts));
    }

    public static string RequireCheckpointName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!EventText.IsToken(name, MaximumCheckpointNameLength))
            throw new ArgumentException("Checkpoint names are 1-64 characters of a-z, 0-9, '-', '_' or '.'.", nameof(name));
        return name;
    }
}

public enum EventWriteRejection
{
    /// <summary>The workspace already has <see cref="IEventSubscriptionStore.MaximumSubscriptionsPerWorkspace"/> subscriptions.</summary>
    LimitReached = 1,

    /// <summary>A subscription with this identifier already exists.</summary>
    Duplicate = 2,
}

public sealed record EventWriteApplied<T>(T Value);

public sealed record EventWriteNotFound
{
    private EventWriteNotFound()
    {
    }

    public static EventWriteNotFound Instance { get; } = new();
}

public sealed record EventWriteConflict(long CurrentRevision);

public sealed record EventWriteRejected(EventWriteRejection Reason);

/// <summary>Outcome of a subscription store write. Switch over every case without a discard arm.</summary>
public union EventWriteResult<T>(EventWriteApplied<T>, EventWriteNotFound, EventWriteConflict, EventWriteRejected);

/// <summary>
/// Durable subscriptions and their delivery state. Contract:
/// <list type="bullet">
/// <item>Subscriptions are scope-confined, created at revision 1 with a delivery cursor, updated and deleted compare-and-set; at most
/// <see cref="MaximumSubscriptionsPerWorkspace"/> per scope (checked atomically). Deleting removes delivery state and dead letters.</item>
/// <item><see cref="ClaimAsync"/> (store-wide) leases up to <c>limit</c> enabled subscriptions whose retry time has come, whose lease is free
/// or expired and whose scope log head is beyond their cursor; every claim increments the fence. Concurrent claimers never receive the same
/// subscription under a live lease.</item>
/// <item><see cref="CompleteAsync"/>, <see cref="FailAsync"/> and <see cref="DeadLetterAsync"/> succeed only with the current fence while the
/// lease is held, and release it. Completion and dead-lettering advance the cursor (never backwards) and reset attempts; dead-lettering
/// records its letters atomically.</item>
/// <item><see cref="ReplayAsync"/> rewinds the cursor, resets attempts, deletes dead letters at or after the replay position and increments
/// the fence (an in-flight lease can no longer complete).</item>
/// </list>
/// </summary>
public interface IEventSubscriptionStore
{
    public const int MaximumSubscriptionsPerWorkspace = 64;
    public const int MaximumClaim = 100;
    public const int MaximumDeadLetterRead = 200;
    public const int MaximumErrorLength = 128;

    /// <summary>The longest lease a claim may take.</summary>
    public static readonly TimeSpan MaximumLease = TimeSpan.FromHours(1);

    Task<StoredEventSubscription?> GetAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default);

    /// <summary>The scope's subscriptions ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredEventSubscription>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<EventWriteResult<StoredEventSubscription>> CreateAsync(TicketScope scope, EventSubscription subscription, long cursor, DateTimeOffset createdAt, CancellationToken cancellationToken = default);

    Task<EventWriteResult<StoredEventSubscription>> UpdateAsync(TicketScope scope, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default);

    Task<EventWriteResult<StoredEventSubscription>> DeleteAsync(TicketScope scope, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<EventDeliveryState?> GetStateAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EventDeliveryClaim>> ClaimAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, DateTimeOffset? deliveredAt, CancellationToken cancellationToken = default);

    Task<bool> FailAsync(TicketScope scope, EventSubscriptionId id, long fence, int attempts, DateTimeOffset retryAt, string error, CancellationToken cancellationToken = default);

    Task<bool> DeadLetterAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, IReadOnlyList<EventDeadLetter> letters, CancellationToken cancellationToken = default);

    /// <summary>Rewinds delivery to events after <paramref name="afterSequence"/>; false when the subscription does not exist.</summary>
    Task<bool> ReplayAsync(TicketScope scope, EventSubscriptionId id, long afterSequence, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Dead letters with a sequence greater than <paramref name="afterSequence"/>, ascending, at most <paramref name="limit"/> (1-200).</summary>
    Task<IReadOnlyList<EventDeadLetter>> ListDeadLettersAsync(TicketScope scope, EventSubscriptionId id, long afterSequence, int limit, CancellationToken cancellationToken = default);

    /// <summary>Deletes at most <paramref name="limit"/> (1-10000) dead letters older than <paramref name="olderThan"/>, across all scopes.</summary>
    Task<int> PruneDeadLettersAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default);

    public static string RequireError(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!EventText.IsToken(error, MaximumErrorLength))
            throw new ArgumentException("Error codes are 1-128 characters of a-z, 0-9, '-', '_' or '.'.", nameof(error));
        return error;
    }
}
