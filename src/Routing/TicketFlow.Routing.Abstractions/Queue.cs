using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Routing;

/// <summary>
/// The routing-relevant snapshot of an unassigned open conversation, including the Access resource fields (inbox, team, labels and
/// attribute condition values) so queue lists can apply Access filters inside the store query.
/// </summary>
public sealed record RoutingQueueSnapshot
{
    public RoutingQueueSnapshot(ConversationId conversation, InboxId inbox, TeamId? team, ContactId contact, IEnumerable<LabelId> labels,
        IEnumerable<KeyValuePair<string, string>> accessAttributes, DateTimeOffset waitingSince, long conversationRevision)
    {
        if (conversation == default || inbox == default || contact == default || team == default(TeamId))
            throw new ArgumentException("Conversation, inbox and contact identifiers are required; the team must be initialized when supplied.");
        ArgumentOutOfRangeException.ThrowIfLessThan(conversationRevision, 1);
        Conversation = conversation;
        Inbox = inbox;
        Team = team;
        Contact = contact;
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(accessAttributes);
        var labelSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label == default)
                throw new ArgumentException("Labels must be initialized.", nameof(labels));
            if (labelSet.Add(label.Value) && labelSet.Count > Conversations.Conversation.MaximumLabels)
                throw new ArgumentException($"At most {Conversations.Conversation.MaximumLabels} labels are allowed.", nameof(labels));
        }
        Labels = [.. labelSet.Select(label => new LabelId(label))];
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in accessAttributes)
        {
            RoutingText.RequireToken(key, AccessResource.MaximumAttributeKeyLength, nameof(accessAttributes), "Attribute keys");
            ArgumentNullException.ThrowIfNull(value, nameof(accessAttributes));
            if (value.Length > AccessResource.MaximumAttributeValueLength || !attributes.TryAdd(key, value)
                || attributes.Count > Conversations.Conversation.MaximumAttributes)
                throw new ArgumentException("Attributes must be unique, bounded and at most 4096 characters each.", nameof(accessAttributes));
        }
        AccessAttributes = attributes.AsReadOnly();
        WaitingSince = RoutingText.Instant(waitingSince);
        ConversationRevision = conversationRevision;
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public TeamId? Team { get; }
    public ContactId Contact { get; }
    public IReadOnlyList<LabelId> Labels { get; }

    /// <summary>Attribute condition values (may be personal data; never logged).</summary>
    public IReadOnlyDictionary<string, string> AccessAttributes { get; }

    public DateTimeOffset WaitingSince { get; }
    public long ConversationRevision { get; }

    /// <summary>Snapshot of <paramref name="stored"/>; waiting since its <see cref="Conversation.WaitingSince"/> or else <paramref name="now"/>.</summary>
    public static RoutingQueueSnapshot From(StoredConversation stored, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var resource = ConversationAccess.Resource(stored);
        var conversation = stored.Conversation;
        return new(conversation.Id, conversation.Inbox, conversation.Team, conversation.Contact, conversation.Labels, resource.Attributes,
            conversation.WaitingSince ?? now, stored.Revision);
    }

    public bool Equals(RoutingQueueSnapshot? other) =>
        other is not null && Conversation == other.Conversation && Inbox == other.Inbox && Team == other.Team && Contact == other.Contact
        && Labels.SequenceEqual(other.Labels) && AccessAttributes.SequenceEqual(other.AccessAttributes) && WaitingSince == other.WaitingSince
        && ConversationRevision == other.ConversationRevision;

    public override int GetHashCode() => HashCode.Combine(Conversation, ConversationRevision);

    public override string ToString() => $"RoutingQueueSnapshot {{ Conversation = {Conversation} }}";
}

/// <summary>
/// A queued conversation. <see cref="TargetInbox"/> is the queue it waits in (its own inbox until an inbox overflow moves it);
/// <see cref="QueuedAt"/> starts the overflow and maximum-wait clocks; <see cref="OverflowStage"/> counts applied overflow rules.
/// </summary>
public sealed record RoutingQueueEntry
{
    public RoutingQueueEntry(RoutingQueueSnapshot snapshot, InboxId targetInbox, DateTimeOffset queuedAt, int overflowStage, bool waitExceeded,
        DateTimeOffset nextAttemptAt, int attempts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (targetInbox == default)
            throw new ArgumentException("A target inbox is required.", nameof(targetInbox));
        ArgumentOutOfRangeException.ThrowIfNegative(overflowStage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(overflowStage, RoutingPolicy.MaximumOverflowRules);
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        Snapshot = snapshot;
        TargetInbox = targetInbox;
        QueuedAt = RoutingText.Instant(queuedAt);
        OverflowStage = overflowStage;
        WaitExceeded = waitExceeded;
        NextAttemptAt = RoutingText.Instant(nextAttemptAt);
        Attempts = attempts;
    }

    public RoutingQueueSnapshot Snapshot { get; }
    public ConversationId Conversation => Snapshot.Conversation;
    public InboxId TargetInbox { get; }
    public DateTimeOffset QueuedAt { get; }
    public int OverflowStage { get; }
    public bool WaitExceeded { get; }
    public DateTimeOffset NextAttemptAt { get; }
    public int Attempts { get; }

    /// <summary>The Access resource of the queued conversation (assignee none).</summary>
    public AccessResource Resource(TicketScope scope) =>
        new(scope, Snapshot.Conversation.Value, Snapshot.Team, Snapshot.Inbox, null, Snapshot.Labels, Snapshot.AccessAttributes);
}

/// <summary>A queued conversation with its scope.</summary>
public sealed record StoredRoutingQueueEntry(TicketScope Scope, RoutingQueueEntry Entry);

/// <summary>A leased claim of a due queue entry; updates succeed only with the current <see cref="Fence"/> while the lease is held.</summary>
public sealed record RoutingQueueClaim(TicketScope Scope, RoutingQueueEntry Entry, long Fence, DateTimeOffset LeaseUntil);

/// <summary>The result of a routing attempt on a claimed entry: the next attempt time and any overflow progress.</summary>
public sealed record RoutingQueueUpdate
{
    public RoutingQueueUpdate(DateTimeOffset nextAttemptAt, InboxId targetInbox, int overflowStage, bool waitExceeded)
    {
        if (targetInbox == default)
            throw new ArgumentException("A target inbox is required.", nameof(targetInbox));
        ArgumentOutOfRangeException.ThrowIfNegative(overflowStage);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(overflowStage, RoutingPolicy.MaximumOverflowRules);
        NextAttemptAt = RoutingText.Instant(nextAttemptAt);
        TargetInbox = targetInbox;
        OverflowStage = overflowStage;
        WaitExceeded = waitExceeded;
    }

    public DateTimeOffset NextAttemptAt { get; }
    public InboxId TargetInbox { get; }
    public int OverflowStage { get; }
    public bool WaitExceeded { get; }
}

/// <summary>Keyset position in a queue list: waiting since ascending, then conversation identifier ascending (ordinal).</summary>
public sealed record RoutingQueueCursor
{
    public RoutingQueueCursor(DateTimeOffset waitingSince, ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        WaitingSince = RoutingText.Instant(waitingSince);
        Conversation = conversation;
    }

    public DateTimeOffset WaitingSince { get; }
    public ConversationId Conversation { get; }
}

/// <summary>A bounded queue list: optional target inbox and conversation team, ordered by waiting since (oldest first).</summary>
public sealed record RoutingQueueQuery
{
    public const int MaximumLimit = 100;

    public RoutingQueueQuery(InboxId? inbox = null, TeamId? team = null, int limit = 25, RoutingQueueCursor? after = null)
    {
        if (inbox == default(InboxId) || team == default(TeamId))
            throw new ArgumentException("Criteria must be initialized when supplied.");
        if (limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limits are 1-{MaximumLimit}.");
        Inbox = inbox;
        Team = team;
        Limit = limit;
        After = after;
    }

    public InboxId? Inbox { get; }
    public TeamId? Team { get; }
    public int Limit { get; }
    public RoutingQueueCursor? After { get; }

    public bool Matches(RoutingQueueEntry entry) =>
        (Inbox is null || entry.TargetInbox == Inbox) && (Team is null || entry.Snapshot.Team == Team);

    public bool IsAfterCursor(RoutingQueueEntry entry) =>
        After is not { } cursor || entry.Snapshot.WaitingSince > cursor.WaitingSince
        || (entry.Snapshot.WaitingSince == cursor.WaitingSince && string.CompareOrdinal(entry.Conversation.Value, cursor.Conversation.Value) > 0);
}

/// <summary>One page; <see cref="Next"/> is <see langword="null"/> at the end.</summary>
public sealed record RoutingQueuePage(IReadOnlyList<StoredRoutingQueueEntry> Entries, RoutingQueueCursor? Next);
