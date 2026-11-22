using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Automation;
using TicketFlow.Conversations;
using TicketFlow.Core;

using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>Automation tools: rules and dry runs, macros, canned responses.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Automation)]
public sealed class AutomationTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "automation_list_rules", Title = "List automation rules", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<AutomationRuleOutput>))]
    [Description("Lists automation rules (name, enabled, priority) with their versioned definition JSON.")]
    [TicketFlowTool(P.AutomationRead)]
    public Task<CallToolResult> ListRules(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAutomationRuleService>().ListAsync(principal, cancellationToken), list => new ToolList<AutomationRuleOutput>(
            [.. list.Select(stored => new AutomationRuleOutput(stored.Rule.Id.Value, stored.Rule.Name, stored.Rule.Enabled, stored.Rule.Priority, stored.Revision,
                Json(AutomationCodec.SerializeRule(stored.Rule))))])), cancellationToken);

    [McpServerTool(Name = "automation_dry_run", Title = "Dry-run automation", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(DryRunOutput))]
    [Description("Evaluates automation rules against a live conversation for a hypothetical event without changing anything: which rules would match and what each action would do (rendered from what the caller may see)." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.AutomationRead, P.ConversationRead)]
    public Task<CallToolResult> DryRun(
        [Description("Conversation id.")] string conversationId,
        [Description("Event kind: conversationCreated, messageReceived, messageSent, conversationUpdated, statusChanged, assignmentChanged, scheduled or contactUpdated.")] string eventKind,
        [Description("For statusChanged: the new status (open, pending, snoozed, resolved, closed).")] string? status = null,
        [Description("For conversationUpdated: the changed field.")] string? field = null,
        [Description("Only these rule ids (default: every rule).")] string[]? rules = null,
        [Description("A hypothetical message body for message conditions.")] string? messageBody = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var @event = TicketFlowToolJson.Parse<AutomationEventKind>(eventKind, nameof(eventKind)) switch
            {
                AutomationEventKind.ConversationCreated => AutomationEvent.ConversationCreated(),
                AutomationEventKind.MessageReceived => AutomationEvent.MessageReceived(),
                AutomationEventKind.MessageSent => AutomationEvent.MessageSent(),
                AutomationEventKind.ConversationUpdated => AutomationEvent.ConversationUpdated(
                    TicketFlowToolJson.Parse<ConversationUpdateField>(field ?? throw new ArgumentException("'field' is required for conversationUpdated.", nameof(field)), nameof(field))),
                AutomationEventKind.StatusChanged => AutomationEvent.StatusChanged(
                    TicketFlowToolJson.Parse<ConversationStatusKind>(status ?? throw new ArgumentException("'status' is required for statusChanged.", nameof(status)), nameof(status))),
                AutomationEventKind.AssignmentChanged => AutomationEvent.AssignmentChanged(),
                AutomationEventKind.Scheduled => AutomationEvent.Scheduled(),
                AutomationEventKind.ContactUpdated => AutomationEvent.ContactUpdated(),
                _ => throw new ArgumentException("SLA events cannot be dry-run from this tool.", nameof(eventKind)),
            };
            var request = new AutomationDryRunRequest(new ConversationId(conversationId), @event, rules?.Select(rule => new AutomationRuleId(rule)).ToList(), null, messageBody);
            return Map(await context.Service<IAutomationRuleService>().DryRunAsync(principal, request, cancellationToken), result => new DryRunOutput(
                result.Conversation.Value,
                [.. result.Rules.Select(preview => new RulePreviewOutput(preview.Rule.Value, preview.Name, TicketFlowToolJson.Token(preview.Match),
                    [.. preview.Actions.Select(action => new ActionPreviewOutput(action.Index, Json(AutomationCodec.SerializeActions([action.Action])).GetProperty("actions")[0],
                        context.UntrustedOrNull(ToolViews.Template, action.RenderedText), action.Note))]))],
                result.Stopped?.Value,
                result.EvaluatedAt));
        }, cancellationToken);

    [McpServerTool(Name = "macros_list", Title = "List macros", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<MacroOutput>))]
    [Description("Lists shared macros plus the caller's personal macros.")]
    [TicketFlowTool(P.MacroRun, Service = typeof(IMacroService))]
    public Task<CallToolResult> ListMacros(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IMacroService>().ListAsync(principal, cancellationToken), list => new ToolList<MacroOutput>(
            [.. list.Select(stored => new MacroOutput(stored.Macro.Id.Value, stored.Macro.Name, stored.Macro.Description, TicketFlowToolJson.Token(stored.Macro.Visibility),
                stored.Macro.Owner?.Value, stored.Revision, Json(AutomationCodec.SerializeMacro(stored.Macro))))])), cancellationToken);

    [McpServerTool(Name = "macros_run", Title = "Run macro", Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(MacroRunOutput))]
    [Description("Runs a macro on a conversation. Each action executes as the caller through the conversation service, so each action also needs the caller's own permission (for example conversation:reply for a reply action). Execution stops at the first refused or failed action.")]
    [TicketFlowTool(P.MacroRun, Service = typeof(IMacroService))]
    public Task<CallToolResult> RunMacro(
        [Description("Macro id.")] string macroId,
        [Description("Conversation id.")] string conversationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IMacroService>().RunAsync(principal, new MacroId(macroId), new ConversationId(conversationId),
            cancellationToken), result => new MacroRunOutput(result.Macro.Value, result.Conversation.Value, result.Run, result.Completed,
                [.. result.Outcomes.Select(Outcome)])), cancellationToken);

    [McpServerTool(Name = "canned_responses_list", Title = "List canned responses", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<CannedResponseOutput>))]
    [Description("Lists canned responses (shortcut, title, locales), optionally by shortcut prefix.")]
    [TicketFlowTool(P.ConversationReply, P.CannedResponseManage, AnyOf = true, Service = typeof(ICannedResponseService))]
    public Task<CallToolResult> ListCannedResponses(
        [Description("Shortcut prefix.")] string? prefix = null,
        [Description("Page size, 1-100 (default 25).")] int limit = 25,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var after = cursor is null ? null : TicketFlowToolJson.DecodeCursor(cursor, 1)[0];
            return Map(await context.Service<ICannedResponseService>().ListAsync(principal, new CannedResponseQuery(prefix, after, limit), cancellationToken), page =>
                new ToolPage<CannedResponseOutput>([.. page.Responses.Select(stored => new CannedResponseOutput(stored.Response.Id.Value, stored.Response.Shortcut,
                    stored.Response.Title, stored.Response.DefaultLocale, [.. stored.Response.Variants.Select(variant => variant.Locale)], stored.Revision))],
                    page.NextAfterShortcut is { } next ? TicketFlowToolJson.EncodeCursor(next) : null));
        }, cancellationToken);

    [McpServerTool(Name = "canned_responses_render", Title = "Render canned response", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(RenderedCannedResponseOutput))]
    [Description("Renders a canned response for a conversation (variables filled from what the caller may see; masked values are never substituted). Nothing is sent: reply with conversations_reply." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.ConversationReply, P.CannedResponseManage, AnyOf = true, Service = typeof(ICannedResponseService))]
    public Task<CallToolResult> RenderCannedResponse(
        [Description("Canned response id.")] string responseId,
        [Description("Conversation id.")] string conversationId,
        [Description("Preferred locale.")] string? locale = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ICannedResponseService>().RenderAsync(principal, new CannedResponseId(responseId),
            new ConversationId(conversationId), locale, cancellationToken), rendered => new RenderedCannedResponseOutput(rendered.Response.Value, rendered.Locale,
                TicketFlowToolJson.Token(rendered.ContentType), context.Untrusted(ToolViews.Template, rendered.Body))), cancellationToken);

    internal static ActionOutcomeOutput Outcome(AutomationActionOutcome outcome) => outcome switch
    {
        AutomationActionApplied applied => new("applied", applied.Sequences, null),
        AutomationActionSkipped skipped => new("skipped", null, skipped.Reason),
        AutomationActionRefused refused => new("refused", null, TicketFlowToolJson.Token(refused.Reason)),
        AutomationActionFailed failed => new("failed", null, failed.Code),
    };

    private static JsonElement Json(string json) => TicketFlowToolJson.Parse(json);

    private static object Map<T>(AutomationResult<T> result, Func<T, object> success) => result switch
    {
        AutomationValue<T> value => success(value.Value),
        AutomationNotFound => TicketFlowToolError.NotFound(),
        AutomationConflict conflict => TicketFlowToolError.Conflict(conflict.CurrentRevision),
        AutomationRefused refused => refused.Reason switch
        {
            AutomationRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            AutomationRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
    };
}
