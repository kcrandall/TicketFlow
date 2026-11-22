using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// Prevents privilege escalation through team membership: adding a member to a team confers every allow grant whose subject is
/// the team, so the actor must hold every permission of each such grant's role on the grant's target — the same rule
/// <see cref="IAccessAdministrator.CreateGrantAsync"/> applies to new grants. Tenant-scoped team grants are checked at workspace
/// level. Expired grants are ignored; future grants are checked.
/// </summary>
internal sealed class TeamGrantGuard(IAccessGrantStore grants, IAccessRoleStore roles, IPermissionCatalog catalog, IAccessEvaluator evaluator, TimeProvider time)
{
    public async Task<WorkspaceRefused?> CheckAsync(PrincipalRef actor, TeamId team, CancellationToken cancellationToken)
    {
        var scope = actor.Scope;
        var partition = AccessPartition.From(scope);
        var now = time.GetUtcNow();
        var stored = await grants.ListForSubjectsAsync(partition, scope.WorkspaceId, [team], cancellationToken).ConfigureAwait(false);
        var checkedPairs = new HashSet<(Permission, AccessResource)>();
        foreach (var entry in stored)
        {
            var grant = entry.Grant;
            if (grant.Partition != partition || grant.Subject.Value is not TeamId subject || subject != team || (grant.Workspace is { } workspace && workspace != scope.WorkspaceId))
                throw new InvalidOperationException("The grant store returned a grant outside the requested partition, workspace or subject.");
            if (grant.Effect != GrantEffect.Allow || grant.ValidUntil is { } until && until <= now)
                continue;
            var role = BuiltInRoles.Find(grant.Role) ?? (await roles.GetAsync(scope, grant.Role, cancellationToken).ConfigureAwait(false))?.Role;
            if (role is null)
                continue;
            var target = grant.Scope switch
            {
                TenantGrantScope => WorkspaceResources.Workspace(scope),
                WorkspaceGrantScope => WorkspaceResources.Workspace(scope),
                TeamGrantScope teamScope => new AccessResource(scope, team: teamScope.Team),
                InboxGrantScope inboxScope => new AccessResource(scope, inbox: inboxScope.Inbox),
            };
            var permissions = role.IncludesAllPermissions ? catalog.Definitions.Select(definition => definition.Permission) : role.Permissions;
            foreach (var permission in permissions)
            {
                if (!checkedPairs.Add((permission, target)))
                    continue;
                var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, target), cancellationToken).ConfigureAwait(false);
                if (!decision.IsPermitted)
                    return new WorkspaceRefused(WorkspaceRefusal.Forbidden, ["Adding members to this team would confer permissions the actor does not hold."]);
            }
        }
        return null;
    }
}
