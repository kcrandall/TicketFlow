using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp.Tools;

/// <summary>
/// Access administration tools (sensitive). Listings are guarded by an explicit <c>access:manage</c> point check (like the HTTP
/// endpoints); changes go through <see cref="IAccessAdministrator"/>, which enforces no-escalation rules and audits every call.
/// </summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Access)]
public sealed class AccessTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "access_list_roles", Title = "List roles", ReadOnly = true, Idempotent = true)]
    [Description("Lists the built-in roles and the workspace's custom roles with their permissions.")]
    [TicketFlowTool("access:manage", Service = typeof(IAccessRoleStore))]
    public Task<CallToolResult> ListRoles(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            if (!await CanManageAsync(principal, WorkspaceResources.Workspace(principal.Scope), cancellationToken))
                return TicketFlowToolError.Forbidden();
            var custom = await context.Service<IAccessRoleStore>().ListAsync(principal.Scope, cancellationToken);
            return new
            {
                items = BuiltInRoles.All.Select(role => Role(role, null)).Concat(custom.Select(stored => Role(stored.Role, stored.Revision))).ToList(),
            };
        }, cancellationToken);

    [McpServerTool(Name = "access_list_grants", Title = "List grants", ReadOnly = true, Idempotent = true)]
    [Description("Lists grants whose scope is exactly a workspace, team or inbox target.")]
    [TicketFlowTool("access:manage", Service = typeof(IAccessGrantStore))]
    public Task<CallToolResult> ListGrants(
        [Description("Target kind: workspace (default), team or inbox.")] string? scope = null,
        [Description("Team or inbox id for team/inbox targets.")] string? targetId = null,
        [Description("Page size, 1-200 (default 100).")] int limit = 100,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var (target, resource) = Target(principal.Scope, scope ?? "workspace", targetId);
            if (!await CanManageAsync(principal, resource, cancellationToken))
                return TicketFlowToolError.Forbidden();
            var size = Math.Clamp(limit, 1, 200);
            var after = cursor is null ? (GrantId?)null : new GrantId(TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            var page = await context.Service<IAccessGrantStore>().ListForTargetAsync(AccessPartition.From(principal.Scope), target, size + 1, after, cancellationToken);
            var items = page.Take(size).ToList();
            return new
            {
                items = items.Select(Grant).ToList(),
                next = page.Count > size ? TicketFlowToolJson.EncodeCursor(items[^1].Grant.Id.Value) : null,
            };
        }, cancellationToken);

    [McpServerTool(Name = "access_list_field_policies", Title = "List field policies", ReadOnly = true, Idempotent = true)]
    [Description("Lists the workspace's field masking policies (which permissions they apply to, which roles see values unmasked, the reveal permission).")]
    [TicketFlowTool("access:manage", Service = typeof(IAccessFieldPolicyStore))]
    public Task<CallToolResult> ListFieldPolicies(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            if (!await CanManageAsync(principal, WorkspaceResources.Workspace(principal.Scope), cancellationToken))
                return TicketFlowToolError.Forbidden();
            var policies = await context.Service<IAccessFieldPolicyStore>().ListAsync(principal.Scope, cancellationToken);
            return new
            {
                items = policies.Select(stored => new
                {
                    field = stored.Policy.Field.Value,
                    appliesTo = stored.Policy.AppliesTo.Select(permission => permission.Value).Order(StringComparer.Ordinal).ToList(),
                    unmaskedRoles = stored.Policy.UnmaskedRoles.Select(role => role.Value).Order(StringComparer.Ordinal).ToList(),
                    revealPermission = stored.Policy.RevealPermission?.Value,
                    revision = stored.Revision,
                }).ToList(),
            };
        }, cancellationToken);

    [McpServerTool(Name = "access_create_grant", Title = "Create grant", Destructive = true, Idempotent = false)]
    [Description("Grants a role to a user, team or API client on the workspace, a team or an inbox (allow or deny, optional expiry). Access refuses grants that would escalate beyond the caller's own permissions; every call is audited.")]
    [TicketFlowTool("access:manage")]
    public Task<CallToolResult> CreateGrant(
        [Description("Subject kind: user, team or apiClient.")] string subjectKind,
        [Description("Subject id.")] string subjectId,
        [Description("Role id (built-in: owner, admin, supervisor, agent, light-agent, viewer; or a custom role).")] string role,
        [Description("Target kind: workspace (default), team or inbox.")] string? scope = null,
        [Description("Team or inbox id for team/inbox targets.")] string? targetId = null,
        [Description("allow (default) or deny.")] string? effect = null,
        [Description("Optional expiry (ISO 8601).")] DateTimeOffset? validUntil = null,
        [Description("Justification recorded with the grant.")] string? justification = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            GrantSubject subject = subjectKind switch
            {
                "user" => new UserId(subjectId),
                "team" => new TeamId(subjectId),
                "apiClient" => new ApiClientId(subjectId),
                _ => throw new ArgumentException("Subject kinds are user, team or apiClient.", nameof(subjectKind)),
            };
            var (target, _) = Target(principal.Scope, scope ?? "workspace", targetId);
            var grant = new AccessGrant(new GrantId(Guid.CreateVersion7().ToString("N")), AccessPartition.From(principal.Scope), subject, new RoleId(role), target,
                effect is null ? GrantEffect.Allow : TicketFlowToolJson.Parse<GrantEffect>(effect, nameof(effect)), validUntil: validUntil, justification: justification);
            return Map(await context.Service<IAccessAdministrator>().CreateGrantAsync(principal, grant, cancellationToken), Grant);
        }, cancellationToken);

    [McpServerTool(Name = "access_revoke_grant", Title = "Revoke grant", Destructive = true, Idempotent = false)]
    [Description("Revokes a grant (fenced on its revision); audited.")]
    [TicketFlowTool("access:manage")]
    public Task<CallToolResult> RevokeGrant(
        [Description("Grant id.")] string grantId,
        [Description("Current grant revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IAccessAdministrator>().RevokeGrantAsync(principal, new GrantId(grantId), expectedRevision,
            cancellationToken), Grant), cancellationToken);

    private async Task<bool> CanManageAsync(PrincipalRef principal, AccessResource resource, CancellationToken cancellationToken) =>
        (await context.Service<IAccessEvaluator>().AuthorizeAsync(new AccessRequest(principal, AccessPermissions.AccessManage, resource), cancellationToken)).IsPermitted;

    private static (GrantScope Target, AccessResource Resource) Target(TicketScope scope, string kind, string? id) => kind switch
    {
        "workspace" => (new WorkspaceGrantScope(scope.WorkspaceId), WorkspaceResources.Workspace(scope)),
        "team" => (new TeamGrantScope(scope.WorkspaceId, new TeamId(id ?? throw new ArgumentException("'targetId' is required for team targets.", nameof(id)))),
            WorkspaceResources.Team(scope, new TeamId(id))),
        "inbox" => (new InboxGrantScope(scope.WorkspaceId, new InboxId(id ?? throw new ArgumentException("'targetId' is required for inbox targets.", nameof(id)))),
            WorkspaceResources.Inbox(scope, new InboxId(id))),
        _ => throw new ArgumentException("Target kinds are workspace, team or inbox.", nameof(kind)),
    };

    private static object Role(AccessRole role, long? revision) => new
    {
        id = role.Id.Value,
        name = role.Name,
        builtIn = role.IsBuiltIn,
        includesAllPermissions = role.IncludesAllPermissions,
        permissions = role.Permissions.Select(permission => permission.Value).Order(StringComparer.Ordinal).ToList(),
        revision,
    };

    private static object Grant(StoredAccessGrant stored)
    {
        var grant = stored.Grant;
        return new
        {
            id = grant.Id.Value,
            subject = grant.Subject switch
            {
                UserId user => new { kind = "user", id = user.Value },
                TeamId team => new { kind = "team", id = team.Value },
                ApiClientId client => new { kind = "apiClient", id = client.Value },
            },
            role = grant.Role.Value,
            scope = grant.Scope switch
            {
                TenantGrantScope => new { kind = "tenant", id = (string?)null },
                WorkspaceGrantScope => new { kind = "workspace", id = (string?)null },
                TeamGrantScope team => new { kind = "team", id = (string?)team.Team.Value },
                InboxGrantScope inbox => new { kind = "inbox", id = (string?)inbox.Inbox.Value },
            },
            effect = TicketFlowToolJson.Token(grant.Effect),
            conditional = grant.Condition is not null,
            validFrom = grant.ValidFrom,
            validUntil = grant.ValidUntil,
            kind = TicketFlowToolJson.Token(grant.Kind),
            revision = stored.Revision,
        };
    }

    private static object Map<T>(AccessChangeResult<T> result, Func<T, object> success) => result switch
    {
        AccessChangeApplied<T> applied => success(applied.Value),
        AccessChangeRefused refused => refused.Reason switch
        {
            AccessChangeRefusal.Forbidden => TicketFlowToolError.Forbidden() with { Errors = refused.Errors.Count > 0 ? refused.Errors : null },
            AccessChangeRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            AccessChangeRefusal.NotFound => TicketFlowToolError.NotFound("The grant"),
            AccessChangeRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
    };
}
