using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Automation;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Mcp.Tools;

/// <summary>Automation tools: rules and dry runs, macros, canned responses.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Automation)]
public sealed class AutomationTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "automation_list_rules", Title = "List automation rules", ReadOnly = true, Idempotent = true)]
    [Description("Lists automation rules (name, enabled, priority) with their versioned definition JSON.")]
    [TicketFlowTool("automation:read")]
    public Task<CallToolResult> ListRules(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAutomationRuleService>().ListAsync(principal, cancellationToken), list => new
        {
            items = list.Select(stored => new
            {
                id = stored.Rule.Id.Value,
                name = stored.Rule.Name,
                enabled = stored.Rule.Enabled,
                priority = stored.Rule.Priority,
                revision = stored.Revision,
                definition = Json(AutomationCodec.SerializeRule(stored.Rule)),
            }).ToList(),
        }), cancellationToken);

    [McpServerTool(Name = "automation_dry_run", Title = "Dry-run automation", ReadOnly = true, Idempotent = true)]
    [Description("Evaluates automation rules against a live conversation for a hypothetical event without changing anything: which rules would match and what each action would do (rendered from what the caller may see).")]
    [TicketFlowTool("automation:read", "conversation:read")]
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
            return Map(await context.Service<IAutomationRuleService>().DryRunAsync(principal, request, cancellationToken), result => new
            {
                conversationId = result.Conversation.Value,
                rules = result.Rules.Select(preview => new
                {
                    ruleId = preview.Rule.Value,
                    name = preview.Name,
                    match = TicketFlowToolJson.Token(preview.Match),
                    actions = preview.Actions.Select(action => new
                    {
                        index = action.Index,
                        action = Json(AutomationCodec.SerializeActions([action.Action])).GetProperty("actions")[0],
                        renderedText = action.RenderedText is { } text ? context.Text(text, out _) : null,
                        note = action.Note,
                    }).ToList(),
                }).ToList(),
                stoppedBy = result.Stopped?.Value,
                evaluatedAt = result.EvaluatedAt,
            });
        }, cancellationToken);

    [McpServerTool(Name = "macros_list", Title = "List macros", ReadOnly = true, Idempotent = true)]
    [Description("Lists shared macros plus the caller's personal macros.")]
    [TicketFlowTool("macro:run", Service = typeof(IMacroService))]
    public Task<CallToolResult> ListMacros(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IMacroService>().ListAsync(principal, cancellationToken), list => new
        {
            items = list.Select(stored => new
            {
                id = stored.Macro.Id.Value,
                name = stored.Macro.Name,
                description = stored.Macro.Description,
                visibility = TicketFlowToolJson.Token(stored.Macro.Visibility),
                ownerId = stored.Macro.Owner?.Value,
                revision = stored.Revision,
                definition = Json(AutomationCodec.SerializeMacro(stored.Macro)),
            }).ToList(),
        }), cancellationToken);

    [McpServerTool(Name = "macros_run", Title = "Run macro", Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Runs a macro on a conversation. Each action executes as the caller through the conversation service, so each action also needs the caller's own permission (for example conversation:reply for a reply action). Execution stops at the first refused or failed action.")]
    [TicketFlowTool("macro:run", Service = typeof(IMacroService))]
    public Task<CallToolResult> RunMacro(
        [Description("Macro id.")] string macroId,
        [Description("Conversation id.")] string conversationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IMacroService>().RunAsync(principal, new MacroId(macroId), new ConversationId(conversationId),
            cancellationToken), result => new
            {
                macroId = result.Macro.Value,
                conversationId = result.Conversation.Value,
                runId = result.Run,
                completed = result.Completed,
                outcomes = result.Outcomes.Select(Outcome).ToList(),
            }), cancellationToken);

    [McpServerTool(Name = "canned_responses_list", Title = "List canned responses", ReadOnly = true, Idempotent = true)]
    [Description("Lists canned responses (shortcut, title, locales), optionally by shortcut prefix.")]
    [TicketFlowTool("conversation:reply", "canned-response:manage", AnyOf = true, Service = typeof(ICannedResponseService))]
    public Task<CallToolResult> ListCannedResponses(
        [Description("Shortcut prefix.")] string? prefix = null,
        [Description("Page size, 1-100 (default 25).")] int limit = 25,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var after = cursor is null ? null : TicketFlowToolJson.DecodeCursor(cursor, 1)[0];
            return Map(await context.Service<ICannedResponseService>().ListAsync(principal, new CannedResponseQuery(prefix, after, limit), cancellationToken), page => new
            {
                items = page.Responses.Select(stored => new
                {
                    id = stored.Response.Id.Value,
                    shortcut = stored.Response.Shortcut,
                    title = stored.Response.Title,
                    defaultLocale = stored.Response.DefaultLocale,
                    locales = stored.Response.Variants.Select(variant => variant.Locale).ToList(),
                    revision = stored.Revision,
                }).ToList(),
                next = page.NextAfterShortcut is { } next ? TicketFlowToolJson.EncodeCursor(next) : null,
            });
        }, cancellationToken);

    [McpServerTool(Name = "canned_responses_render", Title = "Render canned response", ReadOnly = true, Idempotent = true)]
    [Description("Renders a canned response for a conversation (variables filled from what the caller may see; masked values are never substituted). Nothing is sent: reply with conversations_reply.")]
    [TicketFlowTool("conversation:reply", "canned-response:manage", AnyOf = true, Service = typeof(ICannedResponseService))]
    public Task<CallToolResult> RenderCannedResponse(
        [Description("Canned response id.")] string responseId,
        [Description("Conversation id.")] string conversationId,
        [Description("Preferred locale.")] string? locale = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ICannedResponseService>().RenderAsync(principal, new CannedResponseId(responseId),
            new ConversationId(conversationId), locale, cancellationToken), rendered => new
            {
                responseId = rendered.Response.Value,
                locale = rendered.Locale,
                contentType = TicketFlowToolJson.Token(rendered.ContentType),
                body = context.Text(rendered.Body, out var truncated),
                truncated,
            }), cancellationToken);

    internal static object Outcome(AutomationActionOutcome outcome) => outcome switch
    {
        AutomationActionApplied applied => new { outcome = "applied", sequences = (IReadOnlyList<long>?)applied.Sequences, reason = (string?)null },
        AutomationActionSkipped skipped => new { outcome = "skipped", sequences = (IReadOnlyList<long>?)null, reason = (string?)skipped.Reason },
        AutomationActionRefused refused => new { outcome = "refused", sequences = (IReadOnlyList<long>?)null, reason = (string?)TicketFlowToolJson.Token(refused.Reason) },
        AutomationActionFailed failed => new { outcome = "failed", sequences = (IReadOnlyList<long>?)null, reason = (string?)failed.Code },
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

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
