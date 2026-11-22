using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// The access snapshots workspace records are authorized against. Point checks and list filters both use these, so a store listing
/// with an <see cref="AccessFilter"/> returns exactly the records whose point check is permitted. Inbox and team records carry no
/// labels (unknown) and no attributes (unknown), so label/attribute conditions fail closed.
/// </summary>
public static class WorkspaceResources
{
    /// <summary>Workspace-level resource: only tenant and workspace grants apply.</summary>
    public static AccessResource Workspace(TicketScope scope) => new(scope);

    /// <summary>An inbox: workspace grants and inbox grants naming <paramref name="inbox"/> apply.</summary>
    public static AccessResource Inbox(TicketScope scope, InboxId inbox) => new(scope, inbox.Value, inbox: inbox);

    /// <summary>A team: workspace grants and team grants naming <paramref name="team"/> apply.</summary>
    public static AccessResource Team(TicketScope scope, TeamId team) => new(scope, team.Value, team: team);
}

/// <summary>Permissions used by workspace packages. All are built into the Access catalog so built-in roles can hold them.</summary>
public static class WorkspacePermissions
{
    public static Permission WorkspaceRead => AccessPermissions.WorkspaceRead;
    public static Permission WorkspaceManage => AccessPermissions.WorkspaceManage;
    public static Permission InboxRead => AccessPermissions.InboxRead;
    public static Permission InboxManage => AccessPermissions.InboxManage;
    public static Permission TeamRead => AccessPermissions.TeamRead;
    public static Permission TeamManage => AccessPermissions.TeamManage;
    public static Permission CalendarManage => AccessPermissions.CalendarManage;

    /// <summary>The catalog definitions of the permissions above, ordered by permission.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        .. AccessPermissions.Definitions.Where(definition => definition.Permission.Resource is "workspace" or "inbox" or "team" or "calendar"),
    ];
}
