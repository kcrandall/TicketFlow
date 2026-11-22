using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>
/// Default <see cref="IRoutingEngine"/>. Each call reconciles the conversation into the queue and assignment projections (revision
/// guarded, so stale callers never overwrite newer state) and, when the conversation is open and unassigned, routes it:
/// <list type="number">
/// <item>Policy: the queue entry's target inbox → its <see cref="RoutingPolicyId"/> → <see cref="RoutingPolicy"/> (none or manual: stays queued).</item>
/// <item>Pool: effectively online agents, restricted to the conversation team (or the policy team), required skills and a positive
/// capacity for the conversation's channel kind.</item>
/// <item>Eligibility: each agent must be permitted <c>conversation:reply</c> on the conversation. One Access plan per agent (built from
/// the agent's current team snapshot) is evaluated against the conversation resource and reused for
/// <see cref="RoutingOptions.EligibilityCacheDuration"/> within this engine instance, instead of a point check per agent per conversation.</item>
/// <item>Reservation: one atomic store step selects the agent (sticky preference, round robin or least busy), checks capacity and moves
/// the cursor.</item>
/// <item>Fenced assignment: <c>AssignAsync(expectedRevision)</c> as the engine API client. A conflict releases the reservation and retries
/// (bounded by <see cref="RoutingOptions.MaximumRouteAttempts"/>); success confirms the reservation.</item>
/// </list>
/// </summary>
public sealed class RoutingEngine : IRoutingEngine
{
    private static readonly ChannelKind UnknownChannel = new("unknown");
    private static readonly RoutingOutcome? Retry = null;
    private static readonly IReadOnlyDictionary<string, int> NoSkills = new Dictionary<string, int>();

    private readonly IConversationStore conversations;
    private readonly IConversationService service;
    private readonly IInboxStore inboxes;
    private readonly ITeamStore teams;
    private readonly ITeamMembershipResolver memberships;
    private readonly IAccessQueryPlanner planner;
    private readonly IRoutingPolicyStore policies;
    private readonly IAgentAvailabilityStore agents;
    private readonly IRoutingAssignmentStore assignments;
    private readonly IRoutingQueueStore queue;
    private readonly RoutingEventPublisher events;
    private readonly RoutingOptions options;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<(TicketScope Scope, UserId Agent), (AccessFilter Filter, DateTimeOffset Until)> eligibility = new();

    public RoutingEngine(IConversationStore conversations, IConversationService service, IInboxStore inboxes, ITeamStore teams, ITeamMembershipResolver memberships,
        IAccessQueryPlanner planner, IRoutingPolicyStore policies, IAgentAvailabilityStore agents, IRoutingAssignmentStore assignments, IRoutingQueueStore queue,
        IEnumerable<IRoutingEventObserver> observers, IOptions<RoutingOptions> options, TimeProvider time)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.teams = teams ?? throw new ArgumentNullException(nameof(teams));
        this.memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.agents = agents ?? throw new ArgumentNullException(nameof(agents));
        this.assignments = assignments ?? throw new ArgumentNullException(nameof(assignments));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        events = new RoutingEventPublisher(observers ?? throw new ArgumentNullException(nameof(observers)));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The principal the engine writes as (an API client that must hold <c>conversation:assign</c>).</summary>
    public PrincipalRef EnginePrincipal(TicketScope scope) => new ApiClientPrincipal(scope, options.EngineClient);

    public async Task<RoutingOutcome> RouteAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        for (var attempt = 1; attempt <= options.MaximumRouteAttempts; attempt++)
        {
            var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
            var reconciled = await ReconcileAsync(scope, conversation, stored, cancellationToken).ConfigureAwait(false);
            if (reconciled.Settled is { } settled)
                return settled;
            var step = await TryAssignAsync(scope, stored!, reconciled.Entry!, cancellationToken).ConfigureAwait(false);
            if (step is { } outcome)
                return outcome;
        }
        return new RoutingQueued(RoutingQueueReason.Contended);
    }

    private async Task<(RoutingSettled? Settled, RoutingQueueEntry? Entry)> ReconcileAsync(TicketScope scope, ConversationId id, StoredConversation? stored,
        CancellationToken cancellationToken)
    {
        if (stored is null)
        {
            await queue.RemoveAsync(scope, id, long.MaxValue, cancellationToken).ConfigureAwait(false);
            if (await assignments.ClearAsync(scope, id, long.MaxValue, cancellationToken).ConfigureAwait(false))
                await queue.NudgeAsync(scope, null, cancellationToken).ConfigureAwait(false);
            return (new RoutingSettled(RoutingSettledReason.NotFound), null);
        }
        var conversation = stored.Conversation;
        var active = conversation.Status.Value is OpenStatus or PendingStatus;
        if (conversation.Assignee is { } assignee)
        {
            await queue.RemoveAsync(scope, id, stored.Revision, cancellationToken).ConfigureAwait(false);
            if (active)
            {
                var channel = (await inboxes.GetAsync(scope, conversation.Inbox, cancellationToken).ConfigureAwait(false))?.Inbox.Channel.Kind ?? UnknownChannel;
                await assignments.RecordAssignedAsync(scope, new RoutingAssignmentRecord(id, stored.Revision, assignee, conversation.Inbox, conversation.Contact, channel),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (await assignments.ClearAsync(scope, id, stored.Revision, cancellationToken).ConfigureAwait(false))
                await queue.NudgeAsync(scope, null, cancellationToken).ConfigureAwait(false);
            return (new RoutingSettled(RoutingSettledReason.Assigned), null);
        }
        if (await assignments.ClearAsync(scope, id, stored.Revision, cancellationToken).ConfigureAwait(false))
            await queue.NudgeAsync(scope, null, cancellationToken).ConfigureAwait(false);
        if (conversation.Status.Value is not OpenStatus)
        {
            await queue.RemoveAsync(scope, id, stored.Revision, cancellationToken).ConfigureAwait(false);
            return (new RoutingSettled(RoutingSettledReason.NotOpen), null);
        }
        var entry = await queue.EnqueueAsync(scope, RoutingQueueSnapshot.From(stored, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return (null, entry.Entry);
    }

    /// <summary>Returns the outcome, or <see langword="null"/> to reload and retry after a lost race.</summary>
    private async Task<RoutingOutcome?> TryAssignAsync(TicketScope scope, StoredConversation stored, RoutingQueueEntry entry, CancellationToken cancellationToken)
    {
        var conversation = stored.Conversation;
        var target = await inboxes.GetAsync(scope, entry.TargetInbox, cancellationToken).ConfigureAwait(false);
        if (target is null)
            return new RoutingQueued(RoutingQueueReason.Misconfigured);
        if (target.Inbox.RoutingPolicy is not { } policyId)
            return new RoutingQueued(RoutingQueueReason.Manual);
        if (await policies.GetAsync(scope, policyId, cancellationToken).ConfigureAwait(false) is not { } storedPolicy)
            return new RoutingQueued(RoutingQueueReason.Misconfigured);
        var policy = storedPolicy.Policy;
        var own = entry.TargetInbox == conversation.Inbox ? target : await inboxes.GetAsync(scope, conversation.Inbox, cancellationToken).ConfigureAwait(false);
        var channel = own?.Inbox.Channel.Kind ?? UnknownChannel;

        UserId? preferred = null;
        var sticky = policy.Strategy.Value as StickyOwnerStrategy;
        if (sticky is not null && await assignments.GetStickyOwnerAsync(scope, conversation.Contact, cancellationToken).ConfigureAwait(false) is { } owner
            && owner.AssignedAt + sticky.Window >= time.GetUtcNow())
            preferred = owner.Agent;
        var strategy = sticky is null ? policy.Strategy : sticky.Fallback;
        var (selection, required, manualFallback) = strategy switch
        {
            RoundRobinStrategy => (RoutingSelection.RoundRobin, NoSkills, false),
            LeastBusyStrategy => (RoutingSelection.LeastBusy, NoSkills, false),
            SkillsBasedStrategy skills => (RoutingSelection.LeastBusy, skills.RequiredSkills(conversation), false),
            ManualStrategy => (RoutingSelection.RoundRobin, NoSkills, true),
            StickyOwnerStrategy => throw new InvalidOperationException("Sticky owner strategies cannot nest."),
        };
        if (manualFallback && preferred is null)
            return new RoutingQueued(RoutingQueueReason.Manual);

        var candidates = await CandidatesAsync(scope, stored, conversation.Team ?? policy.Team, channel, required, manualFallback ? preferred : null, cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
            return new RoutingQueued(RoutingQueueReason.NoEligibleAgent);

        var request = new RoutingReservationRequest(conversation.Id, stored.Revision, conversation.Inbox, conversation.Contact, channel, entry.TargetInbox.Value,
            selection, candidates, preferred, options.ReservationLease);
        var reservation = await assignments.ReserveAsync(scope, request, cancellationToken).ConfigureAwait(false);
        RoutingAssignment? reserved = null;
        switch (reservation)
        {
            case RoutingReserved applied:
                reserved = applied.Assignment;
                break;
            case RoutingNoCapacity:
                return new RoutingQueued(RoutingQueueReason.NoCapacity);
            case RoutingAlreadyReserved existing:
                // Another worker is assigning (live reservation) or a newer assignment is recorded: reload when it is already assigned.
                return existing.Existing.State == RoutingAssignmentState.Assigned ? Retry : new RoutingQueued(RoutingQueueReason.Contended);
        }
        if (reserved is null)
            throw new UnreachableException();

        var result = await service.AssignAsync(EnginePrincipal(scope), conversation.Id, reserved.Agent, stored.Revision, cancellationToken).ConfigureAwait(false);
        if (result.Value is ConversationApplied assigned)
        {
            await assignments.ConfirmAsync(scope, conversation.Id, reserved.Agent, assigned.Revision, cancellationToken).ConfigureAwait(false);
            await queue.RemoveAsync(scope, conversation.Id, assigned.Revision, cancellationToken).ConfigureAwait(false);
            await events.PublishAsync(new ConversationRouted(scope, conversation.Id, reserved.Agent, assigned.Revision, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            return new RoutingAssigned(reserved.Agent, assigned.Revision);
        }
        await assignments.ReleaseAsync(scope, conversation.Id, reserved.Agent, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ConversationApplied => throw new UnreachableException(),
            ConversationConflict => Retry,
            ConversationNotFound => Retry,
            ConversationRefused => new RoutingQueued(RoutingQueueReason.Refused),
        };
    }

    private async Task<IReadOnlyList<RoutingCandidate>> CandidatesAsync(TicketScope scope, StoredConversation stored, TeamId? team, ChannelKind channel,
        IReadOnlyDictionary<string, int> required, UserId? only, CancellationToken cancellationToken)
    {
        var available = await agents.ListAvailableAsync(scope, IAgentAvailabilityStore.MaximumAvailableList, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<UserId>? members = null;
        if (team is { } teamId)
            members = (await teams.GetAsync(scope, teamId, cancellationToken).ConfigureAwait(false))?.Team.Members ?? new HashSet<UserId>();
        var resource = ConversationAccess.Resource(stored);
        var candidates = new List<RoutingCandidate>();
        foreach (var profile in available)
        {
            var settings = profile.Settings;
            var capacity = settings.CapacityFor(channel);
            if (capacity < 1 || (only is { } single && settings.Agent != single) || (members is not null && !members.Contains(settings.Agent)) || !settings.HasSkills(required))
                continue;
            var filter = await EligibilityAsync(scope, settings.Agent, cancellationToken).ConfigureAwait(false);
            if (AccessFilterEvaluator.Matches(filter, resource))
                candidates.Add(new RoutingCandidate(settings.Agent, capacity));
        }
        return candidates;
    }

    private async Task<AccessFilter> EligibilityAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (eligibility.TryGetValue((scope, agent), out var cached) && cached.Until > now)
            return cached.Filter;
        var principal = await memberships.ResolveUserPrincipalAsync(scope, agent, cancellationToken).ConfigureAwait(false);
        var filter = await planner.PlanAsync(new AccessQuery(principal, ConversationPermissions.Reply, scope), cancellationToken).ConfigureAwait(false);
        if (options.EligibilityCacheDuration > TimeSpan.Zero)
            eligibility[(scope, agent)] = (filter, now + options.EligibilityCacheDuration);
        return filter;
    }
}
