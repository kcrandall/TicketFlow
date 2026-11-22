using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Routing;

/// <summary>
/// Permissions used by routing. <c>routing:manage</c>, <c>agent-status:update</c> and <c>agent-status:manage</c> are Access built-ins
/// (Agent holds <c>agent-status:update</c>; Supervisor also holds the other two; Admin and Owner hold all three). Queue reads reuse
/// <c>conversation:read</c>, presence reuses <c>conversation:read</c>, reply locks reuse <c>conversation:reply</c>, the agent roster reuses
/// <c>team:read</c> and policy reads reuse <c>inbox:read</c>.
/// </summary>
public static class RoutingPermissions
{
    /// <summary>Create, update and delete routing policies.</summary>
    public static Permission Manage => AccessPermissions.RoutingManage;

    /// <summary>Set one's own status and send presence heartbeats.</summary>
    public static Permission StatusUpdate => AccessPermissions.AgentStatusUpdate;

    /// <summary>Set other agents' status and configure any agent's capacity and skills.</summary>
    public static Permission StatusManage => AccessPermissions.AgentStatusManage;

    /// <summary>Read routing policies.</summary>
    public static Permission PolicyRead => AccessPermissions.InboxRead;

    /// <summary>Read agent availability profiles.</summary>
    public static Permission RosterRead => AccessPermissions.TeamRead;

    /// <summary>Read unassigned queues (Access-filtered per conversation).</summary>
    public static Permission QueueRead => AccessPermissions.ConversationRead;

    /// <summary>Report and read conversation presence.</summary>
    public static Permission PresenceReport => AccessPermissions.ConversationRead;

    /// <summary>Acquire and release soft reply locks.</summary>
    public static Permission ReplyLock => AccessPermissions.ConversationReply;

    /// <summary>Definitions identical to the Access built-ins, registered by <c>AddRouting()</c> (idempotent).</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
        [.. AccessPermissions.Definitions.Where(definition => definition.Permission == Manage || definition.Permission == StatusUpdate || definition.Permission == StatusManage)];
}

/// <summary>Access resources for routing records (workspace-level, so team- and inbox-scoped grants do not apply).</summary>
public static class RoutingResources
{
    public static AccessResource Workspace(TicketScope scope) => new(scope);

    public static AccessResource Agent(TicketScope scope, UserId agent) => new(scope, agent.Value);

    public static AccessResource Policy(TicketScope scope, Workspaces.RoutingPolicyId policy) => new(scope, policy.Value);
}
