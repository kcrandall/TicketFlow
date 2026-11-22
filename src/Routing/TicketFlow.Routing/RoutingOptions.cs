using TicketFlow.Core;

namespace TicketFlow.Routing;

public sealed class RoutingOptions
{
    /// <summary>
    /// The API client the engine acts as when it assigns, unassigns and sets teams. It must hold <c>conversation:assign</c> in every
    /// routed workspace (automation and system principals cannot hold grants). Default <c>ticketflow-routing</c>.
    /// </summary>
    public ApiClientId EngineClient { get; set; } = new("ticketflow-routing");

    /// <summary>How long agent presence lasts without a heartbeat (default 2 minutes, at most 24 hours).</summary>
    public TimeSpan AgentPresenceTtl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long conversation viewing/composing presence lasts without a refresh (default 30 seconds).</summary>
    public TimeSpan ConversationPresenceTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Default reply lock duration (default 2 minutes).</summary>
    public TimeSpan ReplyLockDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Longest reply lock a caller may request (default 15 minutes, at most 24 hours).</summary>
    public TimeSpan MaximumReplyLockDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Reservation lease while the engine calls <c>AssignAsync</c> (default 30 seconds, at most 10 minutes).</summary>
    public TimeSpan ReservationLease { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Select-reserve-assign attempts per routing call when concurrent writers win (1-16, default 4).</summary>
    public int MaximumRouteAttempts { get; set; } = 4;

    /// <summary>Delay before a queued conversation is retried by the maintenance worker (default 30 seconds).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on reusing one agent's planned eligibility filter <em>within a run</em> (default 15 seconds; zero disables reuse). Plans
    /// live only in the scoped engine instance — one DI scope, typically one dispatcher or maintenance run — and are never shared across
    /// runs, so revoked grants, team changes and removed agents take effect on the next run at the latest.
    /// </summary>
    public TimeSpan RunEligibilityPlanLifetime { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Items per maintenance step (queue claims, expiries, reassignments; 1-100, default 50).</summary>
    public int MaintenanceBatchSize { get; set; } = 50;

    /// <summary>Lease for claimed queue entries and reassignments (default 1 minute, at most 10 minutes).</summary>
    public TimeSpan MaintenanceLease { get; set; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        static bool Positive(TimeSpan value, TimeSpan maximum) => value > TimeSpan.Zero && value <= maximum;
        if (EngineClient == default || !Positive(AgentPresenceTtl, TimeSpan.FromHours(24)) || !Positive(ConversationPresenceTtl, TimeSpan.FromHours(24))
            || !Positive(MaximumReplyLockDuration, TimeSpan.FromHours(24)) || !Positive(ReplyLockDuration, MaximumReplyLockDuration)
            || !Positive(ReservationLease, RoutingReservationRequest.MaximumLease) || MaximumRouteAttempts is < 1 or > 16
            || !Positive(RetryDelay, TimeSpan.FromDays(1)) || RunEligibilityPlanLifetime < TimeSpan.Zero || RunEligibilityPlanLifetime > TimeSpan.FromMinutes(10)
            || MaintenanceBatchSize is < 1 or > IRoutingQueueStore.MaximumClaim || !Positive(MaintenanceLease, IRoutingQueueStore.MaximumLease))
            throw new InvalidOperationException("Routing options are invalid.");
    }
}
