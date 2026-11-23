using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

public enum SlaWriteRejection
{
    /// <summary>A per-workspace capacity would be exceeded.</summary>
    CapacityExceeded = 1,
}

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

    Task<StoreWriteResult<StoredSlaPolicy, SlaWriteRejection>> CreateAsync(TicketScope scope, SlaPolicy policy, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredSlaPolicy, SlaWriteRejection>> UpdateAsync(TicketScope scope, SlaPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredSlaPolicy, SlaWriteRejection>> DeleteAsync(TicketScope scope, SlaPolicyId id, long expectedRevision, CancellationToken cancellationToken = default);
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
            SlaRiskFilter.Breached => Breached(tracker.Schedule, now),
            SlaRiskFilter.AtRiskOrBreached => tracker.Schedule.IsAtRisk(now) || Breached(tracker.Schedule, now),
            _ => false,
        };
    }

    /// <summary>Breached as of <paramref name="now"/>: the urgency instant has passed (bounds the index range scan) and a clock is breached or past due.</summary>
    private static bool Breached(SlaSchedule schedule, DateTimeOffset now) => schedule.UrgencyAt <= now && schedule.IsBreached(now);

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
/// compare-and-set replaces the tracker and, atomically, sets its timer to <see cref="SlaSchedule.NextCheckAt"/>; it clears the timer
/// lease only for the claim that holds it (matching fence), so another worker's lease survives writes from the outbox path.</item>
/// <item><see cref="ClaimDueAsync"/> leases timers with a due instant at or before the store's lease clock (PostgreSQL: database time unless
/// a test clock is injected), earliest first, whose lease is
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

    /// <summary>
    /// Creates or compare-and-set replaces the tracker and sets its timer to <see cref="SlaSchedule.NextCheckAt"/>. The timer lease is
    /// cleared only when <paramref name="release"/> is the current claim (same fence); otherwise a live lease held by another worker is
    /// left untouched.
    /// </summary>
    Task<StoreWriteResult<StoredSlaTracker, SlaWriteRejection>> WriteAsync(TicketScope scope, SlaTracker tracker, long? expectedRevision, SlaTimerClaim? release = null,
        CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredSlaTracker, SlaWriteRejection>> DeleteAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SlaTimerClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(SlaTimerClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default);

    Task<SlaTrackerPage> ListAsync(TicketScope scope, SlaRiskQuery query, DateTimeOffset now, AccessFilter filter, CancellationToken cancellationToken = default);
}
