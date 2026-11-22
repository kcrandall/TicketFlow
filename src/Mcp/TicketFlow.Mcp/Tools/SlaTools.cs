using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>SLA tools: conversation SLA status, the at-risk/breached list and policies.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Sla)]
public sealed class SlaTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "sla_get_status", Title = "Get SLA status", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(SlaStatusOutput))]
    [Description("Returns a conversation's SLA clocks (first response, next response, resolution): elapsed and remaining seconds, predicted breach time, at-risk and breached flags.")]
    [TicketFlowTool(P.SlaRead)]
    public Task<CallToolResult> GetStatus(
        [Description("Conversation id.")] string conversationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ISlaService>().GetStatusAsync(principal, new ConversationId(conversationId), cancellationToken), Status),
            cancellationToken);

    [McpServerTool(Name = "sla_risk_list", Title = "List SLA risks", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<SlaStatusOutput>))]
    [Description("Lists conversations whose SLA is at risk and/or breached, soonest due first.")]
    [TicketFlowTool(P.SlaRead)]
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
                page => new ToolPage<SlaStatusOutput>([.. page.Items.Select(Status)], page.NextCursor));
        }, cancellationToken);

    [McpServerTool(Name = "sla_list_policies", Title = "List SLA policies", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<DefinitionOutput>))]
    [Description("Lists SLA policies as their versioned definition JSON.")]
    [TicketFlowTool(P.SlaRead, Service = typeof(ISlaPolicyService))]
    public Task<CallToolResult> ListPolicies(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ISlaPolicyService>().ListAsync(principal, cancellationToken), list => new ToolList<DefinitionOutput>(
            [.. list.Select(PolicyOutput)])),
            cancellationToken);

    [McpServerTool(Name = "sla_create_policy", Title = "Create SLA policy", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(DefinitionOutput))]
    [Description("Creates an SLA policy from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.SlaManage, Service = typeof(ISlaPolicyService))]
    public Task<CallToolResult> CreatePolicy(
        [Description("SLA policy id.")] string policyId,
        [Description("Versioned SLA policy definition JSON (same format as sla_list_policies).")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ISlaPolicyService>().CreateAsync(principal,
            SlaPolicyCodec.Deserialize(new SlaPolicyId(policyId), definitionJson), cancellationToken), PolicyOutput), cancellationToken);

    [McpServerTool(Name = "sla_update_policy", Title = "Update SLA policy", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(DefinitionOutput))]
    [Description("Replaces an SLA policy (fenced on expectedRevision) from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.SlaManage, Service = typeof(ISlaPolicyService))]
    public Task<CallToolResult> UpdatePolicy(
        [Description("SLA policy id.")] string policyId,
        [Description("Current SLA policy revision.")] long expectedRevision,
        [Description("Versioned SLA policy definition JSON.")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ISlaPolicyService>().UpdateAsync(principal,
            SlaPolicyCodec.Deserialize(new SlaPolicyId(policyId), definitionJson), expectedRevision, cancellationToken), PolicyOutput), cancellationToken);

    [McpServerTool(Name = "sla_delete_policy", Title = "Delete SLA policy", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(DefinitionOutput))]
    [Description("Deletes an SLA policy (fenced on expectedRevision).")]
    [TicketFlowTool(P.SlaManage, Service = typeof(ISlaPolicyService))]
    public Task<CallToolResult> DeletePolicy(
        [Description("SLA policy id.")] string policyId,
        [Description("Current SLA policy revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ISlaPolicyService>().DeleteAsync(principal,
            new SlaPolicyId(policyId), expectedRevision, cancellationToken), PolicyOutput), cancellationToken);

    private static DefinitionOutput PolicyOutput(StoredSlaPolicy stored) =>
        new(stored.Policy.Id.Value, stored.Revision, TicketFlowToolJson.Parse(SlaPolicyCodec.Serialize(stored.Policy)));

    private static SlaStatusOutput Status(SlaStatusView view) => new(view.Conversation.Value, view.Inbox.Value, view.Policy?.Value, view.RuleName,
        TicketFlowToolJson.Token(view.Basis), view.Paused, view.AtRisk, view.Breached, view.DueAt, view.EvaluatedAt,
        [.. view.Clocks.Select(clock => new SlaClockOutput(TicketFlowToolJson.Token(clock.Metric), clock.Cycle, TicketFlowToolJson.Token(clock.State),
            TicketFlowToolJson.Token(clock.Outcome), Seconds(clock.Target), Seconds(clock.Elapsed), Seconds(clock.Remaining), clock.DueAt, clock.BreachedAt, clock.CompletedAt,
            clock.AtRisk, clock.Breached))]);

    private static long Seconds(TimeSpan value) => (long)Math.Floor(value.TotalSeconds);

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
