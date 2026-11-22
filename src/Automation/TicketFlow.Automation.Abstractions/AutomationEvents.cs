using TicketFlow.Conversations;
using TicketFlow.Sla;

namespace TicketFlow.Automation;

/// <summary>Kinds of automation trigger events. Persisted numerically; never renumber.</summary>
public enum AutomationEventKind
{
    ConversationCreated = 1,
    MessageReceived = 2,
    MessageSent = 3,
    ConversationUpdated = 4,
    StatusChanged = 5,
    AssignmentChanged = 6,
    SlaEvent = 7,
    Scheduled = 8,
    ContactUpdated = 9,
}

/// <summary>
/// A concrete event rules are matched against: its kind plus the details triggers filter on (the changed field, the new status, the
/// SLA event kind, metric and escalation actions). <see cref="Sequence"/> is the timeline sequence of the triggering event, when any.
/// </summary>
public sealed record AutomationEvent
{
    private AutomationEvent(AutomationEventKind kind, ConversationUpdateField? field, ConversationStatusKind? status, SlaEventKind? sla, SlaMetric? metric,
        IReadOnlyList<SlaAction>? slaActions, long? sequence)
    {
        Kind = AutomationText.Defined(kind, nameof(kind));
        Field = field;
        Status = status;
        Sla = sla;
        Metric = metric;
        SlaActions = slaActions ?? [];
        if (sequence is < 1)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        Sequence = sequence;
    }

    public AutomationEventKind Kind { get; }
    public ConversationUpdateField? Field { get; }
    public ConversationStatusKind? Status { get; }
    public SlaEventKind? Sla { get; }
    public SlaMetric? Metric { get; }
    public IReadOnlyList<SlaAction> SlaActions { get; }
    public long? Sequence { get; }

    public static AutomationEvent ConversationCreated(long? sequence = null) => new(AutomationEventKind.ConversationCreated, null, null, null, null, null, sequence);

    public static AutomationEvent MessageReceived(long? sequence = null) => new(AutomationEventKind.MessageReceived, null, null, null, null, null, sequence);

    public static AutomationEvent MessageSent(long? sequence = null) => new(AutomationEventKind.MessageSent, null, null, null, null, null, sequence);

    public static AutomationEvent ConversationUpdated(ConversationUpdateField field, long? sequence = null) =>
        new(AutomationEventKind.ConversationUpdated, AutomationText.Defined(field, nameof(field)), null, null, null, null, sequence);

    public static AutomationEvent StatusChanged(ConversationStatusKind status, long? sequence = null) =>
        new(AutomationEventKind.StatusChanged, ConversationUpdateField.Status, AutomationText.Defined(status, nameof(status)), null, null, null, sequence);

    public static AutomationEvent AssignmentChanged(long? sequence = null) => new(AutomationEventKind.AssignmentChanged, null, null, null, null, null, sequence);

    public static AutomationEvent SlaEvent(SlaEventKind kind, SlaMetric metric, IEnumerable<SlaAction> actions, long? sequence = null) =>
        new(AutomationEventKind.SlaEvent, null, null, AutomationText.Defined(kind, nameof(kind)), AutomationText.Defined(metric, nameof(metric)),
            [.. actions ?? throw new ArgumentNullException(nameof(actions))], sequence);

    public static AutomationEvent Scheduled() => new(AutomationEventKind.Scheduled, null, null, null, null, null, null);

    public static AutomationEvent ContactUpdated() => new(AutomationEventKind.ContactUpdated, null, null, null, null, null, null);

    /// <summary>True when <paramref name="trigger"/> fires for this event.</summary>
    public bool Fires(AutomationTrigger trigger) => trigger switch
    {
        ConversationCreatedTrigger => Kind == AutomationEventKind.ConversationCreated,
        MessageReceivedTrigger => Kind == AutomationEventKind.MessageReceived,
        MessageSentTrigger => Kind == AutomationEventKind.MessageSent,
        ConversationUpdatedTrigger updated => Kind is AutomationEventKind.ConversationUpdated or AutomationEventKind.StatusChanged && Field is { } field && updated.Matches(field),
        StatusChangedTrigger changed => Kind == AutomationEventKind.StatusChanged && Status is { } status && changed.Matches(status),
        AssignmentChangedTrigger => Kind == AutomationEventKind.AssignmentChanged,
        SlaEventTrigger sla => Kind == AutomationEventKind.SlaEvent && Sla is { } slaKind && Metric is { } metric && sla.Matches(slaKind, metric),
        ScheduledTrigger => Kind == AutomationEventKind.Scheduled,
        ContactUpdatedTrigger => Kind == AutomationEventKind.ContactUpdated,
    };

    public bool Equals(AutomationEvent? other) =>
        other is not null && Kind == other.Kind && Field == other.Field && Status == other.Status && Sla == other.Sla && Metric == other.Metric
        && SlaActions.SequenceEqual(other.SlaActions) && Sequence == other.Sequence;

    public override int GetHashCode() => HashCode.Combine(Kind, Field, Status, Sla, Sequence);
}
