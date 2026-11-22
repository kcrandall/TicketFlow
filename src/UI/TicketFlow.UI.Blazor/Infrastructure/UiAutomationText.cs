using TicketFlow.Automation;
using TicketFlow.Conversations;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>Human-readable summaries of automation triggers, conditions and actions.</summary>
public static class UiAutomationText
{
    public static (string Icon, string Text) Trigger(AutomationTrigger trigger) => trigger switch
    {
        ConversationCreatedTrigger => ("message-square-plus", "Conversation created"),
        MessageReceivedTrigger => ("message-square-text", "Message received"),
        MessageSentTrigger => ("send", "Message sent"),
        ConversationUpdatedTrigger updated => ("pencil", updated.Fields.Count == 0 ? "Conversation updated" : "Updated: " + string.Join(", ", updated.Fields.Select(field => UiFormat.Token(field).ToLowerInvariant()))),
        StatusChangedTrigger status => ("circle-dot", "Status changed to " + string.Join(" or ", status.To.Select(value => UiFormat.Status(value).ToLowerInvariant()))),
        AssignmentChangedTrigger => ("user-check", "Assignment changed"),
        SlaEventTrigger sla => ("alarm-clock", "SLA " + string.Join("/", sla.Kinds.Select(kind => kind.ToString().ToLowerInvariant()))),
        ScheduledTrigger scheduled => ("calendar-clock", $"Schedule {scheduled.Schedule.Expression} · {string.Join(", ", scheduled.Statuses.Select(value => UiFormat.Status(value).ToLowerInvariant()))}"),
        ContactUpdatedTrigger => ("contact", "Contact updated"),
    };

    public static string Condition(AutomationCondition? condition) => condition switch
    {
        null => "Always",
        AllOfCondition all => string.Join(" and ", all.Items.Select(Nested)),
        AnyOfCondition any => string.Join(" or ", any.Items.Select(Nested)),
        NegatedCondition negated => "not " + Nested(negated.Operand),
        StatusIsCondition status => "status is " + string.Join("/", status.Statuses.Select(value => UiFormat.Status(value).ToLowerInvariant())),
        PriorityCondition priority => $"priority {UiFormat.Token(priority.Comparison).ToLowerInvariant()} {priority.Priority.ToString().ToLowerInvariant()}",
        InboxIsCondition inbox => "inbox is " + string.Join("/", inbox.Inboxes.Select(id => id.Value)),
        TeamIsCondition team => "team is " + string.Join("/", team.Teams.Select(id => id.Value)) + (team.OrNone ? " or none" : ""),
        AssigneeIsCondition assignee => assignee.Users.Count == 0 ? "unassigned" : "assignee is " + string.Join("/", assignee.Users.Select(id => id.Value)),
        HasLabelsCondition labels => $"{labels.Target.ToString().ToLowerInvariant()} has {labels.Match.ToString().ToLowerInvariant()} of {string.Join(", ", labels.Labels.Select(label => label.Value))}",
        ChannelKindIsCondition channel => "channel is " + string.Join("/", channel.Kinds.Select(kind => UiFormat.Channel(kind.Value))),
        AttributeCondition => "attribute matches",
        MessageContentCondition content => $"message {UiFormat.Token(content.Operator).ToLowerInvariant()} “{content.Value}”",
        BusinessHoursCondition hours => hours.Open ? "within business hours" : "outside business hours",
        ElapsedCondition elapsed => $"{UiFormat.Token(elapsed.Since).ToLowerInvariant()} {UiFormat.Token(elapsed.Comparison).ToLowerInvariant()} {UiFormat.Duration(elapsed.Duration)} ago",
    };

    private static string Nested(AutomationCondition condition) => condition is AllOfCondition or AnyOfCondition ? $"({Condition(condition)})" : Condition(condition);

    public static (string Icon, string Text) Action(AutomationAction action) => action switch
    {
        AssignUserAction user => ("user-check", "Assign to " + user.User.Value),
        AssignTeamAction team => ("users", "Move to team " + team.Team.Value),
        AssignToRoutingAction routing => ("route", routing.Team is { } team ? $"Route via {team.Value}" : "Hand to routing"),
        AddLabelsAction labels => ("tag", "Add " + string.Join(", ", labels.Labels.Select(label => label.Value))),
        RemoveLabelsAction labels => ("tag", "Remove " + string.Join(", ", labels.Labels.Select(label => label.Value))),
        SetPriorityAction priority => ("signal-high", "Set priority " + priority.Priority.ToString().ToLowerInvariant()),
        SetStatusAction status => ("circle-dot", "Mark " + UiFormat.Status(status.Status).ToLowerInvariant()),
        SnoozeAction snooze => ("alarm-clock", "Snooze " + UiFormat.Duration(snooze.Duration)),
        SetAttributeAction attribute => ("list", "Set " + attribute.Key),
        SendReplyAction reply => ("send", "Reply with /" + reply.CannedResponse.Value),
        AddNoteAction note => ("sticky-note", "Add note: " + UiText.Excerpt(note.Template.Source, 60)),
        SendWebhookAction webhook => ("webhook", "Webhook " + webhook.Destination.Value),
        TriggerSurveyAction survey => ("smile", "Send survey " + (survey.Survey ?? "default")),
        ExecuteSlaEscalationAction => ("siren", "Run SLA escalations"),
    };
}
