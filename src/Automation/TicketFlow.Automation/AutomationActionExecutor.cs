using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;

namespace TicketFlow.Automation;

/// <summary>
/// One action to execute: <see cref="IdempotencyKey"/> (unique per execution/run and action index) derives deterministic message ids
/// and webhook delivery ids, so re-executing after a crash never duplicates a reply, note or delivery.
/// </summary>
public sealed record AutomationActionRequest(AutomationAction Action, AutomationContext Context, string IdempotencyKey, string? AgentName, AutomationRuleId? Rule = null)
{
    public override string ToString() => $"AutomationActionRequest {{ IdempotencyKey = {IdempotencyKey} }}";
}

/// <summary>
/// Executes automation and macro actions as a given principal through <see cref="IConversationService"/>, so every action is authorized
/// by Access exactly as if that principal had called the service (automation never exceeds the automation client's grants; macros never
/// exceed the agent's). Replies render canned responses from the principal's masked view; webhooks go through
/// <see cref="IAutomationWebhookSender"/>; surveys through the optional <see cref="IAutomationSurveyTrigger"/>. Also renders previews
/// without side effects.
/// </summary>
public sealed class AutomationActionExecutor
{
    private readonly IConversationService conversations;
    private readonly IConversationStore store;
    private readonly ICannedResponseStore responses;
    private readonly IAutomationWebhookSender? webhooks;
    private readonly IAutomationSurveyTrigger? surveys;
    private readonly TimeProvider time;

    public AutomationActionExecutor(IConversationService conversations, IConversationStore store, ICannedResponseStore responses, TimeProvider time,
        IAutomationWebhookSender? webhooks = null, IAutomationSurveyTrigger? surveys = null)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.responses = responses ?? throw new ArgumentNullException(nameof(responses));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.webhooks = webhooks;
        this.surveys = surveys;
    }

    public async Task<AutomationActionOutcome> ExecuteAsync(PrincipalRef actor, AutomationActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Context.Conversation.Id;
        switch (request.Action)
        {
            case AssignUserAction assign:
                return Map(await conversations.AssignAsync(actor, id, assign.User, cancellationToken: cancellationToken).ConfigureAwait(false));
            case AssignTeamAction team:
                return Map(await conversations.SetTeamAsync(actor, id, team.Team, cancellationToken: cancellationToken).ConfigureAwait(false));
            case AssignToRoutingAction routing:
            {
                var sequences = new List<long>();
                if (routing.Team is { } routingTeam)
                {
                    var teamResult = Map(await conversations.SetTeamAsync(actor, id, routingTeam, cancellationToken: cancellationToken).ConfigureAwait(false));
                    if (teamResult.Value is not AutomationActionApplied teamApplied)
                        return teamResult;
                    sequences.AddRange(teamApplied.Sequences);
                }
                var unassign = Map(await conversations.AssignAsync(actor, id, null, cancellationToken: cancellationToken).ConfigureAwait(false));
                return unassign.Value is AutomationActionApplied unassigned ? new AutomationActionApplied(sequences.Concat(unassigned.Sequences)) : unassign;
            }
            case AddLabelsAction add:
                return Map(await conversations.UpdateLabelsAsync(actor, id, add.Labels, [], cancellationToken: cancellationToken).ConfigureAwait(false));
            case RemoveLabelsAction remove:
                return Map(await conversations.UpdateLabelsAsync(actor, id, [], remove.Labels, cancellationToken: cancellationToken).ConfigureAwait(false));
            case SetPriorityAction priority:
                return Map(await conversations.SetPriorityAsync(actor, id, priority.Priority, cancellationToken: cancellationToken).ConfigureAwait(false));
            case SetStatusAction status:
                return Map(await conversations.ChangeStatusAsync(actor, id, ConversationStatus.FromKind(status.Status, null), cancellationToken: cancellationToken).ConfigureAwait(false));
            case SnoozeAction snooze:
                return Map(await conversations.SnoozeAsync(actor, id, time.GetUtcNow() + snooze.Duration, cancellationToken: cancellationToken).ConfigureAwait(false));
            case SetAttributeAction attribute:
                return Map(await conversations.UpdateAttributesAsync(actor, id, new Dictionary<string, ConversationAttributeValue?>(StringComparer.Ordinal) { [attribute.Key] = attribute.Value },
                    cancellationToken: cancellationToken).ConfigureAwait(false));
            case SendReplyAction reply:
            {
                var messageId = MessageIdFor(request.IdempotencyKey);
                if (await AlreadyRecordedAsync(actor.Scope, id, messageId, cancellationToken).ConfigureAwait(false) is { } existing)
                    return new AutomationActionApplied([existing]);
                var (body, contentType, note) = await RenderReplyAsync(actor.Scope, reply, request.Context, request.AgentName, cancellationToken).ConfigureAwait(false);
                if (body is null)
                    return string.Equals(note, "template-output-too-large", StringComparison.Ordinal) ? new AutomationActionFailed(note!) : new AutomationActionSkipped(note!);
                return Map(await conversations.ReplyAsync(actor, id, new OutgoingMessageDraft(body, contentType, Id: messageId), cancellationToken: cancellationToken).ConfigureAwait(false));
            }
            case AddNoteAction noteAction:
            {
                var messageId = MessageIdFor(request.IdempotencyKey);
                if (await AlreadyRecordedAsync(actor.Scope, id, messageId, cancellationToken).ConfigureAwait(false) is { } existing)
                    return new AutomationActionApplied([existing]);
                var body = noteAction.Template.Render(AutomationTemplateValues.Resolver(request.Context, request.AgentName));
                if (body is null)
                    return new AutomationActionFailed("template-output-too-large");
                if (string.IsNullOrWhiteSpace(body))
                    return new AutomationActionSkipped("empty-render");
                return Map(await conversations.AddNoteAsync(actor, id, new NoteDraft(body, Id: messageId), cancellationToken: cancellationToken).ConfigureAwait(false));
            }
            case SendWebhookAction webhook:
            {
                if (webhooks is null)
                    return new AutomationActionSkipped("webhook-unavailable");
                var result = await webhooks.SendAsync(new AutomationWebhookRequest(actor.Scope, webhook.Destination, DeliveryIdFor(request.IdempotencyKey),
                    AutomationWebhookPayload.Create(request, time.GetUtcNow())), cancellationToken).ConfigureAwait(false);
                return result switch
                {
                    AutomationWebhookDelivered => new AutomationActionApplied([]),
                    AutomationWebhookFailed failed => new AutomationActionFailed(SafeCode(failed.Code)),
                };
            }
            case TriggerSurveyAction survey:
            {
                if (surveys is null)
                    return new AutomationActionSkipped("survey-unavailable");
                var result = await surveys.TriggerAsync(actor.Scope, id, request.Context.Conversation.Contact, survey.Survey, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                return result switch
                {
                    AutomationSurveyTriggered => new AutomationActionApplied([]),
                    AutomationSurveyNotTriggered notTriggered => new AutomationActionSkipped(SafeCode(notTriggered.Reason)),
                };
            }
            case ExecuteSlaEscalationAction:
                return await EscalateAsync(actor, request.Context, cancellationToken).ConfigureAwait(false);
        }
        throw new ArgumentException("An action is required.", nameof(request));
    }

    /// <summary>What <paramref name="action"/> would do for <paramref name="context"/>, without side effects.</summary>
    public async Task<AutomationActionPreview> PreviewAsync(TicketScope scope, AutomationContext context, AutomationAction action, int index, string? agentName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (action)
        {
            case SendReplyAction reply:
            {
                var (body, _, note) = await RenderReplyAsync(scope, reply, context, agentName, cancellationToken).ConfigureAwait(false);
                return new AutomationActionPreview(index, action, body, note);
            }
            case AddNoteAction note:
            {
                var body = note.Template.Render(AutomationTemplateValues.Resolver(context, agentName));
                return new AutomationActionPreview(index, action, body, body is null ? "template-output-too-large" : null);
            }
            case SendWebhookAction:
                return new AutomationActionPreview(index, action, null, webhooks is null ? "webhook-unavailable" : null);
            case TriggerSurveyAction:
                return new AutomationActionPreview(index, action, null, surveys is null ? "survey-unavailable" : null);
            case ExecuteSlaEscalationAction:
                return new AutomationActionPreview(index, action, null, context.Event.SlaActions.Count == 0 ? "no-sla-actions" : null);
            case AssignUserAction or AssignTeamAction or AssignToRoutingAction or AddLabelsAction or RemoveLabelsAction or SetPriorityAction or SetStatusAction
                or SnoozeAction or SetAttributeAction:
                return new AutomationActionPreview(index, action, null, null);
        }
        throw new ArgumentException("An action is required.", nameof(action));
    }

    private async Task<(string? Body, MessageContentType ContentType, string? Note)> RenderReplyAsync(TicketScope scope, SendReplyAction reply, AutomationContext context,
        string? agentName, CancellationToken cancellationToken)
    {
        var stored = await responses.GetAsync(scope, reply.CannedResponse, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return (null, MessageContentType.PlainText, "canned-response-missing");
        var response = stored.Response;
        var body = response.Select(context.Contact?.Locale).Template.Render(AutomationTemplateValues.Resolver(context, agentName), response.Escaping);
        if (body is null)
            return (null, response.ContentType, "template-output-too-large");
        if (string.IsNullOrWhiteSpace(body))
            return (null, response.ContentType, "empty-render");
        return (body, response.ContentType, null);
    }

    private async Task<AutomationActionOutcome> EscalateAsync(PrincipalRef actor, AutomationContext context, CancellationToken cancellationToken)
    {
        var sequences = new List<long>();
        var executed = false;
        var priority = context.Conversation.Priority;
        foreach (var slaAction in context.Event.SlaActions)
        {
            AutomationActionOutcome? outcome = slaAction switch
            {
                RaisePriorityAction raise when raise.Priority > priority =>
                    Map(await conversations.SetPriorityAsync(actor, context.Conversation.Id, raise.Priority, cancellationToken: cancellationToken).ConfigureAwait(false)),
                RaisePriorityAction => new AutomationActionApplied([]),
                ReassignTeamAction reassign => Map(await conversations.SetTeamAsync(actor, context.Conversation.Id, reassign.Team, cancellationToken: cancellationToken).ConfigureAwait(false)),
                NotifyUserAction or NotifyTeamAction => (AutomationActionOutcome?)null,
            };
            if (outcome is not { } value)
                continue;
            executed = true;
            if (value.Value is not AutomationActionApplied applied)
                return value;
            sequences.AddRange(applied.Sequences);
            if (slaAction.Value is RaisePriorityAction raised && raised.Priority > priority)
                priority = raised.Priority;
        }
        return executed ? new AutomationActionApplied(sequences) : new AutomationActionSkipped("sla-notify-delegated");
    }

    private async Task<long?> AlreadyRecordedAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken) =>
        (await store.GetMessageAsync(scope, conversation, message, cancellationToken).ConfigureAwait(false))?.Sequence;

    internal static MessageId MessageIdFor(string idempotencyKey) => new("auto-" + idempotencyKey);

    internal static string DeliveryIdFor(string idempotencyKey) => "tf-auto-" + idempotencyKey;

    internal static AutomationActionOutcome Map(ConversationCommandResult result) => result switch
    {
        ConversationApplied applied => new AutomationActionApplied(applied.Events.Select(item => item.Sequence)),
        ConversationNotFound => new AutomationActionRefused(AutomationActionRefusal.NotFound),
        ConversationConflict => new AutomationActionRefused(AutomationActionRefusal.Conflict),
        ConversationRefused refused => new AutomationActionRefused(refused.Reason switch
        {
            ConversationRefusal.Forbidden => AutomationActionRefusal.Forbidden,
            ConversationRefusal.Invalid => AutomationActionRefusal.Invalid,
            ConversationRefusal.Closed => AutomationActionRefusal.Closed,
            ConversationRefusal.Rejected => AutomationActionRefusal.Rejected,
            _ => AutomationActionRefusal.Rejected,
        }),
    };

    private static string SafeCode(string code) => AutomationTextTokens.IsToken(code) ? code : "unknown";
}

internal static class AutomationTextTokens
{
    public static bool IsToken(string? value)
    {
        if (value is not { Length: > 0 and <= 64 } || value[0] is not (>= 'a' and <= 'z'))
            return false;
        for (var i = 1; i < value.Length; i++)
            if (value[i] is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
                return false;
        return true;
    }
}
