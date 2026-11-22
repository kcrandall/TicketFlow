using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Core;
using TicketFlow.Sla;

namespace TicketFlow.Mcp.Tools;

/// <summary>SLA tools: conversation SLA status, the at-risk/breached list and policies.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Sla)]
public sealed class SlaTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "sla_get_status", Title = "Get SLA status", ReadOnly = true, Idempotent = true)]
    [Description("Returns a conversation's SLA clocks (first response, next response, resolution): elapsed and remaining seconds, predicted breach time, at-risk and breached flags.")]
    [TicketFlowTool("sla:read")]
    public Task<CallToolResult> GetStatus(
        [Description("Conversation id.")] string conversationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ISlaService>().GetStatusAsync(principal, new ConversationId(conversationId), cancellationToken), Status),
            cancellationToken);

    [McpServerTool(Name = "sla_risk_list", Title = "List SLA risks", ReadOnly = true, Idempotent = true)]
    [Description("Lists conversations whose SLA is at risk and/or breached, soonest due first.")]
    [TicketFlowTool("sla:read")]
    public Task<CallToolResult> RiskList(
        [Description("atRiskOrBreached (default), atRisk or breached.")] string? filter = null,
        [Description("Only this inbox.")] string? inboxId = null,
        [Description("Page size, 1-100 (default 25).")] int limit = SlaRiskQuery.DefaultLimit,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var query = new SlaRiskQuery(filter is null ? SlaRiskFilter.AtRiskOrBreached : TicketFlowToolJson.Parse<SlaRiskFilter>(filter, nameof(filter)),
                inboxId is null ? null : new InboxId(inboxId), limit);
            return Map(await context.Service<ISlaService>().ListAsync(principal, query, cursor, cancellationToken),
                page => new { items = page.Items.Select(Status).ToList(), next = page.NextCursor });
        }, cancellationToken);

    [McpServerTool(Name = "sla_list_policies", Title = "List SLA policies", ReadOnly = true, Idempotent = true)]
    [Description("Lists SLA policies as their versioned definition JSON.")]
    [TicketFlowTool("sla:read", Service = typeof(ISlaPolicyService))]
    public Task<CallToolResult> ListPolicies(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ISlaPolicyService>().ListAsync(principal, cancellationToken), list => new
        {
            items = list.Select(stored => new { id = stored.Policy.Id.Value, revision = stored.Revision, definition = JsonDocument.Parse(SlaPolicyCodec.Serialize(stored.Policy)).RootElement.Clone() }).ToList(),
        }), cancellationToken);

    private static object Status(SlaStatusView view) => new
    {
        conversationId = view.Conversation.Value,
        inboxId = view.Inbox.Value,
        policyId = view.Policy?.Value,
        ruleName = view.RuleName,
        basis = TicketFlowToolJson.Token(view.Basis),
        paused = view.Paused,
        atRisk = view.AtRisk,
        breached = view.Breached,
        dueAt = view.DueAt,
        evaluatedAt = view.EvaluatedAt,
        clocks = view.Clocks.Select(clock => new
        {
            metric = TicketFlowToolJson.Token(clock.Metric),
            cycle = clock.Cycle,
            state = TicketFlowToolJson.Token(clock.State),
            outcome = TicketFlowToolJson.Token(clock.Outcome),
            targetSeconds = (long)Math.Floor(clock.Target.TotalSeconds),
            elapsedSeconds = (long)Math.Floor(clock.Elapsed.TotalSeconds),
            remainingSeconds = (long)Math.Floor(clock.Remaining.TotalSeconds),
            dueAt = clock.DueAt,
            breachedAt = clock.BreachedAt,
            completedAt = clock.CompletedAt,
            atRisk = clock.AtRisk,
            breached = clock.Breached,
        }).ToList(),
    };

    private static object Map<T>(SlaResult<T> result, Func<T, object> success) => result switch
    {
        SlaValue<T> value => success(value.Value),
        SlaNotFound => TicketFlowToolError.NotFound(),
        SlaConflict conflict => TicketFlowToolError.Conflict(conflict.CurrentRevision),
        SlaRefused refused => refused.Reason switch
        {
            SlaRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            SlaRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
    };
}
