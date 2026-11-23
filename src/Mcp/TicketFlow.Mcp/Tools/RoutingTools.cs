using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Core;
using TicketFlow.Routing;
using TicketFlow.Workspaces;

using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>Routing tools: unassigned queues and agent availability.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Routing)]
public sealed class RoutingTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "routing_queue", Title = "List queue", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<QueueItemOutput>))]
    [Description("Lists unassigned, queued conversations the caller may read, longest waiting first, with overflow progress and whether the maximum wait was exceeded.")]
    [TicketFlowTool(P.ConversationRead)]
    public Task<CallToolResult> Queue(
        [Description("Only this inbox.")] string? inboxId = null,
        [Description("Only this team.")] string? teamId = null,
        [Description("Page size, 1-100 (default 25).")] int limit = 25,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            RoutingQueueCursor? after = null;
            if (cursor is not null)
            {
                var parts = TicketFlowToolJson.DecodeCursor(cursor, 2);
                after = new RoutingQueueCursor(TicketFlowToolJson.ParseInstant(parts[0]), new ConversationId(parts[1]));
            }
            var query = new RoutingQueueQuery(inboxId is null ? null : new InboxId(inboxId), teamId is null ? null : new TeamId(teamId), limit, after);
            return Map(await context.Service<IRoutingQueueService>().ListAsync(principal, query, cancellationToken), page => new ToolPage<QueueItemOutput>(
                [.. page.Items.Select(item => new QueueItemOutput(item.Conversation.Value, item.Inbox.Value, item.TargetInbox.Value, item.Team?.Value, item.WaitingSince,
                    item.QueuedAt, item.OverflowStage, item.WaitExceeded))],
                page.Next is { } next ? TicketFlowToolJson.EncodeCursor(TicketFlowToolJson.Instant(next.WaitingSince), next.Conversation.Value) : null));
        }, cancellationToken);

    [McpServerTool(Name = "routing_list_agents", Title = "List agents", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<AgentProfileOutput>))]
    [Description("Lists agent routing profiles: effective availability status, capacity and skills.")]
    [TicketFlowTool(P.TeamRead, Service = typeof(IAgentAvailabilityService))]
    public Task<CallToolResult> ListAgents(
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var after = cursor is null ? (UserId?)null : new UserId(TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            var now = Now();
            return Map(await context.Service<IAgentAvailabilityService>().ListAsync(principal, after, limit, cancellationToken), profiles => new ToolPage<AgentProfileOutput>(
                [.. profiles.Select(profile => Profile(profile, now))], profiles.Count >= limit && profiles.Count > 0 ? TicketFlowToolJson.EncodeCursor(profiles[^1].Agent.Value) : null));
        }, cancellationToken);

    [McpServerTool(Name = "routing_get_agent", Title = "Get agent", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentProfileOutput))]
    [Description("Reads one agent's routing profile ('me' for the calling user).")]
    [TicketFlowTool(P.TeamRead, Service = typeof(IAgentAvailabilityService))]
    public Task<CallToolResult> GetAgent(
        [Description("Agent user id or 'me'.")] string agentId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAgentAvailabilityService>().GetAsync(principal, Agent(principal, agentId), cancellationToken), Profile),
            cancellationToken);

    [McpServerTool(Name = "routing_set_agent_status", Title = "Set agent status", Destructive = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentProfileOutput))]
    [Description("Sets an agent's availability: online, busy, away or offline. Users set their own status with agent-status:update ('me'); changing another agent needs agent-status:manage.")]
    [TicketFlowTool(P.AgentStatusUpdate, P.AgentStatusManage, AnyOf = true, Service = typeof(IAgentAvailabilityService))]
    public Task<CallToolResult> SetAgentStatus(
        [Description("Agent user id or 'me'.")] string agentId,
        [Description("online, busy, away or offline.")] string status,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAgentAvailabilityService>().SetStatusAsync(principal, Agent(principal, agentId),
            TicketFlowToolJson.Parse<AgentStatus>(status, nameof(status)), cancellationToken), Profile), cancellationToken);

    [McpServerTool(Name = "routing_list_policies", Title = "List routing policies", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<DefinitionOutput>))]
    [Description("Lists routing policies as their versioned v1 definition JSON.")]
    [TicketFlowTool(P.InboxRead, Service = typeof(IRoutingPolicyService))]
    public Task<CallToolResult> ListPolicies(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IRoutingPolicyService>().ListAsync(principal, cancellationToken),
            list => new ToolList<DefinitionOutput>([.. list.Select(PolicyOutput)])), cancellationToken);

    [McpServerTool(Name = "routing_create_policy", Title = "Create routing policy", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(DefinitionOutput))]
    [Description("Creates a routing policy from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.RoutingManage, Service = typeof(IRoutingPolicyService))]
    public Task<CallToolResult> CreatePolicy(
        [Description("Routing policy id.")] string policyId,
        [Description("Versioned routing policy definition JSON (same format as routing_list_policies).")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IRoutingPolicyService>().CreateAsync(principal,
            RoutingPolicyCodec.Deserialize(new RoutingPolicyId(policyId), definitionJson), cancellationToken), PolicyOutput), cancellationToken);

    [McpServerTool(Name = "routing_update_policy", Title = "Update routing policy", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(DefinitionOutput))]
    [Description("Replaces a routing policy (fenced on expectedRevision) from its versioned v1 definition JSON.")]
    [TicketFlowTool(P.RoutingManage, Service = typeof(IRoutingPolicyService))]
    public Task<CallToolResult> UpdatePolicy(
        [Description("Routing policy id.")] string policyId,
        [Description("Current routing policy revision.")] long expectedRevision,
        [Description("Versioned routing policy definition JSON.")] string definitionJson,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IRoutingPolicyService>().UpdateAsync(principal,
            RoutingPolicyCodec.Deserialize(new RoutingPolicyId(policyId), definitionJson), expectedRevision, cancellationToken), PolicyOutput), cancellationToken);

    [McpServerTool(Name = "routing_delete_policy", Title = "Delete routing policy", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(DefinitionOutput))]
    [Description("Deletes a routing policy (fenced on expectedRevision).")]
    [TicketFlowTool(P.RoutingManage, Service = typeof(IRoutingPolicyService))]
    public Task<CallToolResult> DeletePolicy(
        [Description("Routing policy id.")] string policyId,
        [Description("Current routing policy revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IRoutingPolicyService>().DeleteAsync(principal,
            new RoutingPolicyId(policyId), expectedRevision, cancellationToken), PolicyOutput), cancellationToken);

    private static DefinitionOutput PolicyOutput(StoredRoutingPolicy stored) =>
        new(stored.Policy.Id.Value, stored.Revision, TicketFlowToolJson.Parse(RoutingPolicyCodec.Serialize(stored.Policy)));

    private static UserId Agent(PrincipalRef principal, string agentId) => agentId == "me"
        ? principal is UserPrincipal user ? user.UserId : throw new ArgumentException("'me' requires a user principal.", nameof(agentId))
        : new UserId(agentId);

    private DateTimeOffset Now() => (context.Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    private AgentProfileOutput Profile(StoredAgentProfile profile) => Profile(profile, Now());

    private static AgentProfileOutput Profile(StoredAgentProfile profile, DateTimeOffset now) =>
        new(profile.Agent.Value, TicketFlowToolJson.Token(profile.Presence.EffectiveStatus(now)), profile.Presence.ChangedAt,
            profile.Presence.ExpiresAt, profile.Settings.DefaultCapacity,
            profile.Settings.Capacities.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
            [.. profile.Settings.Skills.Select(skill => new AgentSkillOutput(skill.Name, skill.Level))], profile.ReassignmentPending, profile.Revision);

    internal static object Map<T>(TicketResult<T> result, Func<T, object> success) => TicketFlowToolError.Map(result, success);
}
