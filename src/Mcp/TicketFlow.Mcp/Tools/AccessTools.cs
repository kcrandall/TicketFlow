using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Access;
using TicketFlow.Core;
using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>
/// Access administration tools (sensitive), all through <see cref="IAccessAdministrator"/>: listings require <c>access:manage</c>;
/// changes enforce the no-escalation rules and are audited.
/// </summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Access)]
public sealed class AccessTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    private IAccessAdministrator Administrator => context.Service<IAccessAdministrator>();

    [McpServerTool(Name = "access_list_roles", Title = "List roles", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<RoleOutput>))]
    [Description("Lists the built-in roles and the workspace's custom roles with their permissions.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> ListRoles(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.ListRolesAsync(principal, cancellationToken),
            roles => new ToolList<RoleOutput>([.. roles.Select(listing => Role(listing.Role, listing.Revision))])), cancellationToken);

    [McpServerTool(Name = "access_list_grants", Title = "List grants", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<GrantOutput>))]
    [Description("Lists grants whose scope is exactly a workspace, team or inbox target.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> ListGrants(
        [Description("Target kind: workspace (default), team or inbox.")] string? scope = null,
        [Description("Team or inbox id for team/inbox targets.")] string? targetId = null,
        [Description("Page size, 1-200 (default 100).")] int limit = 100,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var size = Math.Clamp(limit, 1, 200);
            var after = cursor is null ? (GrantId?)null : new GrantId(TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            return Map(await Administrator.ListGrantsAsync(principal, Target(principal.Scope, scope ?? "workspace", targetId), size + 1, after, cancellationToken), page =>
            {
                var items = page.Take(size).ToList();
                return new ToolPage<GrantOutput>([.. items.Select(Grant)], page.Count > size ? TicketFlowToolJson.EncodeCursor(items[^1].Grant.Id.Value) : null);
            });
        }, cancellationToken);

    [McpServerTool(Name = "access_list_field_policies", Title = "List field policies", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<FieldPolicyOutput>))]
    [Description("Lists the workspace's field masking policies (which permissions they apply to, which roles see values unmasked, the reveal permission).")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> ListFieldPolicies(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.ListFieldPoliciesAsync(principal, cancellationToken), policies => new ToolList<FieldPolicyOutput>(
            [.. policies.Select(stored => new FieldPolicyOutput(stored.Policy.Field.Value,
                [.. stored.Policy.AppliesTo.Select(permission => permission.Value).Order(StringComparer.Ordinal)],
                [.. stored.Policy.UnmaskedRoles.Select(role => role.Value).Order(StringComparer.Ordinal)], stored.Policy.RevealPermission?.Value, stored.Revision))])),
            cancellationToken);

    [McpServerTool(Name = "access_create_grant", Title = "Create grant", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(GrantOutput))]
    [Description("Grants a role to a user, team or API client on the workspace, a team or an inbox (allow or deny, optional expiry). Access refuses grants that would escalate beyond the caller's own permissions; every call is audited.")]
    [TicketFlowTool(P.AccessManage)]
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
            var grant = new AccessGrant(new GrantId(Guid.CreateVersion7().ToString("N")), AccessPartition.From(principal.Scope), subject, new RoleId(role),
                Target(principal.Scope, scope ?? "workspace", targetId), effect is null ? GrantEffect.Allow : TicketFlowToolJson.Parse<GrantEffect>(effect, nameof(effect)),
                validUntil: validUntil, justification: justification);
            return Map(await Administrator.CreateGrantAsync(principal, grant, cancellationToken), Grant);
        }, cancellationToken);

    [McpServerTool(Name = "access_revoke_grant", Title = "Revoke grant", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(GrantOutput))]
    [Description("Revokes a grant (fenced on its revision); audited.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> RevokeGrant(
        [Description("Grant id.")] string grantId,
        [Description("Current grant revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.RevokeGrantAsync(principal, new GrantId(grantId), expectedRevision, cancellationToken), Grant),
            cancellationToken);

    [McpServerTool(Name = "access_create_role", Title = "Create custom role", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(RoleOutput))]
    [Description("Creates a custom role with the given permissions. Access enforces no-escalation rules and audits every call.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> CreateRole(
        [Description("Custom role id.")] string roleId,
        [Description("Display name.")] string name,
        [Description("Permission strings granted by the role.")] string[] permissions,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var role = new AccessRole(new RoleId(roleId), name, (permissions ?? throw new ArgumentNullException(nameof(permissions))).Select(p => new Permission(p)));
            return Map(await Administrator.CreateRoleAsync(principal, role, cancellationToken), stored => Role(stored.Role, stored.Revision));
        }, cancellationToken);

    [McpServerTool(Name = "access_update_role", Title = "Update custom role", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(RoleOutput))]
    [Description("Updates a custom role's name and permissions (fenced on expectedRevision); audited.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> UpdateRole(
        [Description("Custom role id.")] string roleId,
        [Description("Current role revision.")] long expectedRevision,
        [Description("Display name.")] string name,
        [Description("Permission strings granted by the role.")] string[] permissions,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var role = new AccessRole(new RoleId(roleId), name, (permissions ?? throw new ArgumentNullException(nameof(permissions))).Select(p => new Permission(p)));
            return Map(await Administrator.UpdateRoleAsync(principal, role, expectedRevision, cancellationToken), stored => Role(stored.Role, stored.Revision));
        }, cancellationToken);

    [McpServerTool(Name = "access_delete_role", Title = "Delete custom role", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(RoleOutput))]
    [Description("Deletes a custom role (fenced on expectedRevision); audited.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> DeleteRole(
        [Description("Custom role id.")] string roleId,
        [Description("Current role revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.DeleteRoleAsync(principal, new RoleId(roleId), expectedRevision, cancellationToken),
            stored => Role(stored.Role, stored.Revision)), cancellationToken);

    [McpServerTool(Name = "access_create_field_policy", Title = "Create field masking policy", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(FieldPolicyOutput))]
    [Description("Creates a field masking policy (which permissions mask the field, which roles see it unmasked, optional reveal permission); audited.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> CreateFieldPolicy(
        [Description("Field path (for example contact.email or conversation.attribute.ssn).")] string field,
        [Description("Permissions to which masking applies.")] string[] appliesTo,
        [Description("Optional role ids that see the field unmasked.")] string[]? unmaskedRoles = null,
        [Description("Optional reveal permission.")] string? revealPermission = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var policy = new AccessFieldPolicy(new FieldPath(field),
                (appliesTo ?? throw new ArgumentNullException(nameof(appliesTo))).Select(p => new Permission(p)),
                unmaskedRoles?.Select(r => new RoleId(r)),
                revealPermission is null ? null : new Permission(revealPermission));
            return Map(await Administrator.CreateFieldPolicyAsync(principal, policy, cancellationToken), FieldPolicy);
        }, cancellationToken);

    [McpServerTool(Name = "access_update_field_policy", Title = "Update field masking policy", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(FieldPolicyOutput))]
    [Description("Updates a field masking policy (fenced on expectedRevision); audited.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> UpdateFieldPolicy(
        [Description("Field path.")] string field,
        [Description("Current policy revision.")] long expectedRevision,
        [Description("Permissions to which masking applies.")] string[] appliesTo,
        [Description("Optional role ids that see the field unmasked.")] string[]? unmaskedRoles = null,
        [Description("Optional reveal permission.")] string? revealPermission = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var policy = new AccessFieldPolicy(new FieldPath(field),
                (appliesTo ?? throw new ArgumentNullException(nameof(appliesTo))).Select(p => new Permission(p)),
                unmaskedRoles?.Select(r => new RoleId(r)),
                revealPermission is null ? null : new Permission(revealPermission));
            return Map(await Administrator.UpdateFieldPolicyAsync(principal, policy, expectedRevision, cancellationToken), FieldPolicy);
        }, cancellationToken);

    [McpServerTool(Name = "access_delete_field_policy", Title = "Delete field masking policy", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(FieldPolicyOutput))]
    [Description("Deletes a field masking policy (fenced on expectedRevision); audited.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> DeleteFieldPolicy(
        [Description("Field path.")] string field,
        [Description("Current policy revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.DeleteFieldPolicyAsync(principal, new FieldPath(field), expectedRevision, cancellationToken),
            FieldPolicy), cancellationToken);

    private static FieldPolicyOutput FieldPolicy(StoredAccessFieldPolicy stored) => new(
        stored.Policy.Field.Value,
        [.. stored.Policy.AppliesTo.Select(permission => permission.Value).Order(StringComparer.Ordinal)],
        [.. stored.Policy.UnmaskedRoles.Select(role => role.Value).Order(StringComparer.Ordinal)],
        stored.Policy.RevealPermission?.Value,
        stored.Revision);

    private static GrantScope Target(TicketScope scope, string kind, string? id) => kind switch
    {
        "workspace" => new WorkspaceGrantScope(scope.WorkspaceId),
        "team" => new TeamGrantScope(scope.WorkspaceId, new TeamId(id ?? throw new ArgumentException("'targetId' is required for team targets.", nameof(id)))),
        "inbox" => new InboxGrantScope(scope.WorkspaceId, new InboxId(id ?? throw new ArgumentException("'targetId' is required for inbox targets.", nameof(id)))),
        _ => throw new ArgumentException("Target kinds are workspace, team or inbox.", nameof(kind)),
    };

    private static RoleOutput Role(AccessRole role, long? revision) => new(role.Id.Value, role.Name, role.IsBuiltIn, role.IncludesAllPermissions,
        [.. role.Permissions.Select(permission => permission.Value).Order(StringComparer.Ordinal)], revision);

    private static GrantOutput Grant(StoredAccessGrant stored)
    {
        var grant = stored.Grant;
        return new GrantOutput(
            grant.Id.Value,
            grant.Subject switch
            {
                UserId user => new GrantSubjectOutput("user", user.Value),
                TeamId team => new GrantSubjectOutput("team", team.Value),
                ApiClientId client => new GrantSubjectOutput("apiClient", client.Value),
            },
            grant.Role.Value,
            grant.Scope switch
            {
                TenantGrantScope => new GrantScopeOutput("tenant", null),
                WorkspaceGrantScope => new GrantScopeOutput("workspace", null),
                TeamGrantScope team => new GrantScopeOutput("team", team.Team.Value),
                InboxGrantScope inbox => new GrantScopeOutput("inbox", inbox.Inbox.Value),
            },
            TicketFlowToolJson.Token(grant.Effect),
            grant.Condition is not null,
            grant.ValidFrom,
            grant.ValidUntil,
            TicketFlowToolJson.Token(grant.Kind),
            stored.Revision);
    }

    private static TicketFlowToolError Refused(AccessChangeRefused refused) => refused.Reason switch
    {
        AccessChangeRefusal.Forbidden => TicketFlowToolError.Forbidden() with { Errors = refused.Errors.Count > 0 ? refused.Errors : null },
        AccessChangeRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
        AccessChangeRefusal.NotFound => TicketFlowToolError.NotFound("The grant"),
        AccessChangeRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
        _ => TicketFlowToolError.Rejected(refused.Errors),
    };

    private static object Map<T>(AccessChangeResult<T> result, Func<T, object> success) => result switch
    {
        AccessChangeApplied<T> applied => success(applied.Value),
        AccessChangeRefused refused => Refused(refused),
    };

    private static object Map<T>(AccessListResult<T> result, Func<T, object> success) => result switch
    {
        AccessListed<T> listed => success(listed.Value),
        AccessChangeRefused refused => Refused(refused),
    };
}
