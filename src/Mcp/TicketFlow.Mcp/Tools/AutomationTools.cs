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
            [.. list.Select(RuleOutput)])), cancellationToken);

    [McpServerTool(Name = "automation_create_rule", Title = "Create automation rule", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationRuleOutput))]
    [Description("Creates an automation rule from its versioned v1 definition JSON (sensitive: rules act with the automation client's grants).")]
    [TicketFlowTool(P.AutomationManage)]
    public Task<CallToolResult> CreateRule(
        [Description("Rule id.")] string ruleId,
        [Description("Versioned rule definition JSON (same format as automation_list_rules).")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAutomationRuleService>().CreateAsync(principal,
            AutomationCodec.DeserializeRule(new AutomationRuleId(ruleId), definitionJson), cancellationToken), RuleOutput), cancellationToken);

    [McpServerTool(Name = "automation_update_rule", Title = "Update automation rule", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(AutomationRuleOutput))]
    [Description("Replaces an automation rule (fenced on expectedRevision) from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.AutomationManage)]
    public Task<CallToolResult> UpdateRule(
        [Description("Rule id.")] string ruleId,
        [Description("Current rule revision.")] long expectedRevision,
        [Description("Versioned rule definition JSON.")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAutomationRuleService>().UpdateAsync(principal,
            AutomationCodec.DeserializeRule(new AutomationRuleId(ruleId), definitionJson), expectedRevision, cancellationToken), RuleOutput), cancellationToken);

    [McpServerTool(Name = "automation_delete_rule", Title = "Delete automation rule", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(AutomationRuleOutput))]
    [Description("Deletes an automation rule (fenced on expectedRevision).")]
    [TicketFlowTool(P.AutomationManage)]
    public Task<CallToolResult> DeleteRule(
        [Description("Rule id.")] string ruleId,
        [Description("Current rule revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAutomationRuleService>().DeleteAsync(principal,
            new AutomationRuleId(ruleId), expectedRevision, cancellationToken), RuleOutput), cancellationToken);

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
                [.. result.Rules.Select(RulePreview)],
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
            [.. list.Select(MacroOutputOf)])), cancellationToken);

    [McpServerTool(Name = "macros_create", Title = "Create macro", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(MacroOutput))]
    [Description("Creates a shared (macro:manage) or personal (macro:run) macro from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.MacroManage, P.MacroRun, AnyOf = true, Service = typeof(IMacroService))]
    public Task<CallToolResult> CreateMacro(
        [Description("Macro id.")] string macroId,
        [Description("Versioned macro definition JSON (same format as macros_list).")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IMacroService>().CreateAsync(principal,
            AutomationCodec.DeserializeMacro(new MacroId(macroId), definitionJson), cancellationToken), MacroOutputOf), cancellationToken);

    [McpServerTool(Name = "macros_update", Title = "Update macro", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(MacroOutput))]
    [Description("Replaces a macro (fenced on expectedRevision) from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.MacroManage, P.MacroRun, AnyOf = true, Service = typeof(IMacroService))]
    public Task<CallToolResult> UpdateMacro(
        [Description("Macro id.")] string macroId,
        [Description("Current macro revision.")] long expectedRevision,
        [Description("Versioned macro definition JSON.")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IMacroService>().UpdateAsync(principal,
            AutomationCodec.DeserializeMacro(new MacroId(macroId), definitionJson), expectedRevision, cancellationToken), MacroOutputOf), cancellationToken);

    [McpServerTool(Name = "macros_delete", Title = "Delete macro", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(MacroOutput))]
    [Description("Deletes a macro (fenced on expectedRevision).")]
    [TicketFlowTool(P.MacroManage, P.MacroRun, AnyOf = true, Service = typeof(IMacroService))]
    public Task<CallToolResult> DeleteMacro(
        [Description("Macro id.")] string macroId,
        [Description("Current macro revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IMacroService>().DeleteAsync(principal,
            new MacroId(macroId), expectedRevision, cancellationToken), MacroOutputOf), cancellationToken);

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
                new ToolPage<CannedResponseOutput>([.. page.Responses.Select(CannedOutputOf)],
                    page.NextAfterShortcut is { } next ? TicketFlowToolJson.EncodeCursor(next) : null));
        }, cancellationToken);

    [McpServerTool(Name = "canned_responses_create", Title = "Create canned response", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(CannedResponseOutput))]
    [Description("Creates a canned response from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.CannedResponseManage, Service = typeof(ICannedResponseService))]
    public Task<CallToolResult> CreateCannedResponse(
        [Description("Canned response id.")] string responseId,
        [Description("Versioned canned response definition JSON.")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ICannedResponseService>().CreateAsync(principal,
            AutomationCodec.DeserializeCannedResponse(new CannedResponseId(responseId), definitionJson), cancellationToken), CannedOutputOf), cancellationToken);

    [McpServerTool(Name = "canned_responses_update", Title = "Update canned response", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(CannedResponseOutput))]
    [Description("Replaces a canned response (fenced on expectedRevision) from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.CannedResponseManage, Service = typeof(ICannedResponseService))]
    public Task<CallToolResult> UpdateCannedResponse(
        [Description("Canned response id.")] string responseId,
        [Description("Current canned response revision.")] long expectedRevision,
        [Description("Versioned canned response definition JSON.")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ICannedResponseService>().UpdateAsync(principal,
            AutomationCodec.DeserializeCannedResponse(new CannedResponseId(responseId), definitionJson), expectedRevision, cancellationToken), CannedOutputOf), cancellationToken);

    [McpServerTool(Name = "canned_responses_delete", Title = "Delete canned response", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(CannedResponseOutput))]
    [Description("Deletes a canned response (fenced on expectedRevision).")]
    [TicketFlowTool(P.CannedResponseManage, Service = typeof(ICannedResponseService))]
    public Task<CallToolResult> DeleteCannedResponse(
        [Description("Canned response id.")] string responseId,
        [Description("Current canned response revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ICannedResponseService>().DeleteAsync(principal,
            new CannedResponseId(responseId), expectedRevision, cancellationToken), CannedOutputOf), cancellationToken);

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

    private RulePreviewOutput RulePreview(AutomationRulePreview preview)
    {
        if (preview.Actions.Count == 0)
            return new RulePreviewOutput(preview.Rule.Value, preview.Name, TicketFlowToolJson.Token(preview.Match), []);
        var rawActions = new AutomationAction[preview.Actions.Count];
        for (var i = 0; i < preview.Actions.Count; i++)
            rawActions[i] = preview.Actions[i].Action;
        using var document = JsonDocument.Parse(AutomationCodec.SerializeActions(rawActions));
        var array = document.RootElement.GetProperty("actions");
        var outputs = new ActionPreviewOutput[preview.Actions.Count];
        for (var i = 0; i < preview.Actions.Count; i++)
        {
            var action = preview.Actions[i];
            outputs[i] = new ActionPreviewOutput(action.Index, array[i].Clone(), context.UntrustedOrNull(ToolViews.Template, action.RenderedText), action.Note);
        }
        return new RulePreviewOutput(preview.Rule.Value, preview.Name, TicketFlowToolJson.Token(preview.Match), outputs);
    }

    private static AutomationRuleOutput RuleOutput(StoredAutomationRule stored) =>
        new(stored.Rule.Id.Value, stored.Rule.Name, stored.Rule.Enabled, stored.Rule.Priority, stored.Revision, Json(AutomationCodec.SerializeRule(stored.Rule)));

    private static MacroOutput MacroOutputOf(StoredMacro stored) =>
        new(stored.Macro.Id.Value, stored.Macro.Name, stored.Macro.Description, TicketFlowToolJson.Token(stored.Macro.Visibility),
            stored.Macro.Owner?.Value, stored.Revision, Json(AutomationCodec.SerializeMacro(stored.Macro)));

    private static CannedResponseOutput CannedOutputOf(StoredCannedResponse stored) =>
        new(stored.Response.Id.Value, stored.Response.Shortcut, stored.Response.Title, stored.Response.DefaultLocale,
            [.. stored.Response.Variants.Select(variant => variant.Locale)], stored.Revision);

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
