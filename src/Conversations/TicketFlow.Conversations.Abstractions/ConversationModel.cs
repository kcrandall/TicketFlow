using System.Collections.Frozen;
using System.Collections.ObjectModel;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>Persisted numeric status discriminators. Never renumber.</summary>
public enum ConversationStatusKind
{
    Open = 1,
    Pending = 2,
    Snoozed = 3,
    Resolved = 4,
    Closed = 5,
}

/// <summary>Agents are working on the conversation.</summary>
public sealed record OpenStatus
{
    private OpenStatus()
    {
    }

    public static OpenStatus Instance { get; } = new();
}

/// <summary>Waiting on the contact.</summary>
public sealed record PendingStatus
{
    private PendingStatus()
    {
    }

    public static PendingStatus Instance { get; } = new();
}

/// <summary>Hidden from active queues until <see cref="Until"/> (UTC, microsecond precision) or the next incoming message.</summary>
public sealed record SnoozedStatus
{
    public SnoozedStatus(DateTimeOffset until) => Until = ConversationText.Instant(until);

    public DateTimeOffset Until { get; }
}

/// <summary>Resolved; an incoming message reopens it.</summary>
public sealed record ResolvedStatus
{
    private ResolvedStatus()
    {
    }

    public static ResolvedStatus Instance { get; } = new();
}

/// <summary>Terminal: no further messages, notes or status changes are accepted.</summary>
public sealed record ClosedStatus
{
    private ClosedStatus()
    {
    }

    public static ClosedStatus Instance { get; } = new();
}

/// <summary>Conversation status. Switch over every case without a discard arm; persisted as <see cref="ConversationStatusKind"/> plus the snooze instant.</summary>
public union ConversationStatus(OpenStatus, PendingStatus, SnoozedStatus, ResolvedStatus, ClosedStatus)
{
    public static ConversationStatus Open => OpenStatus.Instance;
    public static ConversationStatus Pending => PendingStatus.Instance;
    public static ConversationStatus Resolved => ResolvedStatus.Instance;
    public static ConversationStatus Closed => ClosedStatus.Instance;

    public static ConversationStatus Snoozed(DateTimeOffset until) => new SnoozedStatus(until);

    /// <exception cref="System.Runtime.CompilerServices.SwitchExpressionException">The status is <c>default</c>.</exception>
    public ConversationStatusKind Kind => this switch
    {
        OpenStatus => ConversationStatusKind.Open,
        PendingStatus => ConversationStatusKind.Pending,
        SnoozedStatus => ConversationStatusKind.Snoozed,
        ResolvedStatus => ConversationStatusKind.Resolved,
        ClosedStatus => ConversationStatusKind.Closed,
    };

    /// <summary>The snooze instant for <see cref="SnoozedStatus"/>, otherwise <see langword="null"/>.</summary>
    public DateTimeOffset? SnoozedUntil => this switch
    {
        OpenStatus => null,
        PendingStatus => null,
        SnoozedStatus snoozed => snoozed.Until,
        ResolvedStatus => null,
        ClosedStatus => null,
    };

    /// <summary>Restores a persisted status.</summary>
    public static ConversationStatus FromKind(ConversationStatusKind kind, DateTimeOffset? snoozedUntil) => kind switch
    {
        ConversationStatusKind.Open when snoozedUntil is null => Open,
        ConversationStatusKind.Pending when snoozedUntil is null => Pending,
        ConversationStatusKind.Snoozed when snoozedUntil is { } until => Snoozed(until),
        ConversationStatusKind.Resolved when snoozedUntil is null => Resolved,
        ConversationStatusKind.Closed when snoozedUntil is null => Closed,
        _ => throw new FormatException("The persisted conversation status is invalid."),
    };
}

/// <summary>Persisted priority values. Never renumber.</summary>
public enum ConversationPriority
{
    None = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    Urgent = 5,
}

/// <summary>
/// Opaque channel correlation for a conversation: the channel kind token and the provider's thread identifier (an email thread
/// root, an SMS number pair, a chat session). Correlation evidence only, never authentication; the identifier may contain
/// addresses, so it is masked through <c>conversation.channel</c> and redacted from <see cref="ToString"/>.
/// </summary>
public sealed record ChannelThreadRef
{
    public const int MaximumChannelLength = 64;
    public const int MaximumThreadIdLength = 1024;

    public ChannelThreadRef(string channel, string externalThreadId)
    {
        Channel = ConversationText.RequireToken(channel, MaximumChannelLength, nameof(channel), "Channel kinds");
        ExternalThreadId = ConversationText.RequireText(externalThreadId, MaximumThreadIdLength, nameof(externalThreadId));
    }

    /// <summary>The channel kind token (for example <c>email</c>, <c>sms</c>, <c>live-chat</c>).</summary>
    public string Channel { get; }

    public string ExternalThreadId { get; }

    public override string ToString() => $"ChannelThreadRef {{ Channel = {Channel}, <redacted> }}";
}

/// <summary>A contact re-pointing applied by a contact merge: <see cref="Previous"/> was replaced by the merge target.</summary>
public sealed record ContactRedirect
{
    public ContactRedirect(Guid merge, ContactId previous)
    {
        if (merge == Guid.Empty || previous == default)
            throw new ArgumentException("A merge identifier and previous contact are required.");
        Merge = merge;
        Previous = previous;
    }

    public Guid Merge { get; }
    public ContactId Previous { get; }
}

/// <summary>
/// A conversation snapshot: the projection of its timeline. Labels and participants are kept in ordinal order and attributes are
/// keyed by definition key. <see cref="LastSequence"/> is the sequence of the newest timeline event (0 before the first).
/// SLA markers: <see cref="WaitingSince"/> is the oldest incoming message not yet answered by an outgoing reply (cleared by a
/// reply, resolve or close), <see cref="FirstReplyAt"/> the first outgoing reply.
/// </summary>
public sealed record Conversation
{
    public const int MaximumLabels = 64;
    public const int MaximumAttributes = 128;
    public const int MaximumParticipants = 64;
    public const int MaximumRedirects = 16;

    private static readonly IReadOnlyDictionary<string, ConversationAttributeValue> EmptyAttributes = FrozenDictionary<string, ConversationAttributeValue>.Empty;
    private readonly IReadOnlyList<string> attributeKeys;

    public Conversation(
        ConversationId id,
        InboxId inbox,
        ContactId contact,
        ConversationStatus status,
        ConversationPriority priority,
        UserId? assignee,
        TeamId? team,
        IEnumerable<LabelId> labels,
        IEnumerable<KeyValuePair<string, ConversationAttributeValue>> attributes,
        IEnumerable<UserId> participants,
        ChannelThreadRef? channelThread,
        DateTimeOffset createdAt,
        DateTimeOffset lastActivityAt,
        DateTimeOffset? waitingSince,
        DateTimeOffset? firstReplyAt,
        DateTimeOffset? lastIncomingAt,
        DateTimeOffset? lastOutgoingAt,
        long lastSequence,
        IEnumerable<ContactRedirect>? contactRedirects = null)
    {
        if (id == default || inbox == default || contact == default)
            throw new ArgumentException("Conversation, inbox and contact identifiers are required.");
        if (status.Value is null)
            throw new ArgumentException("A status is required.", nameof(status));
        if (!Enum.IsDefined(priority))
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (assignee == default(UserId) || team == default(TeamId))
            throw new ArgumentException("Assignee and team must be initialized when supplied.");
        ArgumentOutOfRangeException.ThrowIfNegative(lastSequence);
        Id = id;
        Inbox = inbox;
        Contact = contact;
        Status = status;
        Priority = priority;
        Assignee = assignee;
        Team = team;
        Labels = SortedLabels(labels, nameof(labels));
        (Attributes, attributeKeys) = SnapshotAttributes(attributes);
        Participants = SortedUsers(participants, nameof(participants));
        ChannelThread = channelThread;
        CreatedAt = ConversationText.Instant(createdAt);
        LastActivityAt = ConversationText.Instant(lastActivityAt);
        WaitingSince = ConversationText.Instant(waitingSince);
        FirstReplyAt = ConversationText.Instant(firstReplyAt);
        LastIncomingAt = ConversationText.Instant(lastIncomingAt);
        LastOutgoingAt = ConversationText.Instant(lastOutgoingAt);
        if (LastActivityAt < CreatedAt)
            throw new ArgumentException("Last activity cannot precede creation.", nameof(lastActivityAt));
        LastSequence = lastSequence;
        ContactRedirects = NormalizeRedirects(contactRedirects, nameof(contactRedirects));
    }

    private Conversation(
        ConversationId id,
        InboxId inbox,
        ContactId contact,
        ConversationStatus status,
        ConversationPriority priority,
        UserId? assignee,
        TeamId? team,
        IReadOnlyList<LabelId> labels,
        IReadOnlyDictionary<string, ConversationAttributeValue> attributes,
        IReadOnlyList<string> attributeKeys,
        IReadOnlyList<UserId> participants,
        ChannelThreadRef? channelThread,
        DateTimeOffset createdAt,
        DateTimeOffset lastActivityAt,
        DateTimeOffset? waitingSince,
        DateTimeOffset? firstReplyAt,
        DateTimeOffset? lastIncomingAt,
        DateTimeOffset? lastOutgoingAt,
        long lastSequence,
        IReadOnlyList<ContactRedirect> contactRedirects)
    {
        Id = id;
        Inbox = inbox;
        Contact = contact;
        Status = status;
        Priority = priority;
        Assignee = assignee;
        Team = team;
        Labels = labels;
        Attributes = attributes;
        this.attributeKeys = attributeKeys;
        Participants = participants;
        ChannelThread = channelThread;
        CreatedAt = createdAt;
        LastActivityAt = lastActivityAt;
        WaitingSince = waitingSince;
        FirstReplyAt = firstReplyAt;
        LastIncomingAt = lastIncomingAt;
        LastOutgoingAt = lastOutgoingAt;
        LastSequence = lastSequence;
        ContactRedirects = contactRedirects;
    }

    public ConversationId Id { get; }
    public InboxId Inbox { get; }
    public ContactId Contact { get; }
    public ConversationStatus Status { get; }
    public ConversationPriority Priority { get; }
    public UserId? Assignee { get; }
    public TeamId? Team { get; }

    /// <summary>Labels in ordinal order.</summary>
    public IReadOnlyList<LabelId> Labels { get; }

    /// <summary>Attribute values keyed by definition key. Personal data may be present; never logged.</summary>
    public IReadOnlyDictionary<string, ConversationAttributeValue> Attributes { get; }

    /// <summary>Attribute keys in ordinal order.</summary>
    public IReadOnlyList<string> AttributeKeys => attributeKeys;

    /// <summary>Users following the conversation, in ordinal order.</summary>
    public IReadOnlyList<UserId> Participants { get; }

    public ChannelThreadRef? ChannelThread { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivityAt { get; }
    public DateTimeOffset? WaitingSince { get; }
    public DateTimeOffset? FirstReplyAt { get; }
    public DateTimeOffset? LastIncomingAt { get; }
    public DateTimeOffset? LastOutgoingAt { get; }
    public long LastSequence { get; }

    /// <summary>Contact merge re-pointings, oldest first.</summary>
    public IReadOnlyList<ContactRedirect> ContactRedirects { get; }

    /// <summary>A copy with the supplied values replaced.</summary>
    public Conversation With(
        ContactId? contact = null,
        ConversationStatus? status = null,
        ConversationPriority? priority = null,
        Optional<UserId?> assignee = default,
        Optional<TeamId?> team = default,
        IEnumerable<LabelId>? labels = null,
        IEnumerable<KeyValuePair<string, ConversationAttributeValue>>? attributes = null,
        IEnumerable<UserId>? participants = null,
        DateTimeOffset? lastActivityAt = null,
        Optional<DateTimeOffset?> waitingSince = default,
        Optional<DateTimeOffset?> firstReplyAt = default,
        Optional<DateTimeOffset?> lastIncomingAt = default,
        Optional<DateTimeOffset?> lastOutgoingAt = default,
        long? lastSequence = null,
        IEnumerable<ContactRedirect>? contactRedirects = null)
    {
        var nextContact = contact ?? Contact;
        if (nextContact == default)
            throw new ArgumentException("Conversation, inbox and contact identifiers are required.");
        var nextStatus = status ?? Status;
        if (nextStatus.Value is null)
            throw new ArgumentException("A status is required.", nameof(status));
        var nextPriority = priority ?? Priority;
        if (!Enum.IsDefined(nextPriority))
            throw new ArgumentOutOfRangeException(nameof(priority));
        var nextAssignee = assignee.HasValue ? assignee.Value : Assignee;
        var nextTeam = team.HasValue ? team.Value : Team;
        if (nextAssignee == default(UserId) || nextTeam == default(TeamId))
            throw new ArgumentException("Assignee and team must be initialized when supplied.");
        var nextSequence = lastSequence ?? LastSequence;
        ArgumentOutOfRangeException.ThrowIfNegative(nextSequence);
        var nextLastActivity = lastActivityAt is { } la ? ConversationText.Instant(la) : LastActivityAt;
        if (nextLastActivity < CreatedAt)
            throw new ArgumentException("Last activity cannot precede creation.", nameof(lastActivityAt));
        var nextLabels = labels is null || ReferenceEquals(labels, Labels) ? Labels : SortedLabels(labels, nameof(labels));
        var (nextAttributes, nextKeys) = attributes is null || ReferenceEquals(attributes, Attributes) ? (Attributes, attributeKeys) : SnapshotAttributes(attributes);
        var nextParticipants = participants is null || ReferenceEquals(participants, Participants) ? Participants : SortedUsers(participants, nameof(participants));
        var nextRedirects = contactRedirects is null || ReferenceEquals(contactRedirects, ContactRedirects) ? ContactRedirects : NormalizeRedirects(contactRedirects, nameof(contactRedirects));
        return new(Id, Inbox, nextContact, nextStatus, nextPriority, nextAssignee, nextTeam, nextLabels, nextAttributes, nextKeys, nextParticipants,
            ChannelThread, CreatedAt, nextLastActivity,
            waitingSince.HasValue ? ConversationText.Instant(waitingSince.Value) : WaitingSince,
            firstReplyAt.HasValue ? ConversationText.Instant(firstReplyAt.Value) : FirstReplyAt,
            lastIncomingAt.HasValue ? ConversationText.Instant(lastIncomingAt.Value) : LastIncomingAt,
            lastOutgoingAt.HasValue ? ConversationText.Instant(lastOutgoingAt.Value) : LastOutgoingAt,
            nextSequence, nextRedirects);
    }

    public bool Equals(Conversation? other)
    {
        if (other is null || Id != other.Id || Inbox != other.Inbox || Contact != other.Contact || !Status.Equals(other.Status)
            || Priority != other.Priority || Assignee != other.Assignee || Team != other.Team
            || Labels.Count != other.Labels.Count || Attributes.Count != other.Attributes.Count
            || Participants.Count != other.Participants.Count || ContactRedirects.Count != other.ContactRedirects.Count
            || !Equals(ChannelThread, other.ChannelThread) || CreatedAt != other.CreatedAt
            || LastActivityAt != other.LastActivityAt || WaitingSince != other.WaitingSince || FirstReplyAt != other.FirstReplyAt
            || LastIncomingAt != other.LastIncomingAt || LastOutgoingAt != other.LastOutgoingAt || LastSequence != other.LastSequence)
            return false;
        for (var i = 0; i < Labels.Count; i++)
            if (Labels[i] != other.Labels[i])
                return false;
        for (var i = 0; i < Participants.Count; i++)
            if (Participants[i] != other.Participants[i])
                return false;
        for (var i = 0; i < ContactRedirects.Count; i++)
            if (ContactRedirects[i] != other.ContactRedirects[i])
                return false;
        for (var i = 0; i < attributeKeys.Count; i++)
        {
            var key = attributeKeys[i];
            if (!string.Equals(key, other.attributeKeys[i], StringComparison.Ordinal) || !Attributes[key].Equals(other.Attributes[key]))
                return false;
        }
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Id, Inbox, Contact, LastSequence);

    public override string ToString() => $"Conversation {{ Id = {Id}, Status = {Status.Kind} }}";

    private static IReadOnlyList<LabelId> SortedLabels(IEnumerable<LabelId> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values is ICollection<LabelId> { Count: 0 } or IReadOnlyCollection<LabelId> { Count: 0 })
            return [];
        var list = values.TryGetNonEnumeratedCount(out var count) ? new List<LabelId>(Math.Min(count, MaximumLabels)) : [];
        foreach (var value in values)
        {
            if (value == default)
                throw new ArgumentException("Values must be initialized.", parameterName);
            list.Add(value);
        }
        if (list.Count == 0)
            return [];
        if (list.Count == 1)
            return list.AsReadOnly();
        list.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        var write = 1;
        for (var read = 1; read < list.Count; read++)
            if (!string.Equals(list[read].Value, list[write - 1].Value, StringComparison.Ordinal))
                list[write++] = list[read];
        if (write > MaximumLabels)
            throw new ArgumentException($"At most {MaximumLabels} values are allowed.", parameterName);
        if (write < list.Count)
            list.RemoveRange(write, list.Count - write);
        return list.AsReadOnly();
    }

    private static IReadOnlyList<UserId> SortedUsers(IEnumerable<UserId> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values is ICollection<UserId> { Count: 0 } or IReadOnlyCollection<UserId> { Count: 0 })
            return [];
        var list = values.TryGetNonEnumeratedCount(out var count) ? new List<UserId>(Math.Min(count, MaximumParticipants)) : [];
        foreach (var value in values)
        {
            if (value == default)
                throw new ArgumentException("Values must be initialized.", parameterName);
            list.Add(value);
        }
        if (list.Count == 0)
            return [];
        if (list.Count == 1)
            return list.AsReadOnly();
        list.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        var write = 1;
        for (var read = 1; read < list.Count; read++)
            if (!string.Equals(list[read].Value, list[write - 1].Value, StringComparison.Ordinal))
                list[write++] = list[read];
        if (write > MaximumParticipants)
            throw new ArgumentException($"At most {MaximumParticipants} values are allowed.", parameterName);
        if (write < list.Count)
            list.RemoveRange(write, list.Count - write);
        return list.AsReadOnly();
    }

    private static IReadOnlyList<ContactRedirect> NormalizeRedirects(IEnumerable<ContactRedirect>? redirects, string parameterName)
    {
        if (redirects is null)
            return [];
        if (redirects is ICollection<ContactRedirect> { Count: 0 } or IReadOnlyCollection<ContactRedirect> { Count: 0 })
            return [];
        ContactRedirect[] list = [.. redirects];
        if (list.Length == 0)
            return [];
        if (list.Length > MaximumRedirects)
            throw new ArgumentException($"A conversation carries at most {MaximumRedirects} distinct contact redirects.", parameterName);
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i] is null)
                throw new ArgumentException($"A conversation carries at most {MaximumRedirects} distinct contact redirects.", parameterName);
            for (var j = i + 1; j < list.Length; j++)
                if (list[j] is not null && list[i].Merge == list[j].Merge)
                    throw new ArgumentException($"A conversation carries at most {MaximumRedirects} distinct contact redirects.", parameterName);
        }
        return list;
    }

    private static (IReadOnlyDictionary<string, ConversationAttributeValue> Attributes, IReadOnlyList<string> Keys) SnapshotAttributes(
        IEnumerable<KeyValuePair<string, ConversationAttributeValue>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes is ICollection<KeyValuePair<string, ConversationAttributeValue>> { Count: 0 } or IReadOnlyCollection<KeyValuePair<string, ConversationAttributeValue>> { Count: 0 })
            return (EmptyAttributes, []);
        var capacity = attributes.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumAttributes) : 0;
        var snapshot = capacity > 0
            ? new Dictionary<string, ConversationAttributeValue>(capacity, StringComparer.Ordinal)
            : new Dictionary<string, ConversationAttributeValue>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            ConversationAttributeDefinition.RequireKey(key, nameof(attributes));
            if (value.Value is null)
                throw new ArgumentException("Attribute values are required.", nameof(attributes));
            if (!snapshot.TryAdd(key, value))
                throw new ArgumentException("Attribute keys must be unique.", nameof(attributes));
            if (snapshot.Count > MaximumAttributes)
                throw new ArgumentException($"A conversation can carry at most {MaximumAttributes} attributes.", nameof(attributes));
        }
        if (snapshot.Count == 0)
            return (EmptyAttributes, []);
        var keys = new string[snapshot.Count];
        snapshot.Keys.CopyTo(keys, 0);
        if (keys.Length > 1)
            Array.Sort(keys, StringComparer.Ordinal);
        return (new ReadOnlyDictionary<string, ConversationAttributeValue>(snapshot), keys);
    }
}

/// <summary>An optional argument that distinguishes "not supplied" from an explicit <see langword="null"/>.</summary>
public readonly record struct Optional<T>(T Value)
{
    public bool HasValue { get; } = true;

    public static implicit operator Optional<T>(T value) => new(value);
}

/// <summary>A conversation with its full scope and revision (first revision is 1).</summary>
public sealed record StoredConversation(TicketScope Scope, Conversation Conversation, long Revision)
{
    public ConversationId Id => Conversation.Id;
}
