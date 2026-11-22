using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Routing;

/// <summary>The write committed.</summary>
public sealed record RoutingWriteApplied<T>(T Value);

/// <summary>The record does not exist in the scope. Nothing was written.</summary>
public sealed record RoutingWriteNotFound
{
    private RoutingWriteNotFound()
    {
    }

    public static RoutingWriteNotFound Instance { get; } = new();
}

/// <summary>The revision did not match, or a create found an existing record. Nothing was written.</summary>
public sealed record RoutingWriteConflict(long CurrentRevision);

public enum RoutingWriteRejection
{
    /// <summary>A store capacity limit would be exceeded.</summary>
    CapacityExceeded = 1,
}

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record RoutingWriteRejected(RoutingWriteRejection Reason);

/// <summary>Outcome of a revisioned routing write. Switch over every case without a discard arm.</summary>
public union RoutingWriteResult<T>(RoutingWriteApplied<T>, RoutingWriteNotFound, RoutingWriteConflict, RoutingWriteRejected);

/// <summary>
/// Workspace-scoped routing policies keyed by <see cref="Workspaces.RoutingPolicyId"/>: creates start at revision 1, updates and
/// deletes are compare-and-set, at most <see cref="MaximumPoliciesPerWorkspace"/> per scope (checked atomically).
/// </summary>
public interface IRoutingPolicyStore
{
    public const int MaximumPoliciesPerWorkspace = 256;

    Task<StoredRoutingPolicy?> GetAsync(TicketScope scope, Workspaces.RoutingPolicyId id, CancellationToken cancellationToken = default);

    /// <summary>Policies ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredRoutingPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<RoutingWriteResult<StoredRoutingPolicy>> CreateAsync(TicketScope scope, RoutingPolicy policy, CancellationToken cancellationToken = default);

    Task<RoutingWriteResult<StoredRoutingPolicy>> UpdateAsync(TicketScope scope, RoutingPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<RoutingWriteResult<StoredRoutingPolicy>> DeleteAsync(TicketScope scope, Workspaces.RoutingPolicyId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent routing profiles. Contract:
/// <list type="bullet">
/// <item>Scope-confined; at most <see cref="MaximumAgentsPerWorkspace"/> profiles per scope (checked atomically).</item>
/// <item><see cref="SaveSettingsAsync"/> creates (expected revision <see langword="null"/>, starting Offline at revision 1) or
/// compare-and-set replaces the settings, keeping presence.</item>
/// <item><see cref="SetStatusAsync"/> records the status with a presence expiry of now + ttl (none when Offline) and increments the
/// revision. A transition from an effectively non-offline status to Offline sets <see cref="StoredAgentProfile.ReassignmentPending"/>.
/// <see cref="HeartbeatAsync"/> extends presence of an effectively non-offline agent without changing the revision; it never revives
/// a lapsed agent.</item>
/// <item><see cref="ExpirePresenceAsync"/> (store-wide) records lapsed agents as Offline with a pending reassignment.</item>
/// <item><see cref="ClaimReassignmentsAsync"/> (store-wide) leases pending reassignments with a fence (concurrent claimers never share a
/// live lease); <see cref="CompleteReassignmentAsync"/> clears the flag only with the current fence.</item>
/// </list>
/// Times come from the store's clock.
/// </summary>
public interface IAgentAvailabilityStore
{
    public const int MaximumAgentsPerWorkspace = 10_000;
    public const int MaximumList = 200;
    public const int MaximumAvailableList = 1000;

    Task<StoredAgentProfile?> GetAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default);

    /// <summary>Profiles ordered by agent identifier (ordinal) after <paramref name="after"/>; 1-<see cref="MaximumList"/>.</summary>
    Task<IReadOnlyList<StoredAgentProfile>> ListAsync(TicketScope scope, UserId? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// One keyset page of profiles effectively Online now that match <paramref name="query"/> (positive capacity for its channel kind,
    /// within its agent set, holding its required skills), ordered by agent identifier after <see cref="AvailableAgentQuery.After"/>.
    /// The filters run inside the store query, so callers page through every match.
    /// </summary>
    Task<IReadOnlyList<StoredAgentProfile>> ListAvailableAsync(TicketScope scope, AvailableAgentQuery query, CancellationToken cancellationToken = default);

    Task<RoutingWriteResult<StoredAgentProfile>> SaveSettingsAsync(TicketScope scope, AgentRoutingSettings settings, long? expectedRevision, CancellationToken cancellationToken = default);

    Task<RoutingWriteResult<StoredAgentProfile>> SetStatusAsync(TicketScope scope, UserId agent, AgentStatus status, TimeSpan presenceTtl, CancellationToken cancellationToken = default);

    Task<RoutingWriteResult<StoredAgentProfile>> HeartbeatAsync(TicketScope scope, UserId agent, TimeSpan presenceTtl, CancellationToken cancellationToken = default);

    Task<RoutingWriteResult<StoredAgentProfile>> DeleteAsync(TicketScope scope, UserId agent, long expectedRevision, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentPresenceExpired>> ExpirePresenceAsync(int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentReassignmentClaim>> ClaimReassignmentsAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> CompleteReassignmentAsync(TicketScope scope, UserId agent, long fence, CancellationToken cancellationToken = default);
}

/// <summary>
/// The assignment (load) projection, round-robin cursors and sticky owners. Contract:
/// <list type="bullet">
/// <item>Scope-confined; one row per conversation.</item>
/// <item><see cref="ReserveAsync"/> is atomic across clients (see <see cref="RoutingReservationRequest"/>): at most one live reservation
/// per conversation, an agent's load on a channel never exceeds the capacity named in a successful request, the cursor moves in the
/// same step, and selection equals <see cref="RoutingReservationRequest.Select"/>.</item>
/// <item><see cref="ConfirmAsync"/> turns the caller's reservation into an assigned row at the applied conversation revision and records
/// the contact's sticky owner; <see cref="ReleaseAsync"/> deletes the caller's reservation.</item>
/// <item><see cref="RecordAssignedAsync"/> upserts an assigned row (no capacity check: manual assignments may exceed capacity) and the
/// contact's sticky owner unless the stored row reflects a newer revision. <see cref="ClearAsync"/> deletes the row when it reflects
/// a revision not newer than the given one and is not a live reservation. Rows never move back to an older revision.</item>
/// </list>
/// </summary>
public interface IRoutingAssignmentStore
{
    public const int MaximumList = 200;

    Task<RoutingReservationResult> ReserveAsync(TicketScope scope, RoutingReservationRequest request, CancellationToken cancellationToken = default);

    Task<bool> ConfirmAsync(TicketScope scope, ConversationId conversation, UserId agent, long conversationRevision, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(TicketScope scope, ConversationId conversation, UserId agent, CancellationToken cancellationToken = default);

    Task RecordAssignedAsync(TicketScope scope, RoutingAssignmentRecord record, CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default);

    Task<RoutingAssignment?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);

    /// <summary>The agent's rows (assigned and reserved) ordered by conversation identifier after <paramref name="after"/>; 1-<see cref="MaximumList"/>.</summary>
    Task<IReadOnlyList<RoutingAssignment>> ListForAgentAsync(TicketScope scope, UserId agent, ConversationId? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>The agent's current load per channel kind (rows counting now).</summary>
    Task<IReadOnlyDictionary<Workspaces.ChannelKind, int>> GetLoadAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default);

    Task<StickyOwner?> GetStickyOwnerAsync(TicketScope scope, ContactId contact, CancellationToken cancellationToken = default);

    /// <summary>The agent identifier the round-robin cursor <paramref name="cursorKey"/> last chose, if any.</summary>
    Task<string?> GetCursorAsync(TicketScope scope, string cursorKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Unassigned open conversations waiting for routing. Contract:
/// <list type="bullet">
/// <item><see cref="EnqueueAsync"/> inserts (queued at, next attempt at now; target inbox = conversation inbox) or refreshes the
/// snapshot when its conversation revision is not older, keeping queue position, target, overflow progress and attempts.</item>
/// <item><see cref="ListAsync"/> applies the <see cref="AccessFilter"/> over <see cref="RoutingQueueEntry.Resource"/> inside the bounded
/// keyset query (waiting since, conversation identifier ascending); untranslatable filters return no rows.</item>
/// <item><see cref="ClaimDueAsync"/> (store-wide) leases entries whose next attempt is due and whose lease is free or expired, taken in
/// next-attempt order (index-backed) and returned oldest waiting first; every claim increments the fence; concurrent claimers never
/// share a live lease. <see cref="CompleteAttemptAsync"/> applies the update and frees the lease only with the current fence while the lease is held.</item>
/// <item><see cref="RemoveAsync"/> deletes the entry only when its snapshot revision is not newer than the given revision.</item>
/// <item><see cref="NudgeAsync"/> makes the scope's entries (optionally one target inbox) due now.</item>
/// </list>
/// Times come from the store's clock.
/// </summary>
public interface IRoutingQueueStore
{
    public const int MaximumClaim = 100;
    public static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(10);

    Task<StoredRoutingQueueEntry> EnqueueAsync(TicketScope scope, RoutingQueueSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default);

    Task<StoredRoutingQueueEntry?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<RoutingQueuePage> ListAsync(TicketScope scope, RoutingQueueQuery query, AccessFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoutingQueueClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> CompleteAttemptAsync(TicketScope scope, ConversationId conversation, long fence, RoutingQueueUpdate update, CancellationToken cancellationToken = default);

    Task<int> NudgeAsync(TicketScope scope, InboxId? targetInbox, CancellationToken cancellationToken = default);
}

/// <summary>
/// Conversation presence and soft reply locks. Contract: scope-confined; expired presence and locks are never returned; at most
/// <see cref="MaximumPresencePerConversation"/> live presences per conversation; a live lock held by another user refuses acquisition;
/// re-acquiring one's own lock renews it with a new fence; release requires the holder and current fence. Times come from the store's
/// clock.
/// </summary>
public interface IConversationPresenceStore
{
    public const int MaximumPresencePerConversation = 64;
    public const int MaximumPrune = 10_000;

    Task<PresenceWriteResult> TouchAsync(TicketScope scope, ConversationId conversation, UserId user, ConversationActivity activity, TimeSpan ttl, CancellationToken cancellationToken = default);

    Task<bool> LeaveAsync(TicketScope scope, ConversationId conversation, UserId user, CancellationToken cancellationToken = default);

    /// <summary>Live presences ordered by user identifier (ordinal).</summary>
    Task<IReadOnlyList<ConversationPresence>> ListAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<ReplyLockResult> AcquireLockAsync(TicketScope scope, ConversationId conversation, UserId holder, TimeSpan duration, CancellationToken cancellationToken = default);

    Task<ReplyLock?> GetLockAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<bool> ReleaseLockAsync(TicketScope scope, ConversationId conversation, UserId holder, long fence, CancellationToken cancellationToken = default);

    /// <summary>Deletes up to <paramref name="limit"/> expired presences and locks store-wide.</summary>
    Task<int> PruneAsync(int limit, CancellationToken cancellationToken = default);
}
