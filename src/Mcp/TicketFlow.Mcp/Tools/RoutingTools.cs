using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Core;
using TicketFlow.Routing;

namespace TicketFlow.Mcp.Tools;

/// <summary>Routing tools: unassigned queues and agent availability.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Routing)]
public sealed class RoutingTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "routing_queue", Title = "List queue", ReadOnly = true, Idempotent = true)]
    [Description("Lists unassigned, queued conversations the caller may read, longest waiting first, with overflow progress and whether the maximum wait was exceeded.")]
    [TicketFlowTool("conversation:read")]
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
            return Map(await context.Service<IRoutingQueueService>().ListAsync(principal, query, cancellationToken), page => new
            {
                items = page.Items.Select(item => new
                {
                    conversationId = item.Conversation.Value,
                    inboxId = item.Inbox.Value,
                    targetInboxId = item.TargetInbox.Value,
                    teamId = item.Team?.Value,
                    waitingSince = item.WaitingSince,
                    queuedAt = item.QueuedAt,
                    overflowStage = item.OverflowStage,
                    waitExceeded = item.WaitExceeded,
                }).ToList(),
                next = page.Next is { } next ? TicketFlowToolJson.EncodeCursor(TicketFlowToolJson.Instant(next.WaitingSince), next.Conversation.Value) : null,
            });
        }, cancellationToken);

    [McpServerTool(Name = "routing_list_agents", Title = "List agents", ReadOnly = true, Idempotent = true)]
    [Description("Lists agent routing profiles: effective availability status, capacity and skills.")]
    [TicketFlowTool("team:read", Service = typeof(IAgentAvailabilityService))]
    public Task<CallToolResult> ListAgents(
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var after = cursor is null ? (UserId?)null : new UserId(TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            return Map(await context.Service<IAgentAvailabilityService>().ListAsync(principal, after, limit, cancellationToken), profiles => new
            {
                items = profiles.Select(Profile).ToList(),
                next = profiles.Count >= limit && profiles.Count > 0 ? TicketFlowToolJson.EncodeCursor(profiles[^1].Agent.Value) : null,
            });
        }, cancellationToken);

    [McpServerTool(Name = "routing_get_agent", Title = "Get agent", ReadOnly = true, Idempotent = true)]
    [Description("Reads one agent's routing profile ('me' for the calling user).")]
    [TicketFlowTool("team:read", Service = typeof(IAgentAvailabilityService))]
    public Task<CallToolResult> GetAgent(
        [Description("Agent user id or 'me'.")] string agentId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAgentAvailabilityService>().GetAsync(principal, Agent(principal, agentId), cancellationToken), Profile),
            cancellationToken);

    [McpServerTool(Name = "routing_set_agent_status", Title = "Set agent status", Destructive = false, Idempotent = true)]
    [Description("Sets an agent's availability: online, busy, away or offline. Users set their own status with agent-status:update ('me'); changing another agent needs agent-status:manage.")]
    [TicketFlowTool("agent-status:update", "agent-status:manage", AnyOf = true, Service = typeof(IAgentAvailabilityService))]
    public Task<CallToolResult> SetAgentStatus(
        [Description("Agent user id or 'me'.")] string agentId,
        [Description("online, busy, away or offline.")] string status,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAgentAvailabilityService>().SetStatusAsync(principal, Agent(principal, agentId),
            TicketFlowToolJson.Parse<AgentStatus>(status, nameof(status)), cancellationToken), Profile), cancellationToken);

    private static UserId Agent(PrincipalRef principal, string agentId) => agentId == "me"
        ? principal.Value is UserPrincipal user ? user.UserId : throw new ArgumentException("'me' requires a user principal.", nameof(agentId))
        : new UserId(agentId);

    private object Profile(StoredAgentProfile profile)
    {
        var now = (context.Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        return new
        {
            userId = profile.Agent.Value,
            status = TicketFlowToolJson.Token(profile.Presence.EffectiveStatus(now)),
            statusChangedAt = profile.Presence.ChangedAt,
            presenceExpiresAt = profile.Presence.ExpiresAt,
            defaultCapacity = profile.Settings.DefaultCapacity,
            capacities = profile.Settings.Capacities.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
            skills = profile.Settings.Skills.Select(skill => new { name = skill.Name, level = skill.Level }).ToList(),
            reassignmentPending = profile.ReassignmentPending,
            revision = profile.Revision,
        };
    }

    internal static object Map<T>(RoutingResult<T> result, Func<T, object> success) => result switch
    {
        RoutingValue<T> value => success(value.Value),
        RoutingNotFound => TicketFlowToolError.NotFound(),
        RoutingConflict conflict => TicketFlowToolError.Conflict(conflict.CurrentRevision),
        RoutingRefused refused => refused.Reason switch
        {
            RoutingRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            RoutingRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
    };
}
