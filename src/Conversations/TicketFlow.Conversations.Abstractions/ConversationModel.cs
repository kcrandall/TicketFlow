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
        Labels = Sorted(labels, MaximumLabels, label => label.Value, label => label != default, nameof(labels));
        Attributes = SnapshotAttributes(attributes);
        Participants = Sorted(participants, MaximumParticipants, user => user.Value, user => user != default, nameof(participants));
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
        ContactRedirects = [.. contactRedirects ?? []];
        if (ContactRedirects.Count > MaximumRedirects || ContactRedirects.Any(redirect => redirect is null)
            || ContactRedirects.Select(redirect => redirect.Merge).Distinct().Count() != ContactRedirects.Count)
            throw new ArgumentException($"A conversation carries at most {MaximumRedirects} distinct contact redirects.", nameof(contactRedirects));
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
    public IReadOnlyList<string> AttributeKeys => [.. Attributes.Keys.Order(StringComparer.Ordinal)];

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
        IEnumerable<ContactRedirect>? contactRedirects = null) =>
        new(Id, Inbox, contact ?? Contact, status ?? Status, priority ?? Priority,
            assignee.HasValue ? assignee.Value : Assignee, team.HasValue ? team.Value : Team,
            labels ?? Labels, attributes ?? Attributes, participants ?? Participants, ChannelThread, CreatedAt, lastActivityAt ?? LastActivityAt,
            waitingSince.HasValue ? waitingSince.Value : WaitingSince, firstReplyAt.HasValue ? firstReplyAt.Value : FirstReplyAt,
            lastIncomingAt.HasValue ? lastIncomingAt.Value : LastIncomingAt, lastOutgoingAt.HasValue ? lastOutgoingAt.Value : LastOutgoingAt,
            lastSequence ?? LastSequence, contactRedirects ?? ContactRedirects);

    public bool Equals(Conversation? other) =>
        other is not null && Id == other.Id && Inbox == other.Inbox && Contact == other.Contact && Status.Equals(other.Status)
        && Priority == other.Priority && Assignee == other.Assignee && Team == other.Team && Labels.SequenceEqual(other.Labels)
        && Attributes.Count == other.Attributes.Count
        && Attributes.All(pair => other.Attributes.TryGetValue(pair.Key, out var value) && value.Equals(pair.Value))
        && Participants.SequenceEqual(other.Participants) && Equals(ChannelThread, other.ChannelThread) && CreatedAt == other.CreatedAt
        && LastActivityAt == other.LastActivityAt && WaitingSince == other.WaitingSince && FirstReplyAt == other.FirstReplyAt
        && LastIncomingAt == other.LastIncomingAt && LastOutgoingAt == other.LastOutgoingAt && LastSequence == other.LastSequence
        && ContactRedirects.SequenceEqual(other.ContactRedirects);

    public override int GetHashCode() => HashCode.Combine(Id, Inbox, Contact, LastSequence);

    public override string ToString() => $"Conversation {{ Id = {Id}, Status = {Status.Kind} }}";

    private static IReadOnlyList<T> Sorted<T>(IEnumerable<T> values, int maximum, Func<T, string> key, Func<T, bool> valid, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = new SortedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!valid(value))
                throw new ArgumentException("Values must be initialized.", parameterName);
            snapshot[key(value)] = value;
            if (snapshot.Count > maximum)
                throw new ArgumentException($"At most {maximum} values are allowed.", parameterName);
        }
        return [.. snapshot.Values];
    }

    private static IReadOnlyDictionary<string, ConversationAttributeValue> SnapshotAttributes(IEnumerable<KeyValuePair<string, ConversationAttributeValue>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var snapshot = new Dictionary<string, ConversationAttributeValue>(StringComparer.Ordinal);
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
        return snapshot.AsReadOnly();
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
