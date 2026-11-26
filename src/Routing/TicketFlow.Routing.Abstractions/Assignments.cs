using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>Persisted assignment row states. Never renumber.</summary>
public enum RoutingAssignmentState
{
    /// <summary>The engine selected the agent and holds a short lease while it calls <c>AssignAsync</c>.</summary>
    Reserved = 1,

    /// <summary>The conversation is assigned to the agent (by the engine or manually) and counts toward the agent's load.</summary>
    Assigned = 2,
}

/// <summary>
/// The routing load projection of one conversation: which agent it counts against, on which channel kind. Live reservations and
/// assigned rows both count toward capacity; expired reservations do not.
/// </summary>
public sealed record RoutingAssignment
{
    public RoutingAssignment(ConversationId conversation, UserId agent, InboxId inbox, ContactId contact, ChannelKind channel, RoutingAssignmentState state,
        DateTimeOffset? reservedUntil, long conversationRevision, DateTimeOffset updatedAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(conversationRevision, 1);
        if (conversation == default || agent == default || inbox == default || contact == default || !channel.IsInitialized)
            throw new ArgumentException("Conversation, agent, inbox, contact and channel are required.", conversation == default ? nameof(conversation) : agent == default ? nameof(agent) : inbox == default ? nameof(inbox) : contact == default ? nameof(contact) : nameof(channel));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if ((state == RoutingAssignmentState.Reserved) != (reservedUntil is not null))
            throw new ArgumentException("Reservations, and only they, carry a lease.", nameof(reservedUntil));
        Conversation = conversation;
        Agent = agent;
        Inbox = inbox;
        Contact = contact;
        Channel = channel;
        State = state;
        ReservedUntil = RoutingText.Instant(reservedUntil);
        ConversationRevision = conversationRevision;
        UpdatedAt = RoutingText.Instant(updatedAt);
    }

    public ConversationId Conversation { get; }
    public UserId Agent { get; }
    public InboxId Inbox { get; }
    public ContactId Contact { get; }
    public ChannelKind Channel { get; }
    public RoutingAssignmentState State { get; }
    public DateTimeOffset? ReservedUntil { get; }

    /// <summary>
    /// The conversation revision the row reflects: the revision the reservation was made against, or the revision at which the
    /// assignment was observed. Projection writes never move a row back to an older revision.
    /// </summary>
    public long ConversationRevision { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>True when the row counts toward load at <paramref name="now"/>.</summary>
    public bool CountsAt(DateTimeOffset now) => State == RoutingAssignmentState.Assigned || ReservedUntil > now;
}

/// <summary>A candidate agent with its capacity for the conversation's channel kind (1-1000).</summary>
public sealed record RoutingCandidate
{
    public RoutingCandidate(UserId agent, int capacity)
    {
        if (agent == default)
            throw new ArgumentException("An agent is required.", nameof(agent));
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, AgentRoutingSettings.MaximumCapacity);
        Agent = agent;
        Capacity = capacity;
    }

    public UserId Agent { get; }
    public int Capacity { get; }
}

/// <summary>How the store chooses among candidates under capacity. Never renumber.</summary>
public enum RoutingSelection
{
    /// <summary>The first candidate in identifier order after the cursor (wrapping) that is under capacity.</summary>
    RoundRobin = 1,

    /// <summary>The candidate with the lowest load/capacity ratio; ties are broken in round-robin order after the cursor.</summary>
    LeastBusy = 2,
}

/// <summary>
/// An atomic select-and-reserve request. The store, in one atomic step: refuses (<see cref="RoutingAlreadyReserved"/>) when a live
/// reservation for the conversation exists or an assigned row reflects a newer conversation revision than
/// <see cref="ConversationRevision"/>,
/// computes each candidate's load (live reservations plus assigned rows on <see cref="Channel"/>), picks <see cref="Preferred"/> when it
/// is a candidate under capacity, otherwise selects by <see cref="Selection"/> among candidates under capacity, writes the reservation
/// and moves the round-robin cursor named <see cref="CursorKey"/> to the chosen agent.
/// </summary>
public sealed record RoutingReservationRequest
{
    public const int MaximumCandidates = 1000;
    public const int MaximumCursorKeyLength = 300;
    public static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(10);

    public RoutingReservationRequest(ConversationId conversation, long conversationRevision, InboxId inbox, ContactId contact, ChannelKind channel, string cursorKey,
        RoutingSelection selection, IEnumerable<RoutingCandidate> candidates, UserId? preferred, TimeSpan lease)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(conversationRevision, 1);
        if (conversation == default || inbox == default || contact == default || !channel.IsInitialized || preferred == default(UserId))
            throw new ArgumentException("Conversation, inbox, contact and channel are required; the preferred agent must be initialized when supplied.", conversation == default ? nameof(conversation) : inbox == default ? nameof(inbox) : contact == default ? nameof(contact) : !channel.IsInitialized ? nameof(channel) : nameof(preferred));
        CursorKey = TicketScope.RequireIdentifier(cursorKey, nameof(cursorKey));
        if (!Enum.IsDefined(selection))
            throw new ArgumentOutOfRangeException(nameof(selection));
        ArgumentNullException.ThrowIfNull(candidates);
        var snapshot = new SortedDictionary<string, RoutingCandidate>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate, nameof(candidates));
            if (!snapshot.TryAdd(candidate.Agent.Value, candidate))
                throw new ArgumentException("Candidates must be distinct.", nameof(candidates));
            if (snapshot.Count > MaximumCandidates)
                throw new ArgumentException($"At most {MaximumCandidates} candidates are allowed.", nameof(candidates));
        }
        if (snapshot.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        if (lease <= TimeSpan.Zero || lease > MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Reservation leases are positive and at most ten minutes.");
        Conversation = conversation;
        ConversationRevision = conversationRevision;
        Inbox = inbox;
        Contact = contact;
        Channel = channel;
        Selection = selection;
        Candidates = [.. snapshot.Values];
        Preferred = preferred;
        Lease = lease;
    }

    public ConversationId Conversation { get; }

    /// <summary>The unassigned conversation revision the caller will fence <c>AssignAsync</c> on.</summary>
    public long ConversationRevision { get; }

    public InboxId Inbox { get; }
    public ContactId Contact { get; }
    public ChannelKind Channel { get; }
    public string CursorKey { get; }
    public RoutingSelection Selection { get; }

    /// <summary>Candidates in agent identifier order (ordinal).</summary>
    public IReadOnlyList<RoutingCandidate> Candidates { get; }

    public UserId? Preferred { get; }
    public TimeSpan Lease { get; }

    /// <summary>
    /// The reference selection over <paramref name="loads"/> (current load per candidate agent) and the stored cursor (last chosen
    /// agent identifier). Stores may implement it in SQL but must choose identically.
    /// </summary>
    public RoutingCandidate? Select(IReadOnlyDictionary<UserId, int> loads, string? cursor)
    {
        ArgumentNullException.ThrowIfNull(loads);
        var count = Candidates.Count;
        if (Preferred is { } preferred)
        {
            var idx = BinarySearchAgent(preferred.Value);
            if (idx >= 0)
            {
                var sticky = Candidates[idx];
                if (loads.GetValueOrDefault(sticky.Agent) < sticky.Capacity)
                    return sticky;
            }
        }
        var start = cursor is null ? 0 : FindFirstAfter(cursor);
        if (Selection == RoutingSelection.RoundRobin)
        {
            for (var i = 0; i < count; i++)
            {
                var candidate = Candidates[(start + i) % count];
                if (loads.GetValueOrDefault(candidate.Agent) < candidate.Capacity)
                    return candidate;
            }
            return null;
        }
        if (Selection == RoutingSelection.LeastBusy)
        {
            RoutingCandidate? best = null;
            long bestLoad = 0;
            long bestCap = 1;
            for (var i = 0; i < count; i++)
            {
                var candidate = Candidates[(start + i) % count];
                var load = loads.GetValueOrDefault(candidate.Agent);
                if (load >= candidate.Capacity)
                    continue;
                if (best is null || (long)load * bestCap < bestLoad * candidate.Capacity)
                {
                    best = candidate;
                    bestLoad = load;
                    bestCap = candidate.Capacity;
                    if (load == 0)
                        return best;
                }
            }
            return best;
        }
        throw new InvalidOperationException("Unknown selection.");
    }

    private int BinarySearchAgent(string agentId)
    {
        int lo = 0, hi = Candidates.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var cmp = StringComparer.Ordinal.Compare(Candidates[mid].Agent.Value, agentId);
            if (cmp == 0)
                return mid;
            if (cmp < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return -1;
    }

    private int FindFirstAfter(string cursor)
    {
        int lo = 0, hi = Candidates.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (StringComparer.Ordinal.Compare(Candidates[mid].Agent.Value, cursor) <= 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo == Candidates.Count ? 0 : lo;
    }
}

/// <summary>The reservation was written; <see cref="Load"/> is the agent's load on the channel including it.</summary>
public sealed record RoutingReserved(RoutingAssignment Assignment, int Load);

/// <summary>No candidate is under capacity. Nothing was written.</summary>
public sealed record RoutingNoCapacity
{
    private RoutingNoCapacity()
    {
    }

    public static RoutingNoCapacity Instance { get; } = new();
}

/// <summary>Another worker holds a live reservation, or a newer assignment is recorded. Nothing was written.</summary>
public sealed record RoutingAlreadyReserved(RoutingAssignment Existing);

/// <summary>Outcome of a reservation. Switch over every case without a discard arm.</summary>
public union RoutingReservationResult(RoutingReserved, RoutingNoCapacity, RoutingAlreadyReserved);

/// <summary>An assignment observed on a conversation (engine-confirmed or manual), recorded without a capacity check.</summary>
public sealed record RoutingAssignmentRecord
{
    public RoutingAssignmentRecord(ConversationId conversation, long conversationRevision, UserId agent, InboxId inbox, ContactId contact, ChannelKind channel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(conversationRevision, 1);
        if (conversation == default || agent == default || inbox == default || contact == default || !channel.IsInitialized)
            throw new ArgumentException("Conversation, agent, inbox, contact and channel are required.", conversation == default ? nameof(conversation) : agent == default ? nameof(agent) : inbox == default ? nameof(inbox) : contact == default ? nameof(contact) : nameof(channel));
        Conversation = conversation;
        ConversationRevision = conversationRevision;
        Agent = agent;
        Inbox = inbox;
        Contact = contact;
        Channel = channel;
    }

    public ConversationId Conversation { get; }
    public long ConversationRevision { get; }
    public UserId Agent { get; }
    public InboxId Inbox { get; }
    public ContactId Contact { get; }
    public ChannelKind Channel { get; }
}

/// <summary>The contact's most recent assignee, used by sticky-owner routing.</summary>
public sealed record StickyOwner(ContactId Contact, UserId Agent, DateTimeOffset AssignedAt);
