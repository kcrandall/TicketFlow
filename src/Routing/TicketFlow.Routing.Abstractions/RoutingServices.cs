using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

public sealed record RoutingValue<T>(T Value);

/// <summary>The record does not exist in the actor's scope.</summary>
public sealed record RoutingNotFound
{
    private RoutingNotFound()
    {
    }

    public static RoutingNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match.</summary>
public sealed record RoutingConflict(long CurrentRevision);

public enum RoutingRefusal
{
    /// <summary>Access denied the operation.</summary>
    Forbidden = 1,

    /// <summary>The input is invalid; see <see cref="RoutingRefused.Errors"/>.</summary>
    Invalid = 2,

    /// <summary>A store limit refused the write, or the principal kind is not supported for the operation.</summary>
    Rejected = 3,
}

/// <summary>The operation was refused. <see cref="Errors"/> are non-sensitive messages.</summary>
public sealed record RoutingRefused(RoutingRefusal Reason, IReadOnlyList<string> Errors)
{
    public static RoutingRefused Forbidden { get; } = new(RoutingRefusal.Forbidden, []);

    public static RoutingRefused Invalid(string error) => new(RoutingRefusal.Invalid, [error]);

    public static RoutingRefused Rejected(string error) => new(RoutingRefusal.Rejected, [error]);
}

/// <summary>Outcome of a routing service operation. Switch over every case without a discard arm.</summary>
public union RoutingResult<T>(RoutingValue<T>, RoutingNotFound, RoutingConflict, RoutingRefused);

/// <summary>Access-checked routing policy management: reads need <c>inbox:read</c>, writes <c>routing:manage</c> (workspace level).</summary>
public interface IRoutingPolicyService
{
    Task<RoutingResult<StoredRoutingPolicy>> GetAsync(PrincipalRef actor, RoutingPolicyId id, CancellationToken cancellationToken = default);

    Task<RoutingResult<IReadOnlyList<StoredRoutingPolicy>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<RoutingResult<StoredRoutingPolicy>> CreateAsync(PrincipalRef actor, RoutingPolicy policy, CancellationToken cancellationToken = default);

    Task<RoutingResult<StoredRoutingPolicy>> UpdateAsync(PrincipalRef actor, RoutingPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<RoutingResult<StoredRoutingPolicy>> DeleteAsync(PrincipalRef actor, RoutingPolicyId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Agent availability. A user principal changing its own status or sending heartbeats needs <c>agent-status:update</c>; changing another
/// agent's status or anyone's capacity and skills needs <c>agent-status:manage</c>; reading profiles needs <c>team:read</c>. All checks are
/// workspace-level. Going offline triggers the inbox policies' offline reassignment through the maintenance worker; coming online makes
/// queued conversations due.
/// </summary>
public interface IAgentAvailabilityService
{
    Task<RoutingResult<StoredAgentProfile>> GetAsync(PrincipalRef actor, UserId agent, CancellationToken cancellationToken = default);

    Task<RoutingResult<IReadOnlyList<StoredAgentProfile>>> ListAsync(PrincipalRef actor, UserId? after = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Creates (expected revision <see langword="null"/>; the agent starts Offline) or replaces an agent's capacity and skills.</summary>
    Task<RoutingResult<StoredAgentProfile>> ConfigureAsync(PrincipalRef actor, AgentRoutingSettings settings, long? expectedRevision = null, CancellationToken cancellationToken = default);

    Task<RoutingResult<StoredAgentProfile>> SetStatusAsync(PrincipalRef actor, UserId agent, AgentStatus status, CancellationToken cancellationToken = default);

    /// <summary>Extends the calling user's presence; lapsed agents must set a status again.</summary>
    Task<RoutingResult<StoredAgentProfile>> HeartbeatAsync(PrincipalRef actor, CancellationToken cancellationToken = default);
}

/// <summary>
/// Collision detection for user principals: viewer/composer presence (<c>conversation:read</c>) and soft reply locks
/// (<c>conversation:reply</c>) on the current conversation. Changes are published to <see cref="IRoutingEventObserver"/>s.
/// </summary>
public interface IConversationCollisionService
{
    Task<RoutingResult<ConversationPresence>> ReportActivityAsync(PrincipalRef actor, ConversationId conversation, ConversationActivity activity, CancellationToken cancellationToken = default);

    Task<RoutingResult<bool>> LeaveAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<RoutingResult<IReadOnlyList<ConversationPresence>>> ListActivityAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default);

    /// <summary>Acquires or renews the caller's lock for <paramref name="duration"/> (default from options).</summary>
    Task<RoutingResult<ReplyLockResult>> AcquireReplyLockAsync(PrincipalRef actor, ConversationId conversation, TimeSpan? duration = null, CancellationToken cancellationToken = default);

    Task<RoutingResult<ReplyLock?>> GetReplyLockAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<RoutingResult<bool>> ReleaseReplyLockAsync(PrincipalRef actor, ConversationId conversation, long fence, CancellationToken cancellationToken = default);
}

/// <summary>A queued conversation as listed: waiting position, target queue and overflow progress (no PII).</summary>
public sealed record RoutingQueueItem(ConversationId Conversation, InboxId Inbox, InboxId TargetInbox, TeamId? Team, DateTimeOffset WaitingSince,
    DateTimeOffset QueuedAt, int OverflowStage, bool WaitExceeded);

public sealed record RoutingQueueListPage(IReadOnlyList<RoutingQueueItem> Items, RoutingQueueCursor? Next);

/// <summary>Unassigned queues: <c>conversation:read</c>, filtered per conversation by the planned Access filter inside the store query.</summary>
public interface IRoutingQueueService
{
    Task<RoutingResult<RoutingQueueListPage>> ListAsync(PrincipalRef actor, RoutingQueueQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The conversation was assigned to <see cref="Agent"/> at conversation revision <see cref="Revision"/>.</summary>
public sealed record RoutingAssigned(UserId Agent, long Revision);

public enum RoutingQueueReason
{
    /// <summary>The target inbox has no routing policy, or its policy is manual.</summary>
    Manual = 1,

    /// <summary>No available agent passed team, skill and Access eligibility.</summary>
    NoEligibleAgent = 2,

    /// <summary>Every eligible agent is at capacity.</summary>
    NoCapacity = 3,

    /// <summary>Another worker holds the reservation, or concurrent writes kept winning.</summary>
    Contended = 4,

    /// <summary>The engine principal was refused by the conversation service (missing <c>conversation:assign</c> grant).</summary>
    Refused = 5,

    /// <summary>The target inbox or its policy is missing.</summary>
    Misconfigured = 6,
}

/// <summary>The conversation stays queued.</summary>
public sealed record RoutingQueued(RoutingQueueReason Reason);

public enum RoutingSettledReason
{
    NotFound = 1,
    Assigned = 2,
    NotOpen = 3,
}

/// <summary>The conversation does not need routing (deleted, already assigned or not open); projections were reconciled.</summary>
public sealed record RoutingSettled(RoutingSettledReason Reason);

/// <summary>Outcome of a routing attempt. Switch over every case without a discard arm.</summary>
public union RoutingOutcome(RoutingAssigned, RoutingQueued, RoutingSettled);

/// <summary>
/// Reconciles a conversation into the queue and assignment projections and, when it is open and unassigned, tries to assign it.
/// Trusted: it reads through the stores and writes through the conversation service as the configured engine principal.
/// </summary>
public interface IRoutingEngine
{
    Task<RoutingOutcome> RouteAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);
}

/// <summary>An agent's status changed (including presence expiry).</summary>
public sealed record AgentStatusChanged(TicketScope Scope, UserId Agent, AgentStatus Status, DateTimeOffset At);

/// <summary>An agent started, changed or ended (<see cref="Activity"/> <see langword="null"/>) presence on a conversation.</summary>
public sealed record ConversationPresenceChanged(TicketScope Scope, ConversationId Conversation, UserId User, ConversationActivity? Activity, DateTimeOffset At);

/// <summary>A reply lock was acquired, renewed or released (<see cref="Holder"/> <see langword="null"/>).</summary>
public sealed record ReplyLockChanged(TicketScope Scope, ConversationId Conversation, UserId? Holder, DateTimeOffset? ExpiresAt, DateTimeOffset At);

/// <summary>The engine assigned a conversation.</summary>
public sealed record ConversationRouted(TicketScope Scope, ConversationId Conversation, UserId Agent, long Revision, DateTimeOffset At);

/// <summary>An overflow rule moved a waiting conversation.</summary>
public sealed record ConversationOverflowed(TicketScope Scope, ConversationId Conversation, OverflowTarget Target, DateTimeOffset At);

/// <summary>A conversation waited longer than its policy's maximum wait (raised once per queue stay).</summary>
public sealed record QueueWaitExceeded(TicketScope Scope, ConversationId Conversation, InboxId TargetInbox, DateTimeOffset At);

/// <summary>A conversation stayed queued after a routing attempt.</summary>
public sealed record ConversationQueued(TicketScope Scope, ConversationId Conversation, InboxId TargetInbox, RoutingQueueReason Reason, DateTimeOffset At);

/// <summary>A routing event for realtime fan-out (E7). Identifiers only. Switch over every case without a discard arm.</summary>
public union RoutingEvent(AgentStatusChanged, ConversationPresenceChanged, ReplyLockChanged, ConversationRouted, ConversationOverflowed, QueueWaitExceeded, ConversationQueued);

/// <summary>
/// Receives routing events in-process after the change is stored. Delivery is best-effort: observer failures are swallowed so routing
/// and collision APIs never fail because realtime fan-out did; durable consumers should use the conversation outbox instead.
/// </summary>
public interface IRoutingEventObserver
{
    Task OnRoutingEventAsync(RoutingEvent routingEvent, CancellationToken cancellationToken = default);
}
