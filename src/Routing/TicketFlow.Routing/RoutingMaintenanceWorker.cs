using Microsoft.Extensions.Options;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>Counts from one maintenance run.</summary>
public sealed record RoutingMaintenanceResult(int Routed, int StillQueued, int Overflowed, int WaitExceeded, int PresenceExpired, int Reassigned, int Pruned);

/// <summary>
/// Store-wide routing maintenance; hosts (E11) call <see cref="RunOnceAsync"/> in their own loop (no hosted service is included). One run:
/// <list type="number">
/// <item>Records lapsed agent presence as offline (pending reassignment) and publishes <see cref="AgentStatusChanged"/>.</item>
/// <item>Claims pending offline reassignments: for every conversation still assigned to the offline agent whose inbox policy says
/// <see cref="OfflineReassignment.ReturnToQueue"/>, unassigns it (fenced on its revision) and routes it again.</item>
/// <item>Claims due queue entries (leased, <c>SKIP LOCKED</c> in PostgreSQL), routes them, applies due overflow rules once each (team:
/// <c>SetTeamAsync</c>; inbox: the entry's target queue), raises <see cref="QueueWaitExceeded"/> once after the policy's maximum wait and
/// schedules the next attempt.</item>
/// <item>Prunes expired conversation presence and reply locks.</item>
/// </list>
/// </summary>
public sealed class RoutingMaintenanceWorker
{
    private readonly IRoutingEngine engine;
    private readonly IConversationStore conversations;
    private readonly IConversationService service;
    private readonly IInboxStore inboxes;
    private readonly IRoutingPolicyStore policies;
    private readonly IAgentAvailabilityStore agents;
    private readonly IRoutingAssignmentStore assignments;
    private readonly IRoutingQueueStore queue;
    private readonly IConversationPresenceStore presence;
    private readonly RoutingEventPublisher events;
    private readonly RoutingOptions options;
    private readonly TimeProvider time;

    public RoutingMaintenanceWorker(IRoutingEngine engine, IConversationStore conversations, IConversationService service, IInboxStore inboxes,
        IRoutingPolicyStore policies, IAgentAvailabilityStore agents, IRoutingAssignmentStore assignments, IRoutingQueueStore queue, IConversationPresenceStore presence,
        IEnumerable<IRoutingEventObserver> observers, IOptions<RoutingOptions> options, TimeProvider time)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.agents = agents ?? throw new ArgumentNullException(nameof(agents));
        this.assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.presence = presence ?? throw new ArgumentNullException(nameof(presence));
        events = new RoutingEventPublisher(observers ?? throw new ArgumentNullException(nameof(observers)));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<RoutingMaintenanceResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var runCache = new RunPolicyCache();
        var expired = await agents.ExpirePresenceAsync(options.MaintenanceBatchSize, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < expired.Count; i++)
            await events.PublishAsync(new AgentStatusChanged(expired[i].Scope, expired[i].Agent, AgentStatus.Offline, expired[i].At), cancellationToken).ConfigureAwait(false);

        var reassigned = 0;
        var reassignClaims = await agents.ClaimReassignmentsAsync(options.MaintenanceBatchSize, options.MaintenanceLease, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < reassignClaims.Count; i++)
            reassigned += await ReassignAsync(reassignClaims[i], runCache, cancellationToken).ConfigureAwait(false);

        int routed = 0, queued = 0, overflowed = 0, waitExceeded = 0;
        var queueClaims = await queue.ClaimDueAsync(options.MaintenanceBatchSize, options.MaintenanceLease, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < queueClaims.Count; i++)
        {
            var claim = queueClaims[i];
            var outcome = await engine.RouteAsync(claim.Scope, claim.Entry.Conversation, cancellationToken).ConfigureAwait(false);
            if (outcome.Value is not RoutingQueued)
            {
                routed++;
                continue;
            }
            queued++;
            var (moved, exceeded) = await AdvanceAsync(claim, runCache, cancellationToken).ConfigureAwait(false);
            overflowed += moved ? 1 : 0;
            waitExceeded += exceeded ? 1 : 0;
        }

        var pruned = await presence.PruneAsync(IConversationPresenceStore.MaximumPrune, cancellationToken).ConfigureAwait(false);
        return new RoutingMaintenanceResult(routed, queued, overflowed, waitExceeded, expired.Count, reassigned, pruned);
    }

    /// <summary>Applies at most one due overflow rule and the max-wait notice, then schedules the next attempt.</summary>
    private async Task<(bool Overflowed, bool WaitExceeded)> AdvanceAsync(RoutingQueueClaim claim, RunPolicyCache cache, CancellationToken cancellationToken)
    {
        // Re-read: the engine may have refreshed the snapshot (the claim fence still guards the update).
        var entry = (await queue.GetAsync(claim.Scope, claim.Entry.Conversation, cancellationToken).ConfigureAwait(false))?.Entry ?? claim.Entry;
        var now = time.GetUtcNow();
        var waited = now - entry.QueuedAt;
        var next = now + options.RetryDelay;
        var target = entry.TargetInbox;
        var stage = entry.OverflowStage;
        var exceeded = entry.WaitExceeded;
        var overflowed = false;
        var raisedWait = false;
        var policy = await PolicyAsync(claim.Scope, target, cache, cancellationToken).ConfigureAwait(false);
        if (policy is not null)
        {
            if (stage < policy.Overflow.Count && stage < RoutingPolicy.MaximumOverflowRules)
            {
                var rule = policy.Overflow[stage];
                if (waited >= rule.After)
                {
                    stage++;
                    overflowed = true;
                    switch (rule.Target)
                    {
                        case OverflowToTeam team:
                            await service.SetTeamAsync(new ApiClientPrincipal(claim.Scope, options.EngineClient), entry.Conversation, team.Team, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                            break;
                        case OverflowToInbox inbox:
                            target = inbox.Inbox;
                            break;
                    }
                    await events.PublishAsync(new ConversationOverflowed(claim.Scope, entry.Conversation, rule.Target, now), cancellationToken).ConfigureAwait(false);
                    next = now;
                }
                else if (entry.QueuedAt + rule.After < next)
                    next = entry.QueuedAt + rule.After;
            }
            if (!exceeded && policy.MaximumWait is { } maximum)
            {
                if (waited >= maximum)
                {
                    exceeded = raisedWait = true;
                    await events.PublishAsync(new QueueWaitExceeded(claim.Scope, entry.Conversation, entry.TargetInbox, now), cancellationToken).ConfigureAwait(false);
                }
                else if (entry.QueuedAt + maximum < next)
                    next = entry.QueuedAt + maximum;
            }
        }
        await queue.CompleteAttemptAsync(claim.Scope, entry.Conversation, claim.Fence, new RoutingQueueUpdate(next, target, stage, exceeded), cancellationToken)
            .ConfigureAwait(false);
        return (overflowed, raisedWait);
    }

    private async Task<RoutingPolicy?> PolicyAsync(TicketScope scope, InboxId inbox, RunPolicyCache cache, CancellationToken cancellationToken)
    {
        if (cache.InboxPolicies.TryGetValue((scope, inbox), out var cachedPolicy))
            return cachedPolicy;
        RoutingPolicy? resolved = null;
        if ((await inboxes.GetAsync(scope, inbox, cancellationToken).ConfigureAwait(false))?.Inbox.RoutingPolicy is { } id)
        {
            if (!cache.Policies.TryGetValue((scope, id), out resolved))
            {
                resolved = (await policies.GetAsync(scope, id, cancellationToken).ConfigureAwait(false))?.Policy;
                cache.Policies[(scope, id)] = resolved;
            }
        }
        cache.InboxPolicies[(scope, inbox)] = resolved;
        return resolved;
    }

    private async Task<int> ReassignAsync(AgentReassignmentClaim claim, RunPolicyCache cache, CancellationToken cancellationToken)
    {
        var profile = await agents.GetAsync(claim.Scope, claim.Agent, cancellationToken).ConfigureAwait(false);
        var reassigned = 0;
        if (profile is not null && profile.Presence.EffectiveStatus(time.GetUtcNow()) == AgentStatus.Offline)
        {
            ConversationId? after = null;
            while (true)
            {
                var page = await assignments.ListForAgentAsync(claim.Scope, claim.Agent, after, IRoutingAssignmentStore.MaximumList, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < page.Count; i++)
                {
                    var row = page[i];
                    if (row.State != RoutingAssignmentState.Assigned)
                        continue;
                    // Check the inbox's offline policy before loading the conversation; Keep policies skip the conversation read entirely.
                    var policy = await PolicyAsync(claim.Scope, row.Inbox, cache, cancellationToken).ConfigureAwait(false);
                    if (policy?.OnAgentOffline != OfflineReassignment.ReturnToQueue)
                        continue;
                    if (await ReturnToQueueAsync(claim.Scope, row.Conversation, claim.Agent, cache, cancellationToken).ConfigureAwait(false))
                        reassigned++;
                }
                if (page.Count < IRoutingAssignmentStore.MaximumList)
                    break;
                after = page[^1].Conversation;
            }
        }
        await agents.CompleteReassignmentAsync(claim.Scope, claim.Agent, claim.Fence, cancellationToken).ConfigureAwait(false);
        return reassigned;
    }

    private async Task<bool> ReturnToQueueAsync(TicketScope scope, ConversationId id, UserId agent, RunPolicyCache cache, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= options.MaximumRouteAttempts; attempt++)
        {
            var stored = await conversations.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            if (stored?.Conversation.Assignee != agent)
            {
                await engine.RouteAsync(scope, id, cancellationToken).ConfigureAwait(false);
                return false;
            }
            var policy = await PolicyAsync(scope, stored.Conversation.Inbox, cache, cancellationToken).ConfigureAwait(false);
            if (policy?.OnAgentOffline != OfflineReassignment.ReturnToQueue)
                return false;
            var result = await service.AssignAsync(new ApiClientPrincipal(scope, options.EngineClient), id, null, stored.Revision, cancellationToken).ConfigureAwait(false);
            if (result.Value is ConversationConflict)
                continue;
            await engine.RouteAsync(scope, id, cancellationToken).ConfigureAwait(false);
            return result.Value is ConversationApplied;
        }
        return false;
    }

    private sealed class RunPolicyCache
    {
        public Dictionary<(TicketScope Scope, InboxId Inbox), RoutingPolicy?> InboxPolicies { get; } = [];
        public Dictionary<(TicketScope Scope, RoutingPolicyId Policy), RoutingPolicy?> Policies { get; } = [];
    }
}
