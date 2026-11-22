using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>Assign the conversation to a user (<c>conversation:assign</c>).</summary>
public sealed record AssignUserAction
{
    public AssignUserAction(UserId user) => User = user == default ? throw new ArgumentException("A user is required.", nameof(user)) : user;

    public UserId User { get; }
}

/// <summary>Move the conversation to a team (<c>conversation:assign</c> on the current and resulting conversation).</summary>
public sealed record AssignTeamAction
{
    public AssignTeamAction(TeamId team) => Team = team == default ? throw new ArgumentException("A team is required.", nameof(team)) : team;

    public TeamId Team { get; }
}

/// <summary>
/// Hand the conversation to routing (E8): set <see cref="Team"/> when supplied, then unassign it so the routing outbox consumer picks
/// an agent (<c>conversation:assign</c>).
/// </summary>
public sealed record AssignToRoutingAction
{
    public AssignToRoutingAction(TeamId? team = null) =>
        Team = team == default(TeamId) ? throw new ArgumentException("The team must be initialized when supplied.", nameof(team)) : team;

    public TeamId? Team { get; }
}

/// <summary>Add 1-16 labels (<c>conversation:manage</c>).</summary>
public sealed record AddLabelsAction
{
    public const int MaximumLabels = 16;

    public AddLabelsAction(params IEnumerable<LabelId> labels) =>
        Labels = [.. AutomationText.Distinct(labels, MaximumLabels, label => label != default, nameof(labels), allowEmpty: false).OrderBy(label => label.Value, StringComparer.Ordinal)];

    public IReadOnlyList<LabelId> Labels { get; }

    public bool Equals(AddLabelsAction? other) => other is not null && Labels.SequenceEqual(other.Labels);

    public override int GetHashCode() => Labels.Count;
}

/// <summary>Remove 1-16 labels (<c>conversation:manage</c>).</summary>
public sealed record RemoveLabelsAction
{
    public RemoveLabelsAction(params IEnumerable<LabelId> labels) =>
        Labels = [.. AutomationText.Distinct(labels, AddLabelsAction.MaximumLabels, label => label != default, nameof(labels), allowEmpty: false).OrderBy(label => label.Value, StringComparer.Ordinal)];

    public IReadOnlyList<LabelId> Labels { get; }

    public bool Equals(RemoveLabelsAction? other) => other is not null && Labels.SequenceEqual(other.Labels);

    public override int GetHashCode() => Labels.Count;
}

/// <summary>Set the priority (<c>conversation:manage</c>).</summary>
public sealed record SetPriorityAction
{
    public SetPriorityAction(ConversationPriority priority) => Priority = AutomationText.Defined(priority, nameof(priority));

    public ConversationPriority Priority { get; }
}

/// <summary>Change the status to Open, Pending, Resolved or Closed (<c>conversation:resolve</c>); use <see cref="SnoozeAction"/> to snooze.</summary>
public sealed record SetStatusAction
{
    public SetStatusAction(ConversationStatusKind status)
    {
        Status = AutomationText.Defined(status, nameof(status));
        if (status == ConversationStatusKind.Snoozed)
            throw new ArgumentException("Use SnoozeAction to snooze.", nameof(status));
    }

    public ConversationStatusKind Status { get; }
}

/// <summary>Snooze for <see cref="Duration"/> from execution time (1 minute-365 days, whole seconds; <c>conversation:resolve</c>).</summary>
public sealed record SnoozeAction
{
    public SnoozeAction(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1) || duration > TimeSpan.FromDays(365) || duration.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Snooze durations are whole seconds between one minute and 365 days.");
        Duration = duration;
    }

    public TimeSpan Duration { get; }
}

/// <summary>Set (or with a <see langword="null"/> value remove) a conversation attribute (<c>conversation:manage</c>).</summary>
public sealed record SetAttributeAction
{
    public SetAttributeAction(string key, ConversationAttributeValue? value)
    {
        Key = ConversationAttributeDefinition.RequireKey(key, nameof(key));
        if (value is { } supplied && supplied.Value is null)
            throw new ArgumentException("The value must be initialized when supplied.", nameof(value));
        Value = value;
    }

    public string Key { get; }
    public ConversationAttributeValue? Value { get; }

    public override string ToString() => $"SetAttributeAction {{ Key = {Key} }}";
}

/// <summary>
/// Send a reply rendered from a canned response in the contact's locale (falling back to the response's default locale)
/// (<c>conversation:reply</c>). Masked values are never substituted.
/// </summary>
public sealed record SendReplyAction
{
    public SendReplyAction(CannedResponseId cannedResponse) =>
        CannedResponse = cannedResponse.IsInitialized ? cannedResponse : throw new ArgumentException("A canned response is required.", nameof(cannedResponse));

    public CannedResponseId CannedResponse { get; }
}

/// <summary>Add a private note rendered from <see cref="Template"/> (<c>note:write</c>).</summary>
public sealed record AddNoteAction
{
    public AddNoteAction(AutomationTemplate template) => Template = template ?? throw new ArgumentNullException(nameof(template));

    public AutomationTemplate Template { get; }
}

/// <summary>
/// POST a signed, identifiers-only JSON notification to an application-registered destination (HMAC-SHA256 and SSRF guard from
/// <c>TicketFlow.Channels.Api</c>). Rules only; not allowed in macros.
/// </summary>
public sealed record SendWebhookAction
{
    public SendWebhookAction(WebhookDestinationId destination) =>
        Destination = destination.IsInitialized ? destination : throw new ArgumentException("A destination is required.", nameof(destination));

    public WebhookDestinationId Destination { get; }
}

/// <summary>Ask the survey integration to invite the contact (optionally to a specific survey). Rules only.</summary>
public sealed record TriggerSurveyAction
{
    public TriggerSurveyAction(string? survey = null) => Survey = survey is null ? null : TicketScope.RequireIdentifier(survey, nameof(survey));

    /// <summary>An opaque survey identifier, or <see langword="null"/> for the integration's default selection.</summary>
    public string? Survey { get; }
}

/// <summary>
/// Carry out the <c>SlaAction</c> records of the triggering <c>sla.warning</c>/<c>sla.breached</c> event: raise priority (only
/// upward) and reassign team; notify actions are left to notification consumers (E7) and recorded as skipped. Rules with an
/// <see cref="SlaEventTrigger"/> only.
/// </summary>
public sealed record ExecuteSlaEscalationAction
{
    private ExecuteSlaEscalationAction()
    {
    }

    public static ExecuteSlaEscalationAction Instance { get; } = new();
}

/// <summary>
/// An automation or macro action. Switch over every case without a discard arm; persisted only through <see cref="AutomationCodec"/>
/// with explicit discriminators.
/// </summary>
public union AutomationAction(AssignUserAction, AssignTeamAction, AssignToRoutingAction, AddLabelsAction, RemoveLabelsAction, SetPriorityAction,
    SetStatusAction, SnoozeAction, SetAttributeAction, SendReplyAction, AddNoteAction, SendWebhookAction, TriggerSurveyAction, ExecuteSlaEscalationAction)
{
    /// <summary>True for actions that only rules may run (webhooks, surveys, SLA escalations).</summary>
    public bool IsRuleOnly => this switch
    {
        SendWebhookAction or TriggerSurveyAction or ExecuteSlaEscalationAction => true,
        AssignUserAction or AssignTeamAction or AssignToRoutingAction or AddLabelsAction or RemoveLabelsAction or SetPriorityAction or SetStatusAction
            or SnoozeAction or SetAttributeAction or SendReplyAction or AddNoteAction => false,
    };
}
