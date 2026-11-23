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
/// capacity for the conversation's channel kind — filtered inside the store query and paged, never cut off at a fixed count.</item>
/// <item>Eligibility: each agent must be permitted <c>conversation:reply</c> on the conversation. One Access plan per agent (built from
/// the agent's current team snapshot) is evaluated against the conversation resource and reused for
/// <see cref="RoutingOptions.RunEligibilityPlanLifetime"/> within this engine instance (one run), instead of a point check per agent per
/// conversation; team snapshots are resolved in batches through <see cref="IRoutingTeamMembershipResolver"/>.</item>
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
    private readonly IRoutingTeamMembershipResolver memberships;
    private readonly IAccessQueryPlanner planner;
    private readonly IRoutingPolicyStore policies;
    private readonly IAgentAvailabilityStore agents;
    private readonly IRoutingAssignmentStore assignments;
    private readonly IRoutingQueueStore queue;
    private readonly RoutingEventPublisher events;
    private readonly RoutingOptions options;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<(TicketScope Scope, UserId Agent), (AccessFilter Filter, DateTimeOffset Until)> eligibility = new();

    public RoutingEngine(IConversationStore conversations, IConversationService service, IInboxStore inboxes, ITeamStore teams, IRoutingTeamMembershipResolver memberships,
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
        InboxId? lastTargetInbox = null;
        for (var attempt = 1; attempt <= options.MaximumRouteAttempts; attempt++)
        {
            var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
            var reconciled = await ReconcileAsync(scope, conversation, stored, cancellationToken).ConfigureAwait(false);
            if (reconciled.Settled is { } settled)
                return settled;
            lastTargetInbox = reconciled.Entry!.TargetInbox;
            var step = await TryAssignAsync(scope, stored!, reconciled.Entry!, cancellationToken).ConfigureAwait(false);
            if (step is { } outcome)
            {
                if (outcome.Value is RoutingQueued queued)
                    await events.PublishAsync(new ConversationQueued(scope, conversation, lastTargetInbox.Value, queued.Reason, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                return outcome;
            }
        }
        var contended = new RoutingQueued(RoutingQueueReason.Contended);
        if (lastTargetInbox is { } targetInbox)
            await events.PublishAsync(new ConversationQueued(scope, conversation, targetInbox, contended.Reason, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return contended;
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

        var candidates = await CandidatesAsync(scope, stored, conversation.Team ?? policy.Team, channel, required, manualFallback ? preferred : null, preferred,
            selection, entry.TargetInbox.Value, cancellationToken)
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

    /// <summary>
    /// Pages through every available agent matching the pool filters (pushed into the store query), keeps those Access permits and, when
    /// more than <see cref="RoutingReservationRequest.MaximumCandidates"/> remain, passes the window that follows the inbox cursor in
    /// rotation order (plus the sticky preference), so no eligible agent is starved by an identifier cutoff.
    /// </summary>
    private async Task<IReadOnlyList<RoutingCandidate>> CandidatesAsync(TicketScope scope, StoredConversation stored, TeamId? team, ChannelKind channel,
        IReadOnlyDictionary<string, int> required, UserId? only, UserId? preferred, RoutingSelection selection, string cursorKey, CancellationToken cancellationToken)
    {
        IEnumerable<UserId>? allowed = null;
        if (team is { } teamId)
        {
            var members = (await teams.GetAsync(scope, teamId, cancellationToken).ConfigureAwait(false))?.Team.Members;
            if (members is null || members.Count == 0)
                return [];
            allowed = members;
        }
        if (only is { } single)
        {
            if (allowed is not null && !allowed.Contains(single))
                return [];
            allowed = [single];
        }
        var resource = ConversationAccess.Resource(stored);
        var eligible = new List<RoutingCandidate>();
        var baseQuery = new AvailableAgentQuery(channel, allowed, required);

        // RoundRobin fast-path: start scanning after the stored cursor (wrapping around if needed) so large workspaces
        // stop as soon as MaximumCandidates eligible agents are collected instead of paging all 10,000 agents.
        if (selection == RoutingSelection.RoundRobin && preferred is null && only is null)
        {
            var cursor = await assignments.GetCursorAsync(scope, cursorKey, cancellationToken).ConfigureAwait(false);
            var startAfter = cursor is not null ? new UserId(cursor) : (UserId?)null;
            var query = startAfter is { } afterId ? baseQuery.WithAfter(afterId) : baseQuery;
            var maxPages = IAgentAvailabilityStore.MaximumAgentsPerWorkspace / IAgentAvailabilityStore.MaximumAvailableList;
            for (var pages = 0; pages < maxPages && eligible.Count < RoutingReservationRequest.MaximumCandidates; pages++)
            {
                var page = await agents.ListAvailableAsync(scope, query, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0)
                    break;
                await CollectEligibleAsync(scope, page, channel, resource, eligible, RoutingReservationRequest.MaximumCandidates, cancellationToken).ConfigureAwait(false);
                if (page.Count < query.Limit || eligible.Count >= RoutingReservationRequest.MaximumCandidates)
                    break;
                query = query.WithAfter(page[^1].Agent);
            }
            if (startAfter is { } wrapLimit && eligible.Count < RoutingReservationRequest.MaximumCandidates)
            {
                query = baseQuery;
                for (var pages = 0; pages < maxPages && eligible.Count < RoutingReservationRequest.MaximumCandidates; pages++)
                {
                    var page = await agents.ListAvailableAsync(scope, query, cancellationToken).ConfigureAwait(false);
                    if (page.Count == 0)
                        break;
                    var take = page.Count;
                    for (var i = 0; i < page.Count; i++)
                    {
                        if (string.CompareOrdinal(page[i].Agent.Value, wrapLimit.Value) > 0)
                        {
                            take = i;
                            break;
                        }
                    }
                    if (take == 0)
                        break;
                    var slice = take == page.Count ? page : page.Take(take).ToList();
                    await CollectEligibleAsync(scope, slice, channel, resource, eligible, RoutingReservationRequest.MaximumCandidates, cancellationToken).ConfigureAwait(false);
                    if (take < page.Count || page.Count < query.Limit || eligible.Count >= RoutingReservationRequest.MaximumCandidates)
                        break;
                    query = query.WithAfter(page[^1].Agent);
                }
            }
            return eligible;
        }

        var scanQuery = baseQuery;
        for (var pages = 0; pages * IAgentAvailabilityStore.MaximumAvailableList < IAgentAvailabilityStore.MaximumAgentsPerWorkspace; pages++)
        {
            var page = await agents.ListAvailableAsync(scope, scanQuery, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
                break;
            await CollectEligibleAsync(scope, page, channel, resource, eligible, int.MaxValue, cancellationToken).ConfigureAwait(false);
            if (page.Count < scanQuery.Limit)
                break;
            scanQuery = scanQuery.WithAfter(page[^1].Agent);
        }
        if (eligible.Count <= RoutingReservationRequest.MaximumCandidates)
            return eligible;
        var currentCursor = await assignments.GetCursorAsync(scope, cursorKey, cancellationToken).ConfigureAwait(false);
        var startIndex = currentCursor is null ? 0 : FindFirstAfter(eligible, currentCursor);
        var window = new List<RoutingCandidate>(RoutingReservationRequest.MaximumCandidates);
        var hasSticky = false;
        for (var i = 0; i < RoutingReservationRequest.MaximumCandidates; i++)
        {
            var item = eligible[(startIndex + i) % eligible.Count];
            if (preferred is { } p && item.Agent == p)
                hasSticky = true;
            window.Add(item);
        }
        if (preferred is { } sticky && !hasSticky)
        {
            for (var i = 0; i < eligible.Count; i++)
                if (eligible[i].Agent == sticky)
                {
                    window[^1] = eligible[i];
                    break;
                }
        }
        return window;
    }

    private async Task CollectEligibleAsync(TicketScope scope, IReadOnlyList<StoredAgentProfile> page, ChannelKind channel, AccessResource resource,
        List<RoutingCandidate> eligible, int maxCount, CancellationToken cancellationToken)
    {
        var ids = new UserId[page.Count];
        for (var i = 0; i < page.Count; i++)
            ids[i] = page[i].Agent;
        var filters = await EligibilityAsync(scope, ids, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < page.Count && eligible.Count < maxCount; i++)
        {
            var profile = page[i];
            if (AccessFilterEvaluator.Matches(filters[profile.Agent], resource))
                eligible.Add(new RoutingCandidate(profile.Agent, profile.Settings.CapacityFor(channel)));
        }
    }

    private static int FindFirstAfter(List<RoutingCandidate> sorted, string cursor)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (string.CompareOrdinal(sorted[mid].Agent.Value, cursor) <= 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo == sorted.Count ? 0 : lo;
    }

    /// <summary>One Access plan per agent, reused within this engine instance (one run); missing plans are resolved in one batch.</summary>
    private async Task<IReadOnlyDictionary<UserId, AccessFilter>> EligibilityAsync(TicketScope scope, IReadOnlyList<UserId> agentIds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var filters = new Dictionary<UserId, AccessFilter>(agentIds.Count);
        List<UserId>? missing = null;
        for (var i = 0; i < agentIds.Count; i++)
        {
            var agent = agentIds[i];
            if (eligibility.TryGetValue((scope, agent), out var cached) && cached.Until > now)
                filters[agent] = cached.Filter;
            else
            {
                missing ??= [];
                missing.Add(agent);
            }
        }
        if (missing is null)
            return filters;
        var teamsByAgent = await memberships.GetTeamsAsync(scope, missing, cancellationToken).ConfigureAwait(false);
        var planUntil = options.RunEligibilityPlanLifetime > TimeSpan.Zero ? now + options.RunEligibilityPlanLifetime : (DateTimeOffset?)null;
        if (missing.Count == 1)
        {
            var agent = missing[0];
            var principal = new UserPrincipal(scope, agent, teamsByAgent.GetValueOrDefault(agent) ?? []);
            var filter = await planner.PlanAsync(new AccessQuery(principal, ConversationPermissions.Reply, scope), cancellationToken).ConfigureAwait(false);
            filters[agent] = filter;
            if (planUntil is { } until)
                eligibility[(scope, agent)] = (filter, until);
            return filters;
        }
        const int maxConcurrency = 8;
        var results = new AccessFilter[missing.Count];
        if (missing.Count <= maxConcurrency)
        {
            var tasks = new Task<AccessFilter>[missing.Count];
            for (var i = 0; i < missing.Count; i++)
            {
                var agent = missing[i];
                var principal = new UserPrincipal(scope, agent, teamsByAgent.GetValueOrDefault(agent) ?? []);
                tasks[i] = planner.PlanAsync(new AccessQuery(principal, ConversationPermissions.Reply, scope), cancellationToken);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            for (var i = 0; i < missing.Count; i++)
                results[i] = tasks[i].Result;
        }
        else
        {
            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            var tasks = new Task[missing.Count];
            for (var i = 0; i < missing.Count; i++)
            {
                var idx = i;
                var agent = missing[idx];
                var principal = new UserPrincipal(scope, agent, teamsByAgent.GetValueOrDefault(agent) ?? []);
                tasks[idx] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        results[idx] = await planner.PlanAsync(new AccessQuery(principal, ConversationPermissions.Reply, scope), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        for (var i = 0; i < missing.Count; i++)
        {
            var agent = missing[i];
            var filter = results[i];
            filters[agent] = filter;
            if (planUntil is { } until)
                eligibility[(scope, agent)] = (filter, until);
        }
        return filters;
    }
}
