using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>The store write was applied; <see cref="Value"/> is the stored state (for deletes, the removed state).</summary>
public sealed record SlaWriteApplied<T>(T Value);

/// <summary>No record exists for the key in the requested scope. Nothing was written.</summary>
public sealed record SlaWriteNotFound
{
    private SlaWriteNotFound()
    {
    }

    public static SlaWriteNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match, or a create found an existing record. Nothing was written.</summary>
public sealed record SlaWriteConflict(long CurrentRevision);

public enum SlaWriteRejection
{
    /// <summary>A per-workspace capacity would be exceeded.</summary>
    CapacityExceeded = 1,
}

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record SlaWriteRejected(SlaWriteRejection Reason);

/// <summary>Outcome of an SLA store write. Switch over every case without a discard arm.</summary>
public union SlaWriteResult<T>(SlaWriteApplied<T>, SlaWriteNotFound, SlaWriteConflict, SlaWriteRejected);

/// <summary>
/// Workspace-scoped SLA policies keyed by <see cref="SlaPolicyId"/>. Contract: scope-confined; creates start at revision 1 (conflict
/// when present); updates and deletes are compare-and-set on revision; at most <see cref="MaximumPoliciesPerWorkspace"/> policies per
/// scope, checked atomically with the insert. Definitions persist through a versioned encoding such as <see cref="SlaPolicyCodec"/>.
/// </summary>
public interface ISlaPolicyStore
{
    public const int MaximumPoliciesPerWorkspace = 256;

    Task<StoredSlaPolicy?> GetAsync(TicketScope scope, SlaPolicyId id, CancellationToken cancellationToken = default);

    /// <summary>Policies in <paramref name="scope"/> ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredSlaPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<SlaWriteResult<StoredSlaPolicy>> CreateAsync(TicketScope scope, SlaPolicy policy, CancellationToken cancellationToken = default);

    Task<SlaWriteResult<StoredSlaPolicy>> UpdateAsync(TicketScope scope, SlaPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<SlaWriteResult<StoredSlaPolicy>> DeleteAsync(TicketScope scope, SlaPolicyId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// A leased claim of one due timer. <see cref="Fence"/> increases with every claim of the timer; <see cref="ISlaTrackerStore.ReleaseAsync"/>
/// succeeds only with the current fence while the lease is held.
/// </summary>
public sealed record SlaTimerClaim(TicketScope Scope, ConversationId Conversation, DateTimeOffset DueAt, long Fence, DateTimeOffset LeaseUntil)
{
    public override string ToString() => $"SlaTimerClaim {{ Conversation = {Conversation}, Fence = {Fence} }}";
}

public enum SlaRiskFilter
{
    /// <summary>Not breached, and a running clock passed its at-risk threshold.</summary>
    AtRisk = 1,

    /// <summary>An active clock is breached, or a running clock is past due.</summary>
    Breached = 2,

    /// <summary>Either of the above.</summary>
    AtRiskOrBreached = 3,
}

/// <summary>Keyset position: the last row's urgency instant and conversation identifier (ascending).</summary>
public sealed record SlaRiskCursor
{
    public SlaRiskCursor(DateTimeOffset urgencyAt, ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        UrgencyAt = SlaText.Instant(urgencyAt);
        Conversation = conversation;
    }

    public DateTimeOffset UrgencyAt { get; }
    public ConversationId Conversation { get; }
}

/// <summary>A bounded risk list query ordered by <see cref="SlaSchedule.UrgencyAt"/> ascending, then conversation identifier (ordinal).</summary>
public sealed record SlaRiskQuery
{
    public const int MaximumLimit = 100;
    public const int DefaultLimit = 25;

    public SlaRiskQuery(SlaRiskFilter filter = SlaRiskFilter.AtRiskOrBreached, InboxId? inbox = null, int limit = DefaultLimit, SlaRiskCursor? after = null)
    {
        Filter = SlaText.Defined(filter, nameof(filter));
        if (inbox == default(InboxId))
            throw new ArgumentException("The inbox must be initialized when supplied.", nameof(inbox));
        if (limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limits are 1-{MaximumLimit}.");
        Inbox = inbox;
        Limit = limit;
        After = after;
    }

    public SlaRiskFilter Filter { get; }
    public InboxId? Inbox { get; }
    public int Limit { get; }
    public SlaRiskCursor? After { get; }

    public SlaRiskQuery WithAfter(SlaRiskCursor? after) => new(Filter, Inbox, Limit, after);

    /// <summary>True when <paramref name="tracker"/> satisfies the filter and inbox at <paramref name="now"/> (not the cursor).</summary>
    public bool Matches(SlaTracker tracker, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (Inbox is { } inbox && tracker.Inbox != inbox || tracker.Schedule.UrgencyAt is null)
            return false;
        return Filter switch
        {
            SlaRiskFilter.AtRisk => tracker.Schedule.IsAtRisk(now),
            SlaRiskFilter.Breached => tracker.Schedule.IsBreached(now),
            SlaRiskFilter.AtRiskOrBreached => tracker.Schedule.IsAtRisk(now) || tracker.Schedule.IsBreached(now),
            _ => false,
        };
    }

    /// <summary>True when <paramref name="tracker"/> sorts after <see cref="After"/> (or there is no cursor).</summary>
    public bool IsAfterCursor(SlaTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (After is not { } cursor)
            return true;
        if (tracker.Schedule.UrgencyAt is not { } urgency)
            return false;
        return urgency > cursor.UrgencyAt || (urgency == cursor.UrgencyAt && string.CompareOrdinal(tracker.Conversation.Value, cursor.Conversation.Value) > 0);
    }
}

/// <summary>One page; <see cref="Next"/> is <see langword="null"/> at the end.</summary>
public sealed record SlaTrackerPage(IReadOnlyList<StoredSlaTracker> Trackers, SlaRiskCursor? Next);

/// <summary>
/// Durable per-conversation SLA trackers with a timer table. Contract:
/// <list type="bullet">
/// <item>Every tracker operation is confined to one <see cref="TicketScope"/>; claims are store-wide and carry their scope.</item>
/// <item><see cref="WriteAsync"/> creates (expected revision <see langword="null"/>, revision 1, conflict when present) or
/// compare-and-set replaces the tracker and, atomically, sets its timer to <see cref="SlaSchedule.NextCheckAt"/> and clears any
/// timer lease.</item>
/// <item><see cref="ClaimDueAsync"/> leases timers with a due instant at or before the store clock's now, earliest first, whose lease is
/// free or expired, incrementing each timer's fence. Concurrent claimers never receive the same timer under a live lease.</item>
/// <item><see cref="ReleaseAsync"/> reschedules a claimed timer to <c>retryAt</c> and clears the lease; it returns
/// <see langword="false"/> when the fence is stale or the lease expired.</item>
/// <item><see cref="ListAsync"/> returns exactly the trackers matching <see cref="SlaRiskQuery.Matches"/> at <c>now</c> and the
/// supplied <see cref="AccessFilter"/> over <see cref="SlaTracker.Resource"/>, applied inside the bounded keyset query (never by
/// post-filtering an unbounded result); untranslatable filters return no rows.</item>
/// </list>
/// </summary>
public interface ISlaTrackerStore
{
    public const int MaximumClaim = 100;

    /// <summary>The longest lease a claim may take.</summary>
    public static readonly TimeSpan MaximumLease = TimeSpan.FromHours(1);

    Task<StoredSlaTracker?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<SlaWriteResult<StoredSlaTracker>> WriteAsync(TicketScope scope, SlaTracker tracker, long? expectedRevision, CancellationToken cancellationToken = default);

    Task<SlaWriteResult<StoredSlaTracker>> DeleteAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SlaTimerClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(SlaTimerClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default);

    Task<SlaTrackerPage> ListAsync(TicketScope scope, SlaRiskQuery query, DateTimeOffset now, AccessFilter filter, CancellationToken cancellationToken = default);
}
