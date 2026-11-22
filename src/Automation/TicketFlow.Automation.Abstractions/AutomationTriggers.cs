using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;

namespace TicketFlow.Automation;

/// <summary>Conversation fields whose change fires <see cref="ConversationUpdatedTrigger"/>. Never renumber.</summary>
public enum ConversationUpdateField
{
    Status = 1,
    Priority = 2,
    Assignee = 3,
    Team = 4,
    Labels = 5,
    Attributes = 6,
    Participants = 7,
}

/// <summary>SLA timeline events that fire <see cref="SlaEventTrigger"/>. Never renumber.</summary>
public enum SlaEventKind
{
    /// <summary><c>sla.warning</c>: an escalation threshold below 100 % was crossed.</summary>
    Warning = 1,

    /// <summary><c>sla.breached</c>: a target was breached.</summary>
    Breached = 2,
}

/// <summary>A conversation was started (with or without an initial message).</summary>
public sealed record ConversationCreatedTrigger
{
    private ConversationCreatedTrigger()
    {
    }

    public static ConversationCreatedTrigger Instance { get; } = new();
}

/// <summary>An incoming message from the contact was recorded (including a start request's initial message).</summary>
public sealed record MessageReceivedTrigger
{
    private MessageReceivedTrigger()
    {
    }

    public static MessageReceivedTrigger Instance { get; } = new();
}

/// <summary>An outgoing reply was recorded.</summary>
public sealed record MessageSentTrigger
{
    private MessageSentTrigger()
    {
    }

    public static MessageSentTrigger Instance { get; } = new();
}

/// <summary>One of <see cref="Fields"/> changed (empty = any field).</summary>
public sealed record ConversationUpdatedTrigger
{
    public ConversationUpdatedTrigger(params IEnumerable<ConversationUpdateField> fields) =>
        Fields = [.. AutomationText.Distinct(fields, 7, Enum.IsDefined, nameof(fields)).Order()];

    /// <summary>Distinct fields in ascending order.</summary>
    public IReadOnlyList<ConversationUpdateField> Fields { get; }

    public bool Matches(ConversationUpdateField field) => Fields.Count == 0 || Fields.Contains(field);

    public bool Equals(ConversationUpdatedTrigger? other) => other is not null && Fields.SequenceEqual(other.Fields);

    public override int GetHashCode() => Fields.Count;
}

/// <summary>The status changed to one of <see cref="To"/> (empty = any status).</summary>
public sealed record StatusChangedTrigger
{
    public StatusChangedTrigger(params IEnumerable<ConversationStatusKind> to) => To = [.. AutomationText.Distinct(to, 5, Enum.IsDefined, nameof(to)).Order()];

    public IReadOnlyList<ConversationStatusKind> To { get; }

    public bool Matches(ConversationStatusKind status) => To.Count == 0 || To.Contains(status);

    public bool Equals(StatusChangedTrigger? other) => other is not null && To.SequenceEqual(other.To);

    public override int GetHashCode() => To.Count;
}

/// <summary>The assignee or the team changed.</summary>
public sealed record AssignmentChangedTrigger
{
    private AssignmentChangedTrigger()
    {
    }

    public static AssignmentChangedTrigger Instance { get; } = new();
}

/// <summary>An SLA <c>sla.warning</c> or <c>sla.breached</c> event of one of <see cref="Kinds"/> and <see cref="Metrics"/> (empty = any) was appended.</summary>
public sealed record SlaEventTrigger
{
    public SlaEventTrigger(IEnumerable<SlaEventKind> kinds, IEnumerable<SlaMetric>? metrics = null)
    {
        Kinds = [.. AutomationText.Distinct(kinds, 2, Enum.IsDefined, nameof(kinds), allowEmpty: false).Order()];
        Metrics = [.. AutomationText.Distinct(metrics ?? [], 3, Enum.IsDefined, nameof(metrics)).Order()];
    }

    public IReadOnlyList<SlaEventKind> Kinds { get; }
    public IReadOnlyList<SlaMetric> Metrics { get; }

    public bool Matches(SlaEventKind kind, SlaMetric metric) => Kinds.Contains(kind) && (Metrics.Count == 0 || Metrics.Contains(metric));

    public bool Equals(SlaEventTrigger? other) => other is not null && Kinds.SequenceEqual(other.Kinds) && Metrics.SequenceEqual(other.Metrics);

    public override int GetHashCode() => HashCode.Combine(Kinds.Count, Metrics.Count);
}

/// <summary>
/// Fires on <see cref="Schedule"/> for a bounded set of conversations: those whose status is one of <see cref="Statuses"/> (and in
/// <see cref="Inbox"/> when set), most recently active first, at most <see cref="MaximumConversations"/> per fire, restricted to what
/// the automation client may read. Conditions then select among them.
/// </summary>
public sealed record ScheduledTrigger
{
    public const int MaximumConversationLimit = 500;

    public ScheduledTrigger(AutomationSchedule schedule, IEnumerable<ConversationStatusKind> statuses, InboxId? inbox = null, int maximumConversations = 100)
    {
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        Statuses = [.. AutomationText.Distinct(statuses, 5, Enum.IsDefined, nameof(statuses), allowEmpty: false).Order()];
        if (inbox == default(InboxId))
            throw new ArgumentException("The inbox must be initialized when supplied.", nameof(inbox));
        if (maximumConversations is < 1 or > MaximumConversationLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumConversations), $"Scheduled rules scan 1-{MaximumConversationLimit} conversations.");
        Inbox = inbox;
        MaximumConversations = maximumConversations;
    }

    public AutomationSchedule Schedule { get; }
    public IReadOnlyList<ConversationStatusKind> Statuses { get; }
    public InboxId? Inbox { get; }
    public int MaximumConversations { get; }

    public bool Equals(ScheduledTrigger? other) =>
        other is not null && Schedule == other.Schedule && Statuses.SequenceEqual(other.Statuses) && Inbox == other.Inbox && MaximumConversations == other.MaximumConversations;

    public override int GetHashCode() => HashCode.Combine(Schedule, Statuses.Count, Inbox, MaximumConversations);
}

/// <summary>The conversation's contact was updated (profile, attributes, labels or identities); fires for its open conversations.</summary>
public sealed record ContactUpdatedTrigger
{
    private ContactUpdatedTrigger()
    {
    }

    public static ContactUpdatedTrigger Instance { get; } = new();
}

/// <summary>What fires a rule. Switch over every case without a discard arm; persisted only through <see cref="AutomationCodec"/>.</summary>
public union AutomationTrigger(ConversationCreatedTrigger, MessageReceivedTrigger, MessageSentTrigger, ConversationUpdatedTrigger, StatusChangedTrigger,
    AssignmentChangedTrigger, SlaEventTrigger, ScheduledTrigger, ContactUpdatedTrigger);
