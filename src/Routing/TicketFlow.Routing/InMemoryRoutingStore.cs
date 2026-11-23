using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>
/// Development-only implementation of every routing store contract. State is process-local, lost on restart and never shared between
/// processes; every operation is serialized under one lock and scans are linear, so it suits small development data sets only. It
/// is an example of the contracts, not durable storage.
/// </summary>
public sealed class InMemoryRoutingStore : IRoutingPolicyStore, IAgentAvailabilityStore, IRoutingAssignmentStore, IRoutingQueueStore, IConversationPresenceStore
{
    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly Dictionary<(TicketScope Scope, string Id), StoredRoutingPolicy> policies = [];
    private readonly Dictionary<(TicketScope Scope, UserId Agent), AgentRow> agents = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), RoutingAssignment> assignments = [];
    private readonly Dictionary<(TicketScope Scope, string Key), string> cursors = [];
    private readonly Dictionary<(TicketScope Scope, ContactId Contact), StickyOwner> sticky = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), QueueRow> queue = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), SortedDictionary<string, ConversationPresence>> presence = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), ReplyLock> locks = [];
    private long nextFence;

    public InMemoryRoutingStore(TimeProvider? time = null, int maximumRows = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 1);
        this.time = time ?? TimeProvider.System;
        MaximumRows = maximumRows;
    }

    /// <summary>Upper bound for each of the agent, assignment, queue and presence collections.</summary>
    public int MaximumRows { get; }

    private DateTimeOffset Now => RoutingTextAccess.Instant(time.GetUtcNow());

    // ---- Policies ----

    public Task<StoredRoutingPolicy?> GetAsync(TicketScope scope, RoutingPolicyId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequirePolicyId(id);
        lock (gate)
            return Task.FromResult(policies.GetValueOrDefault((scope, id.Value)));
    }

    public Task<IReadOnlyList<StoredRoutingPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredRoutingPolicy>>([.. policies.Values.Where(p => p.Scope == scope).OrderBy(p => p.Policy.Id.Value, StringComparer.Ordinal)]);
    }

    public Task<StoreWriteResult<StoredRoutingPolicy, RoutingWriteRejection>> CreateAsync(TicketScope scope, RoutingPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        lock (gate)
        {
            if (policies.TryGetValue((scope, policy.Id.Value), out var existing))
                return Result<StoredRoutingPolicy>(new StoreConflict(existing.Revision));
            if (policies.Keys.Count(key => key.Scope == scope) >= IRoutingPolicyStore.MaximumPoliciesPerWorkspace)
                return Result<StoredRoutingPolicy>(new StoreRejected<RoutingWriteRejection>(RoutingWriteRejection.CapacityExceeded));
            var stored = new StoredRoutingPolicy(scope, policy, 1);
            policies[(scope, policy.Id.Value)] = stored;
            return Result<StoredRoutingPolicy>(new StoreApplied<StoredRoutingPolicy>(stored));
        }
    }

    public Task<StoreWriteResult<StoredRoutingPolicy, RoutingWriteRejection>> UpdateAsync(TicketScope scope, RoutingPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        lock (gate)
        {
            if (!policies.TryGetValue((scope, policy.Id.Value), out var existing))
                return Result<StoredRoutingPolicy>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredRoutingPolicy>(new StoreConflict(existing.Revision));
            var stored = new StoredRoutingPolicy(scope, policy, existing.Revision + 1);
            policies[(scope, policy.Id.Value)] = stored;
            return Result<StoredRoutingPolicy>(new StoreApplied<StoredRoutingPolicy>(stored));
        }
    }

    public Task<StoreWriteResult<StoredRoutingPolicy, RoutingWriteRejection>> DeleteAsync(TicketScope scope, RoutingPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequirePolicyId(id);
        lock (gate)
        {
            if (!policies.TryGetValue((scope, id.Value), out var existing))
                return Result<StoredRoutingPolicy>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredRoutingPolicy>(new StoreConflict(existing.Revision));
            policies.Remove((scope, id.Value));
            return Result<StoredRoutingPolicy>(new StoreApplied<StoredRoutingPolicy>(existing));
        }
    }

    // ---- Agents ----

    public Task<StoredAgentProfile?> GetAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        lock (gate)
            return Task.FromResult(agents.GetValueOrDefault((scope, agent))?.Profile);
    }

    public Task<IReadOnlyList<StoredAgentProfile>> ListAsync(TicketScope scope, UserId? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumList);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredAgentProfile>>([.. agents.Where(pair => pair.Key.Scope == scope
                    && (after is not { } cursor || string.CompareOrdinal(pair.Key.Agent.Value, cursor.Value) > 0))
                .Select(pair => pair.Value.Profile).OrderBy(profile => profile.Agent.Value, StringComparer.Ordinal).Take(limit)]);
    }

    public Task<IReadOnlyList<StoredAgentProfile>> ListAvailableAsync(TicketScope scope, AvailableAgentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        var now = Now;
        var allowed = query.Agents?.ToHashSet();
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredAgentProfile>>([.. agents.Where(pair => pair.Key.Scope == scope
                    && (allowed is null || allowed.Contains(pair.Key.Agent))
                    && (query.After is not { } after || string.CompareOrdinal(pair.Key.Agent.Value, after.Value) > 0)
                    && pair.Value.Profile.Presence.EffectiveStatus(now) == AgentStatus.Online && query.Matches(pair.Value.Profile))
                .Select(pair => pair.Value.Profile).OrderBy(profile => profile.Agent.Value, StringComparer.Ordinal).Take(query.Limit)]);
    }

    public Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> SaveSettingsAsync(TicketScope scope, AgentRoutingSettings settings, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var now = Now;
        lock (gate)
        {
            var key = (scope, settings.Agent);
            var exists = agents.TryGetValue(key, out var row);
            if (expectedRevision is null)
            {
                if (exists)
                    return Result<StoredAgentProfile>(new StoreConflict(row!.Profile.Revision));
                if (agents.Keys.Count(item => item.Scope == scope) >= IAgentAvailabilityStore.MaximumAgentsPerWorkspace || agents.Count >= MaximumRows)
                    return Result<StoredAgentProfile>(new StoreRejected<RoutingWriteRejection>(RoutingWriteRejection.CapacityExceeded));
                var created = new AgentRow(new StoredAgentProfile(scope, settings, AgentPresence.Offline(now), false, 1));
                agents[key] = created;
                return Result<StoredAgentProfile>(new StoreApplied<StoredAgentProfile>(created.Profile));
            }
            if (!exists)
                return Result<StoredAgentProfile>(StoreNotFound.Instance);
            if (row!.Profile.Revision != expectedRevision)
                return Result<StoredAgentProfile>(new StoreConflict(row.Profile.Revision));
            row.Profile = row.Profile with { Settings = settings, Revision = row.Profile.Revision + 1 };
            return Result<StoredAgentProfile>(new StoreApplied<StoredAgentProfile>(row.Profile));
        }
    }

    public Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> SetStatusAsync(TicketScope scope, UserId agent, AgentStatus status, TimeSpan presenceTtl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        RequireTtl(presenceTtl);
        var now = Now;
        lock (gate)
        {
            if (!agents.TryGetValue((scope, agent), out var row))
                return Result<StoredAgentProfile>(StoreNotFound.Instance);
            var profile = row.Profile;
            var wasOnline = profile.Presence.EffectiveStatus(now) != AgentStatus.Offline;
            var presence = status == AgentStatus.Offline ? AgentPresence.Offline(now) : new AgentPresence(status, now, now, now + presenceTtl);
            row.Profile = profile with
            {
                Presence = presence,
                ReassignmentPending = profile.ReassignmentPending || (wasOnline && status == AgentStatus.Offline),
                Revision = profile.Revision + 1,
            };
            return Result<StoredAgentProfile>(new StoreApplied<StoredAgentProfile>(row.Profile));
        }
    }

    public Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> HeartbeatAsync(TicketScope scope, UserId agent, TimeSpan presenceTtl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        RequireTtl(presenceTtl);
        var now = Now;
        lock (gate)
        {
            if (!agents.TryGetValue((scope, agent), out var row))
                return Result<StoredAgentProfile>(StoreNotFound.Instance);
            var presence = row.Profile.Presence;
            if (presence.EffectiveStatus(now) != AgentStatus.Offline)
                row.Profile = row.Profile with { Presence = new AgentPresence(presence.Status, presence.ChangedAt, now, now + presenceTtl) };
            return Result<StoredAgentProfile>(new StoreApplied<StoredAgentProfile>(row.Profile));
        }
    }

    public Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> DeleteAsync(TicketScope scope, UserId agent, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        lock (gate)
        {
            if (!agents.TryGetValue((scope, agent), out var row))
                return Result<StoredAgentProfile>(StoreNotFound.Instance);
            if (row.Profile.Revision != expectedRevision)
                return Result<StoredAgentProfile>(new StoreConflict(row.Profile.Revision));
            agents.Remove((scope, agent));
            return Result<StoredAgentProfile>(new StoreApplied<StoredAgentProfile>(row.Profile));
        }
    }

    public Task<IReadOnlyList<AgentPresenceExpired>> ExpirePresenceAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumAvailableList);
        var now = Now;
        lock (gate)
        {
            var expired = new List<AgentPresenceExpired>();
            foreach (var row in agents.Values.Where(row => row.Profile.Presence.Status != AgentStatus.Offline && row.Profile.Presence.ExpiresAt <= now)
                .OrderBy(row => row.Profile.Presence.ExpiresAt).Take(limit))
            {
                row.Profile = row.Profile with { Presence = AgentPresence.Offline(now), ReassignmentPending = true, Revision = row.Profile.Revision + 1 };
                expired.Add(new AgentPresenceExpired(row.Profile.Scope, row.Profile.Agent, now));
            }
            return Task.FromResult<IReadOnlyList<AgentPresenceExpired>>(expired);
        }
    }

    public Task<IReadOnlyList<AgentReassignmentClaim>> ClaimReassignmentsAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingQueueStore.MaximumClaim);
        RequireLease(lease);
        var now = Now;
        lock (gate)
        {
            var claims = new List<AgentReassignmentClaim>();
            foreach (var row in agents.Values.Where(row => row.Profile.ReassignmentPending && !(row.LeaseUntil > now)).Take(limit))
            {
                row.Fence++;
                row.LeaseUntil = now + lease;
                claims.Add(new AgentReassignmentClaim(row.Profile.Scope, row.Profile.Agent, row.Fence, now + lease));
            }
            return Task.FromResult<IReadOnlyList<AgentReassignmentClaim>>(claims);
        }
    }

    public Task<bool> CompleteReassignmentAsync(TicketScope scope, UserId agent, long fence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        lock (gate)
        {
            if (!agents.TryGetValue((scope, agent), out var row) || row.Fence != fence || !row.Profile.ReassignmentPending)
                return Task.FromResult(false);
            row.Profile = row.Profile with { ReassignmentPending = false };
            row.LeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    // ---- Assignments ----

    public Task<RoutingReservationResult> ReserveAsync(TicketScope scope, RoutingReservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        var now = Now;
        lock (gate)
        {
            if (assignments.TryGetValue((scope, request.Conversation), out var existing)
                && ((existing.State == RoutingAssignmentState.Reserved && existing.CountsAt(now))
                    || (existing.State == RoutingAssignmentState.Assigned && existing.ConversationRevision > request.ConversationRevision)))
                return Task.FromResult<RoutingReservationResult>(new RoutingAlreadyReserved(existing));
            var loads = ComputeLoadsLocked(scope, request.Candidates, request.Channel, request.Conversation, now);
            var chosen = request.Select(loads, cursors.GetValueOrDefault((scope, request.CursorKey)));
            if (chosen is null)
                return Task.FromResult<RoutingReservationResult>(RoutingNoCapacity.Instance);
            if (existing is null && assignments.Count >= MaximumRows)
                throw new InvalidOperationException("The in-memory routing store is full.");
            var reservation = new RoutingAssignment(request.Conversation, chosen.Agent, request.Inbox, request.Contact, request.Channel, RoutingAssignmentState.Reserved,
                now + request.Lease, request.ConversationRevision, now);
            assignments[(scope, request.Conversation)] = reservation;
            cursors[(scope, request.CursorKey)] = chosen.Agent.Value;
            return Task.FromResult<RoutingReservationResult>(new RoutingReserved(reservation, loads.GetValueOrDefault(chosen.Agent) + 1));
        }
    }

    public Task<bool> ConfirmAsync(TicketScope scope, ConversationId conversation, UserId agent, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(conversationRevision, 1);
        var now = Now;
        lock (gate)
        {
            if (!assignments.TryGetValue((scope, conversation), out var row) || row.Agent != agent || row.ConversationRevision > conversationRevision)
                return Task.FromResult(false);
            if (row.State == RoutingAssignmentState.Reserved || row.ConversationRevision < conversationRevision)
            {
                assignments[(scope, conversation)] = new RoutingAssignment(conversation, agent, row.Inbox, row.Contact, row.Channel, RoutingAssignmentState.Assigned, null,
                    conversationRevision, now);
                sticky[(scope, row.Contact)] = new StickyOwner(row.Contact, agent, now);
            }
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseAsync(TicketScope scope, ConversationId conversation, UserId agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(agent);
        lock (gate)
        {
            if (!assignments.TryGetValue((scope, conversation), out var row) || row.Agent != agent || row.State != RoutingAssignmentState.Reserved)
                return Task.FromResult(false);
            return Task.FromResult(assignments.Remove((scope, conversation)));
        }
    }

    public Task RecordAssignedAsync(TicketScope scope, RoutingAssignmentRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(record);
        var now = Now;
        lock (gate)
        {
            var key = (scope, record.Conversation);
            if (assignments.TryGetValue(key, out var row) && (row.ConversationRevision > record.ConversationRevision
                || (row.State == RoutingAssignmentState.Assigned && row.Agent == record.Agent && row.Channel == record.Channel)))
                return Task.CompletedTask;
            if (row is null && assignments.Count >= MaximumRows)
                throw new InvalidOperationException("The in-memory routing store is full.");
            assignments[key] = new RoutingAssignment(record.Conversation, record.Agent, record.Inbox, record.Contact, record.Channel, RoutingAssignmentState.Assigned, null,
                record.ConversationRevision, now);
            sticky[(scope, record.Contact)] = new StickyOwner(record.Contact, record.Agent, now);
            return Task.CompletedTask;
        }
    }

    public Task<bool> ClearAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        var now = Now;
        lock (gate)
        {
            if (!assignments.TryGetValue((scope, conversation), out var row) || row.ConversationRevision > conversationRevision
                || (row.State == RoutingAssignmentState.Reserved && row.CountsAt(now)))
                return Task.FromResult(false);
            return Task.FromResult(assignments.Remove((scope, conversation)));
        }
    }

    public Task<RoutingAssignment?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        lock (gate)
            return Task.FromResult(assignments.GetValueOrDefault((scope, conversation)));
    }

    public Task<IReadOnlyList<RoutingAssignment>> ListForAgentAsync(TicketScope scope, UserId agent, ConversationId? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingAssignmentStore.MaximumList);
        lock (gate)
            return Task.FromResult<IReadOnlyList<RoutingAssignment>>([.. assignments.Where(pair => pair.Key.Scope == scope && pair.Value.Agent == agent
                    && (after is not { } cursor || string.CompareOrdinal(pair.Key.Conversation.Value, cursor.Value) > 0))
                .Select(pair => pair.Value).OrderBy(row => row.Conversation.Value, StringComparer.Ordinal).Take(limit)]);
    }

    public Task<IReadOnlyDictionary<ChannelKind, int>> GetLoadAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        var now = Now;
        lock (gate)
            return Task.FromResult<IReadOnlyDictionary<ChannelKind, int>>(assignments
                .Where(pair => pair.Key.Scope == scope && pair.Value.Agent == agent && pair.Value.CountsAt(now))
                .GroupBy(pair => pair.Value.Channel).ToDictionary(group => group.Key, group => group.Count()));
    }

    public Task<StickyOwner?> GetStickyOwnerAsync(TicketScope scope, ContactId contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (contact == default)
            throw new ArgumentException("A contact is required.", nameof(contact));
        lock (gate)
            return Task.FromResult(sticky.GetValueOrDefault((scope, contact)));
    }

    public Task<string?> GetCursorAsync(TicketScope scope, string cursorKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        TicketScope.RequireIdentifier(cursorKey, nameof(cursorKey));
        lock (gate)
            return Task.FromResult(cursors.GetValueOrDefault((scope, cursorKey)));
    }

    private Dictionary<UserId, int> ComputeLoadsLocked(TicketScope scope, IReadOnlyList<RoutingCandidate> candidates, ChannelKind channel, ConversationId excluded, DateTimeOffset now)
    {
        var loads = new Dictionary<UserId, int>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
            loads[candidates[i].Agent] = 0;
        foreach (var (key, value) in assignments)
        {
            if (key.Scope == scope && value.Channel == channel && key.Conversation != excluded && value.CountsAt(now)
                && loads.TryGetValue(value.Agent, out var current))
                loads[value.Agent] = current + 1;
        }
        return loads;
    }

    // ---- Queue ----

    public Task<StoredRoutingQueueEntry> EnqueueAsync(TicketScope scope, RoutingQueueSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = Now;
        lock (gate)
        {
            var key = (scope, snapshot.Conversation);
            if (queue.TryGetValue(key, out var row))
            {
                if (snapshot.ConversationRevision >= row.Entry.Snapshot.ConversationRevision)
                    row.Entry = new RoutingQueueEntry(snapshot, row.Entry.TargetInbox, row.Entry.QueuedAt, row.Entry.OverflowStage, row.Entry.WaitExceeded,
                        row.Entry.NextAttemptAt, row.Entry.Attempts);
                return Task.FromResult(new StoredRoutingQueueEntry(scope, row.Entry));
            }
            if (queue.Count >= MaximumRows)
                throw new InvalidOperationException("The in-memory routing store is full.");
            var created = new QueueRow(new RoutingQueueEntry(snapshot, snapshot.Inbox, now, 0, false, now, 0));
            queue[key] = created;
            return Task.FromResult(new StoredRoutingQueueEntry(scope, created.Entry));
        }
    }

    public Task<bool> RemoveAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        lock (gate)
            return Task.FromResult(queue.TryGetValue((scope, conversation), out var row) && row.Entry.Snapshot.ConversationRevision <= conversationRevision
                && queue.Remove((scope, conversation)));
    }

    Task<StoredRoutingQueueEntry?> IRoutingQueueStore.GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        lock (gate)
            return Task.FromResult(queue.TryGetValue((scope, conversation), out var row) ? new StoredRoutingQueueEntry(scope, row.Entry) : null);
    }

    public Task<RoutingQueuePage> ListAsync(TicketScope scope, RoutingQueueQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            var rows = queue.Where(pair => pair.Key.Scope == scope && query.Matches(pair.Value.Entry) && query.IsAfterCursor(pair.Value.Entry)
                    && AccessFilterEvaluator.Matches(filter, pair.Value.Entry.Resource(scope)))
                .Select(pair => pair.Value.Entry)
                .OrderBy(entry => entry.Snapshot.WaitingSince).ThenBy(entry => entry.Conversation.Value, StringComparer.Ordinal)
                .Take(query.Limit + 1).ToList();
            var page = rows.Take(query.Limit).Select(entry => new StoredRoutingQueueEntry(scope, entry)).ToList();
            return Task.FromResult(new RoutingQueuePage(page, rows.Count > query.Limit
                ? new RoutingQueueCursor(page[^1].Entry.Snapshot.WaitingSince, page[^1].Entry.Conversation) : null));
        }
    }

    public Task<IReadOnlyList<RoutingQueueClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingQueueStore.MaximumClaim);
        RequireLease(lease);
        var now = Now;
        lock (gate)
        {
            var claims = new List<RoutingQueueClaim>();
            foreach (var (key, row) in queue.Where(pair => pair.Value.Entry.NextAttemptAt <= now && !(pair.Value.LeaseUntil > now))
                .OrderBy(pair => pair.Value.Entry.NextAttemptAt).ThenBy(pair => pair.Key.Conversation.Value, StringComparer.Ordinal).Take(limit).ToList())
            {
                row.Fence = ++nextFence;
                row.LeaseUntil = now + lease;
                claims.Add(new RoutingQueueClaim(key.Scope, row.Entry, row.Fence, now + lease));
            }
            return Task.FromResult<IReadOnlyList<RoutingQueueClaim>>([.. claims.OrderBy(claim => claim.Entry.Snapshot.WaitingSince)
                .ThenBy(claim => claim.Entry.Conversation.Value, StringComparer.Ordinal)]);
        }
    }

    public Task<bool> CompleteAttemptAsync(TicketScope scope, ConversationId conversation, long fence, RoutingQueueUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        ArgumentNullException.ThrowIfNull(update);
        var now = Now;
        lock (gate)
        {
            if (!queue.TryGetValue((scope, conversation), out var row) || row.Fence != fence || !(row.LeaseUntil > now))
                return Task.FromResult(false);
            var entry = row.Entry;
            row.Entry = new RoutingQueueEntry(entry.Snapshot, update.TargetInbox, entry.QueuedAt, update.OverflowStage, update.WaitExceeded, update.NextAttemptAt, entry.Attempts + 1);
            row.LeaseUntil = null;
            return Task.FromResult(true);
        }
    }

    public Task<int> NudgeAsync(TicketScope scope, InboxId? targetInbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (targetInbox == default(InboxId))
            throw new ArgumentException("The inbox must be initialized when supplied.", nameof(targetInbox));
        var now = Now;
        lock (gate)
        {
            var count = 0;
            foreach (var (key, row) in queue)
            {
                if (key.Scope != scope || (targetInbox is { } inbox && row.Entry.TargetInbox != inbox) || row.Entry.NextAttemptAt <= now)
                    continue;
                var entry = row.Entry;
                row.Entry = new RoutingQueueEntry(entry.Snapshot, entry.TargetInbox, entry.QueuedAt, entry.OverflowStage, entry.WaitExceeded, now, entry.Attempts);
                count++;
            }
            return Task.FromResult(count);
        }
    }

    // ---- Presence and locks ----

    public Task<PresenceWriteResult> TouchAsync(TicketScope scope, ConversationId conversation, UserId user, ConversationActivity activity, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(user);
        if (!Enum.IsDefined(activity))
            throw new ArgumentOutOfRangeException(nameof(activity));
        RequireTtl(ttl);
        var now = Now;
        lock (gate)
        {
            if (!presence.TryGetValue((scope, conversation), out var entries))
            {
                if (presence.Count >= MaximumRows)
                    throw new InvalidOperationException("The in-memory routing store is full.");
                presence[(scope, conversation)] = entries = new(StringComparer.Ordinal);
            }
            foreach (var expired in entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
                entries.Remove(expired);
            var existing = entries.GetValueOrDefault(user.Value);
            if (existing is null && entries.Count >= IConversationPresenceStore.MaximumPresencePerConversation)
                return Task.FromResult<PresenceWriteResult>(PresenceCapacityExceeded.Instance);
            var changed = existing is null || existing.Activity != activity;
            var current = new ConversationPresence(user, activity, changed ? now : existing!.Since, now + ttl);
            entries[user.Value] = current;
            return Task.FromResult<PresenceWriteResult>(new PresenceTouched(current, changed));
        }
    }

    public Task<bool> LeaveAsync(TicketScope scope, ConversationId conversation, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(user);
        var now = Now;
        lock (gate)
        {
            if (!presence.TryGetValue((scope, conversation), out var entries) || !entries.Remove(user.Value, out var removed))
                return Task.FromResult(false);
            if (entries.Count == 0)
                presence.Remove((scope, conversation));
            return Task.FromResult(removed.ExpiresAt > now);
        }
    }

    Task<IReadOnlyList<ConversationPresence>> IConversationPresenceStore.ListAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        var now = Now;
        lock (gate)
            return Task.FromResult<IReadOnlyList<ConversationPresence>>(presence.TryGetValue((scope, conversation), out var entries)
                ? [.. entries.Values.Where(item => item.ExpiresAt > now)]
                : []);
    }

    public Task<ReplyLockResult> AcquireLockAsync(TicketScope scope, ConversationId conversation, UserId holder, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(holder);
        RequireTtl(duration);
        var now = Now;
        lock (gate)
        {
            var current = locks.GetValueOrDefault((scope, conversation));
            if (current is not null && current.ExpiresAt > now && current.Holder != holder)
                return Task.FromResult<ReplyLockResult>(new ReplyLockHeld(current));
            if (current is null && locks.Count >= MaximumRows)
                throw new InvalidOperationException("The in-memory routing store is full.");
            var renewed = current is not null && current.ExpiresAt > now;
            var acquired = new ReplyLock(conversation, holder, ++nextFence, renewed ? current!.AcquiredAt : now, now + duration);
            locks[(scope, conversation)] = acquired;
            return Task.FromResult<ReplyLockResult>(new ReplyLockAcquired(acquired, renewed));
        }
    }

    public Task<ReplyLock?> GetLockAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        var now = Now;
        lock (gate)
            return Task.FromResult(locks.GetValueOrDefault((scope, conversation)) is { } current && current.ExpiresAt > now ? current : null);
    }

    public Task<bool> ReleaseLockAsync(TicketScope scope, ConversationId conversation, UserId holder, long fence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(holder);
        var now = Now;
        lock (gate)
        {
            if (locks.GetValueOrDefault((scope, conversation)) is not { } current || current.Holder != holder || current.Fence != fence || current.ExpiresAt <= now)
                return Task.FromResult(false);
            return Task.FromResult(locks.Remove((scope, conversation)));
        }
    }

    public Task<int> PruneAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationPresenceStore.MaximumPrune);
        var now = Now;
        lock (gate)
        {
            var removed = 0;
            List<(TicketScope, ConversationId)>? emptyPresence = null;
            List<string>? expiredUsers = null;
            foreach (var (key, entries) in presence)
            {
                if (removed >= limit)
                    break;
                expiredUsers?.Clear();
                foreach (var (user, item) in entries)
                {
                    if (item.ExpiresAt <= now)
                    {
                        expiredUsers ??= [];
                        expiredUsers.Add(user);
                        if (removed + expiredUsers.Count >= limit)
                            break;
                    }
                }
                if (expiredUsers is { Count: > 0 })
                {
                    for (var i = 0; i < expiredUsers.Count; i++)
                        entries.Remove(expiredUsers[i]);
                    removed += expiredUsers.Count;
                    if (entries.Count == 0)
                    {
                        emptyPresence ??= [];
                        emptyPresence.Add(key);
                    }
                }
            }
            if (emptyPresence is not null)
                for (var i = 0; i < emptyPresence.Count; i++)
                    presence.Remove(emptyPresence[i]);
            if (removed < limit)
            {
                List<(TicketScope, ConversationId)>? expiredLocks = null;
                foreach (var (key, item) in locks)
                {
                    if (item.ExpiresAt <= now)
                    {
                        expiredLocks ??= [];
                        expiredLocks.Add(key);
                        if (removed + expiredLocks.Count >= limit)
                            break;
                    }
                }
                if (expiredLocks is not null)
                {
                    for (var i = 0; i < expiredLocks.Count; i++)
                        locks.Remove(expiredLocks[i]);
                    removed += expiredLocks.Count;
                }
            }
            return Task.FromResult(removed);
        }
    }

    private static Task<StoreWriteResult<T, RoutingWriteRejection>> Result<T>(StoreWriteResult<T, RoutingWriteRejection> result) => Task.FromResult(result);

    private static void RequirePolicyId(RoutingPolicyId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A routing policy identifier is required.", nameof(id));
    }

    private static void RequireAgent(UserId agent)
    {
        if (agent == default)
            throw new ArgumentException("An agent identifier is required.", nameof(agent));
    }

    private static void RequireConversation(ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
    }

    private static void RequireTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(ttl), "Durations are positive and at most 24 hours.");
    }

    private static void RequireLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > IRoutingQueueStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most ten minutes.");
    }

    private sealed class AgentRow(StoredAgentProfile profile)
    {
        public StoredAgentProfile Profile { get; set; } = profile;
        public long Fence { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
    }

    private sealed class QueueRow(RoutingQueueEntry entry)
    {
        public RoutingQueueEntry Entry { get; set; } = entry;
        public long Fence { get; set; }
        public DateTimeOffset? LeaseUntil { get; set; }
    }
}

/// <summary>Instant normalization shared with the abstractions (microsecond precision).</summary>
internal static class RoutingTextAccess
{
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
