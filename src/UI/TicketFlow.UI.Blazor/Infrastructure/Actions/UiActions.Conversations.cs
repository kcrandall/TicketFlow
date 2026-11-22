using TicketFlow.Assist;
using TicketFlow.Automation;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.UI.Blazor.Pages;

namespace TicketFlow.UI.Blazor.Infrastructure;

public static partial class UiActions
{
    private static void RegisterConversations(Dictionary<string, UiActionHandler> handlers)
    {
        handlers["conversation.send"] = SendAsync;
        handlers["conversation.assign"] = context => CommandAsync(context, "Assignee updated", (service, id, revision) =>
            service.AssignAsync(context.Actor, id, context.Text("assignee") is { } user ? new UserId(user) : null, revision, context.Aborted));
        handlers["conversation.team"] = context => CommandAsync(context, "Team updated", (service, id, revision) =>
            service.SetTeamAsync(context.Actor, id, context.Text("team") is { } team ? new TeamId(team) : null, revision, context.Aborted));
        handlers["conversation.priority"] = context => CommandAsync(context, "Priority updated", (service, id, revision) =>
            service.SetPriorityAsync(context.Actor, id, Enum.Parse<ConversationPriority>(context.Require("priority", "Priority"), ignoreCase: true), revision, context.Aborted));
        handlers["conversation.status"] = context => CommandAsync(context, "Status updated", (service, id, revision) =>
            service.ChangeStatusAsync(context.Actor, id, Status(context.Require("status", "Status")), revision, context.Aborted));
        handlers["conversation.labels.add"] = context => CommandAsync(context, "Label added", (service, id, revision) =>
            service.UpdateLabelsAsync(context.Actor, id, [new LabelId(context.Require("label", "Label").ToLowerInvariant().Replace(' ', '-'))], [], revision, context.Aborted));
        handlers["conversation.labels.remove"] = context => CommandAsync(context, "Label removed", (service, id, revision) =>
            service.UpdateLabelsAsync(context.Actor, id, [], [new LabelId(context.Require("label", "Label"))], revision, context.Aborted));
        handlers["conversation.macro"] = MacroAsync;
        handlers["conversation.canned"] = CannedAsync;
        handlers["conversation.assist.draft"] = DraftAsync;
        handlers["conversation.assist.summary"] = SummaryAsync;
    }

    private static ConversationId ConversationFromPath(UiActionContext context)
    {
        var segments = context.Session.Path.Split('/');
        return segments is ["conversations", var id] ? new ConversationId(Uri.UnescapeDataString(id)) : throw new UiInputException("The conversation is missing.");
    }

    private static async Task<UiActionResult> CommandAsync(UiActionContext context, string success,
        Func<IConversationService, ConversationId, long?, Task<ConversationCommandResult>> command)
    {
        if (context.Session.Service<IConversationService>() is not { } service)
            return UiOutcome.NotFound();
        // Every state-changing command is fenced on the revision the agent saw.
        return UiOutcomes.From(await command(service, ConversationFromPath(context), context.RequireRevision()), success);
    }

    private static ConversationStatus Status(string value) => value switch
    {
        "open" => ConversationStatus.Open,
        "pending" => ConversationStatus.Pending,
        "resolved" => ConversationStatus.Resolved,
        "closed" => ConversationStatus.Closed,
        "snooze-1h" => ConversationStatus.Snoozed(TimeProvider.System.GetUtcNow().AddHours(1)),
        "snooze-tomorrow" => ConversationStatus.Snoozed(new DateTimeOffset(TimeProvider.System.GetUtcNow().UtcDateTime.Date.AddDays(1).AddHours(9), TimeSpan.Zero)),
        _ => throw new UiInputException("Choose a valid status."),
    };

    /// <summary>Reply or private note (fenced on the revision the agent saw), optionally resolving afterwards.</summary>
    private static async Task<UiActionResult> SendAsync(UiActionContext context)
    {
        if (context.Session.Service<IConversationService>() is not { } service)
            return UiOutcome.NotFound();
        var body = context.Raw("body") is { } raw && raw.Trim().Length > 0 ? raw.Trim() : throw new UiInputException("Write a message before sending.");
        var id = ConversationFromPath(context);
        var note = context.Text("kind") == "note";
        var revision = context.RequireRevision();
        var result = note
            ? await service.AddNoteAsync(context.Actor, id, new NoteDraft(body), revision, context.Aborted)
            : await service.ReplyAsync(context.Actor, id, new OutgoingMessageDraft(body), revision, context.Aborted);
        var outcome = UiOutcomes.From(result, note ? "Note added" : "Reply sent");
        if (result.Value is ConversationApplied applied && context.Flag("resolve"))
            outcome = UiOutcomes.From(await service.ChangeStatusAsync(context.Actor, id, ConversationStatus.Resolved, applied.Revision, context.Aborted),
                note ? "Note added and conversation resolved" : "Reply sent and conversation resolved");
        return outcome;
    }

    private static async Task<UiActionResult> MacroAsync(UiActionContext context)
    {
        if (context.Session.Service<IMacroService>() is not { } macros)
            return UiOutcome.NotFound();
        return UiOutcomes.From(await macros.RunAsync(context.Actor, new MacroId(context.Require("macro", "Macro")), ConversationFromPath(context), context.Aborted), run =>
            run.Completed && run.Outcomes.All(outcome => outcome.Value is AutomationActionApplied or AutomationActionSkipped)
                ? UiOutcome.Success("Macro applied")
                : new UiOutcome(UiOutcomeKind.Rejected, "Some macro actions did not apply",
                    [.. run.Outcomes.Select((outcome, index) => $"Action {index + 1}: {Describe(outcome)}")]));
    }

    private static string Describe(AutomationActionOutcome outcome) => outcome switch
    {
        AutomationActionApplied => "applied",
        AutomationActionSkipped skipped => "skipped (" + skipped.Reason + ")",
        AutomationActionRefused refused => "refused (" + UiFormat.Token(refused.Reason).ToLowerInvariant() + ")",
        AutomationActionFailed failed => "failed (" + failed.Code + ")",
    };

    /// <summary>Renders a canned response for this conversation into the composer (re-rendered page, nothing sent).</summary>
    private static async Task<UiActionResult> CannedAsync(UiActionContext context)
    {
        if (context.Session.Service<ICannedResponseService>() is not { } canned)
            return UiOutcome.NotFound();
        var response = context.Text("canned") ?? throw new UiInputException("Choose a canned response to insert.");
        return await canned.RenderAsync(context.Actor, new CannedResponseId(response), ConversationFromPath(context), null, context.Aborted) switch
        {
            AutomationValue<RenderedCannedResponse> rendered => new UiActionResult(new UiOutcome(UiOutcomeKind.Info, "Canned response inserted — review and send", []),
                new ConversationDetailPage.ComposerState(Join(context.Raw("body"), rendered.Value.Body), null)),
            AutomationNotFound => UiOutcome.NotFound(),
            AutomationConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
            AutomationRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
        };
    }

    private static string Join(string? current, string addition) => string.IsNullOrWhiteSpace(current) ? addition : current.TrimEnd() + "\n\n" + addition;

    private static async Task<UiActionResult> DraftAsync(UiActionContext context)
    {
        if (context.Session.Service<IAssistService>() is not { } assist)
            return UiOutcome.NotFound();
        return await assist.DraftReplyAsync(context.Actor, new AssistDraftRequest(ConversationFromPath(context), null, null), context.Aborted) switch
        {
            AssistSucceeded<AssistDraft> draft => new UiActionResult(new UiOutcome(UiOutcomeKind.Info, "AI draft ready — review before sending", draft.Value.Grounded ? ["Grounded in help-center articles."] : []),
                new ConversationDetailPage.ComposerState(draft.Value.Body, null)),
            AssistRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
            AssistProviderFailed failed => new UiOutcome(UiOutcomeKind.Rejected, "The assistant is unavailable", [failed.Retryable ? "Try again in a moment." : "The model provider failed."]),
        };
    }

    private static async Task<UiActionResult> SummaryAsync(UiActionContext context)
    {
        if (context.Session.Service<IAssistService>() is not { } assist)
            return UiOutcome.NotFound();
        return await assist.SummarizeAsync(context.Actor, new AssistSummaryRequest(ConversationFromPath(context)), context.Aborted) switch
        {
            AssistSucceeded<AssistSummary> summary => new UiActionResult(new UiOutcome(UiOutcomeKind.Info, "Summary generated", []),
                new ConversationDetailPage.ComposerState(context.Raw("body"), summary.Value)),
            AssistRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
            AssistProviderFailed failed => new UiOutcome(UiOutcomeKind.Rejected, "The assistant is unavailable", [failed.Retryable ? "Try again in a moment." : "The model provider failed."]),
        };
    }
}
