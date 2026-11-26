using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Access;
using TicketFlow.Core;
using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>
/// Access administration tools (sensitive), through <see cref="IAccessAdministrator"/>, <see cref="IServiceAccountService"/> and
/// <see cref="IRoleFederationService"/>: changes enforce no-escalation rules and are audited.
/// </summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Access)]
public sealed class AccessTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;
    private const string ApiKeyOnceWarning = "Store this API key securely now — it cannot be retrieved again.";

    private IAccessAdministrator Administrator => context.Service<IAccessAdministrator>();
    private IServiceAccountService ServiceAccounts => context.Service<IServiceAccountService>();
    private IRoleFederationService RoleFederation => context.Service<IRoleFederationService>();

    [McpServerTool(Name = "access_list_roles", Title = "List roles", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<RoleOutput>))]
    [Description("Lists the built-in roles and the workspace's custom roles with their permissions.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> ListRoles(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.ListRolesAsync(principal, cancellationToken),
            roles => new ToolList<RoleOutput>([.. roles.Select(listing => Role(listing.Role, listing.Revision))]), "The role"), cancellationToken);

    [McpServerTool(Name = "access_list_grants", Title = "List grants", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<GrantOutput>))]
    [Description("Lists grants whose scope is a workspace, team, inbox or tenant target. Tenant targets require access:manage-tenant and list grants for the named subject (or the caller's own subjects when omitted).")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true)]
    public Task<CallToolResult> ListGrants(
        [Description("Target kind: workspace (default), team, inbox or tenant.")] string? scope = null,
        [Description("Team or inbox id for team/inbox targets.")] string? targetId = null,
        [Description("Subject kind for tenant targets: user, team or apiClient (defaults to the caller's own subjects when omitted).")] string? subjectKind = null,
        [Description("Subject id for tenant targets (required when subjectKind is specified).")] string? subjectId = null,
        [Description("Page size, 1-200 (default 100).")] int limit = 100,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            if ((subjectKind is null) != (subjectId is null))
                throw new ArgumentException("Provide both 'subjectKind' and 'subjectId'.", nameof(subjectKind));
            var size = Math.Clamp(limit, 1, 200);
            var after = cursor is null ? (GrantId?)null : new GrantId(TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            var target = Target(principal.Scope, scope ?? "workspace", targetId);
            if (target is TenantGrantScope)
            {
                IReadOnlyCollection<GrantSubject> subjects = subjectKind is not null
                    ? [ParseSubject(subjectKind, subjectId!)]
                    : OwnSubjects(principal);
                return Map(await Administrator.ListTenantGrantsForSubjectsAsync(principal, subjects, cancellationToken), listed =>
                {
                    var filtered = after is { } afterId
                        ? listed.Where(item => string.CompareOrdinal(item.Grant.Id.Value, afterId.Value) > 0).ToList()
                        : listed;
                    var items = filtered.Take(size).ToList();
                    return new ToolPage<GrantOutput>([.. items.Select(Grant)], filtered.Count > size ? TicketFlowToolJson.EncodeCursor(items[^1].Grant.Id.Value) : null);
                }, "The grant");
            }
            if (subjectKind is not null)
                throw new ArgumentException("'subjectKind' and 'subjectId' are only supported when scope is 'tenant'.", nameof(subjectKind));
            return Map(await Administrator.ListGrantsAsync(principal, target, size + 1, after, cancellationToken), page =>
            {
                var items = page.Take(size).ToList();
                return new ToolPage<GrantOutput>([.. items.Select(Grant)], page.Count > size ? TicketFlowToolJson.EncodeCursor(items[^1].Grant.Id.Value) : null);
            }, "The grant");
        }, cancellationToken);

    [McpServerTool(Name = "access_list_field_policies", Title = "List field policies", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<FieldPolicyOutput>))]
    [Description("Lists the workspace's field masking policies (which permissions they apply to, which roles see values unmasked, the reveal permission).")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> ListFieldPolicies(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.ListFieldPoliciesAsync(principal, cancellationToken), policies => new ToolList<FieldPolicyOutput>(
            [.. policies.Select(FieldPolicy)]), "The field policy"), cancellationToken);

    [McpServerTool(Name = "access_create_grant", Title = "Create grant", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(GrantOutput))]
    [Description("Grants a role to a user, team or API client on the workspace, a team, an inbox or the tenant (allow or deny, optional expiry). Tenant grants require access:manage-tenant and built-in roles only. Access refuses grants that would escalate beyond the caller's own permissions; every call is audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true)]
    public Task<CallToolResult> CreateGrant(
        [Description("Subject kind: user, team or apiClient.")] string subjectKind,
        [Description("Subject id.")] string subjectId,
        [Description("Role id (built-in: owner, admin, supervisor, agent, light-agent, viewer; or a custom role for non-tenant targets).")] string role,
        [Description("Target kind: workspace (default), team, inbox or tenant.")] string? scope = null,
        [Description("Team or inbox id for team/inbox targets.")] string? targetId = null,
        [Description("allow (default) or deny.")] string? effect = null,
        [Description("Optional expiry (ISO 8601).")] DateTimeOffset? validUntil = null,
        [Description("Justification recorded with the grant.")] string? justification = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var subject = ParseSubject(subjectKind, subjectId);
            var grant = new AccessGrant(new GrantId(Guid.CreateVersion7().ToString("N")), AccessPartition.From(principal.Scope), subject, new RoleId(role),
                Target(principal.Scope, scope ?? "workspace", targetId), effect is null ? GrantEffect.Allow : TicketFlowToolJson.Parse<GrantEffect>(effect, nameof(effect)),
                validUntil: validUntil, justification: justification);
            return Map(await Administrator.CreateGrantAsync(principal, grant, cancellationToken), Grant, "The grant");
        }, cancellationToken);

    [McpServerTool(Name = "access_revoke_grant", Title = "Revoke grant", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(GrantOutput))]
    [Description("Revokes a grant (fenced on its revision); audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true)]
    public Task<CallToolResult> RevokeGrant(
        [Description("Grant id.")] string grantId,
        [Description("Current grant revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Administrator.RevokeGrantAsync(principal, new GrantId(grantId), expectedRevision, cancellationToken),
            Grant, "The grant"), cancellationToken);

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
            return Map(await Administrator.CreateRoleAsync(principal, role, cancellationToken), stored => Role(stored.Role, stored.Revision), "The role");
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
            return Map(await Administrator.UpdateRoleAsync(principal, role, expectedRevision, cancellationToken), stored => Role(stored.Role, stored.Revision), "The role");
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
            stored => Role(stored.Role, stored.Revision), "The role"), cancellationToken);

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
            return Map(await Administrator.CreateFieldPolicyAsync(principal, policy, cancellationToken), FieldPolicy, "The field policy");
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
            return Map(await Administrator.UpdateFieldPolicyAsync(principal, policy, expectedRevision, cancellationToken), FieldPolicy, "The field policy");
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
            FieldPolicy, "The field policy"), cancellationToken);

    // Break-glass elevation

    [McpServerTool(Name = "access_list_break_glass", Title = "List break-glass grants", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<GrantOutput>))]
    [Description("Lists active break-glass elevation grants on the workspace, a team or an inbox. Administrators (access:manage) see all break-glass grants on the target; responders with access:break-glass only see their own break-glass grants on the target.")]
    [TicketFlowTool(P.AccessBreakGlass, P.AccessManage, AnyOf = true)]
    public Task<CallToolResult> ListBreakGlass(
        [Description("Target kind: workspace (default), team or inbox.")] string? scope = null,
        [Description("Team or inbox id for team/inbox targets.")] string? targetId = null,
        [Description("Include expired break-glass grants (default false).")] bool includeExpired = false,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var target = BreakGlassTarget(principal.Scope, scope ?? "workspace", targetId);
            var now = (context.Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
            var listed = await Administrator.ListGrantsAsync(principal, target, AccessLimits.MaximumTargetPage, null, cancellationToken);
            if (listed is TicketValue<IReadOnlyList<StoredAccessGrant>> firstPage)
            {
                var breakGlass = new List<GrantOutput>();
                var page = firstPage.Value;
                while (true)
                {
                    for (var i = 0; i < page.Count; i++)
                    {
                        var item = page[i];
                        if (item.Grant.Kind == GrantKind.BreakGlass && (includeExpired || item.Grant.IsValidAt(now)))
                            breakGlass.Add(Grant(item));
                    }
                    if (page.Count < AccessLimits.MaximumTargetPage)
                        break;
                    var after = page[^1].Grant.Id;
                    var next = await Administrator.ListGrantsAsync(principal, target, AccessLimits.MaximumTargetPage, after, cancellationToken);
                    if (next is not TicketValue<IReadOnlyList<StoredAccessGrant>> nextPage)
                        return Refused((TicketFailure)next.Value!, "The break-glass grant");
                    page = nextPage.Value;
                    if (page.Count == 0 || page[^1].Grant.Id == after)
                        break;
                }
                return new ToolList<GrantOutput>(breakGlass);
            }

            var refused = (TicketFailure)listed.Value!;
            if (refused.Kind == TicketFailureKind.Forbidden
                && context.Services.GetService<IAccessEvaluator>() is { } evaluator
                && context.Services.GetService<IAccessGrantStore>() is { } grantStore)
            {
                var decision = await evaluator.AuthorizeAsync(new AccessRequest(principal, AccessPermissions.AccessBreakGlass, TargetResource(principal.Scope, target)), cancellationToken);
                if (decision.IsPermitted)
                {
                    var subjects = OwnSubjects(principal);
                    if (subjects.Count == 0)
                        return new ToolList<GrantOutput>([]);
                    var own = await grantStore.ListForSubjectsAsync(AccessPartition.From(principal.Scope), principal.Scope.WorkspaceId, subjects, cancellationToken);
                    return new ToolList<GrantOutput>([.. own
                        .Where(item => item.Grant.Kind == GrantKind.BreakGlass && item.Grant.Scope == target && (includeExpired || item.Grant.IsValidAt(now)))
                        .Select(Grant)]);
                }
            }
            return Refused(refused, "The break-glass grant");
        }, cancellationToken);

    [McpServerTool(Name = "access_activate_break_glass", Title = "Activate break-glass", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(GrantOutput))]
    [Description("Activates a time-bound break-glass elevation grant for the calling principal on the workspace, a team or an inbox. Requires access:break-glass; roles containing access-management or break-glass permissions are rejected; activation and every use are audited.")]
    [TicketFlowTool(P.AccessBreakGlass)]
    public Task<CallToolResult> ActivateBreakGlass(
        [Description("Role id to elevate to (cannot include access-management or break-glass permissions).")] string role,
        [Description("Mandatory incident justification (minimum length enforced by Access).")] string justification,
        [Description("Duration in minutes (positive, within the server's configured maximum; default 60).")] int durationMinutes = 60,
        [Description("Target kind: workspace (default), team or inbox.")] string? scope = null,
        [Description("Team or inbox id for team/inbox targets.")] string? targetId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            if (Math.Abs((long)durationMinutes) > 60L * 24 * 365)
                throw new ArgumentException("The break-glass duration is out of range.", nameof(durationMinutes));
            var request = new BreakGlassRequest(
                principal,
                new GrantId(Guid.CreateVersion7().ToString("N")),
                new RoleId(role),
                Target(principal.Scope, scope ?? "workspace", targetId),
                TimeSpan.FromMinutes(durationMinutes),
                justification ?? throw new ArgumentNullException(nameof(justification)));
            return Map(await Administrator.ActivateBreakGlassAsync(request, cancellationToken), Grant, "The break-glass grant");
        }, cancellationToken);

    [McpServerTool(Name = "access_revoke_break_glass", Title = "Revoke break-glass", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(GrantOutput))]
    [Description("Revokes a break-glass grant (fenced on its revision); requires access:manage and is audited.")]
    [TicketFlowTool(P.AccessManage)]
    public Task<CallToolResult> RevokeBreakGlass(
        [Description("Break-glass grant id.")] string grantId,
        [Description("Current grant revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var id = new GrantId(grantId);
            if (context.Services.GetService<IAccessGrantStore>() is { } grantStore
                && await grantStore.GetAsync(AccessPartition.From(principal.Scope), id, cancellationToken) is { } existing
                && await Administrator.CheckCanRevokeAsync(principal, [existing.Grant], cancellationToken) is AccessCheckPassed
                && existing.Grant.Kind != GrantKind.BreakGlass)
                return TicketFlowToolError.Invalid(["The specified grant is not a break-glass grant; use access_revoke_grant instead."]);
            return Map(await Administrator.RevokeGrantAsync(principal, id, expectedRevision, cancellationToken), Grant, "The break-glass grant");
        }, cancellationToken);

    // Service accounts & API keys

    [McpServerTool(Name = "access_list_service_accounts", Title = "List service accounts", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<ServiceAccountOutput>))]
    [Description("Lists managed service accounts visible in the workspace (workspace-scoped and tenant-scoped).")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IServiceAccountService))]
    public Task<CallToolResult> ListServiceAccounts(
        [Description("Include disabled service accounts (default true).")] bool includeDisabled = true,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await ServiceAccounts.ListServiceAccountsAsync(principal, includeDisabled, cancellationToken),
            accounts => new ToolList<ServiceAccountOutput>([.. accounts.Select(ServiceAccountView)]), "The service account"), cancellationToken);

    [McpServerTool(Name = "access_create_service_account", Title = "Create service account", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ServiceAccountOutput))]
    [Description("Creates a managed service account scoped to the workspace, a team, an inbox or the tenant. Tenant-scoped service accounts require access:manage-tenant; reserved worker client ids are rejected; audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IServiceAccountService))]
    public Task<CallToolResult> CreateServiceAccount(
        [Description("Unique API client identifier for the service account.")] string clientId,
        [Description("Display name.")] string displayName,
        [Description("Optional description.")] string? description = null,
        [Description("Target scope kind: workspace (default), tenant, team or inbox.")] string? scope = null,
        [Description("Team or inbox id for team/inbox scopes.")] string? targetId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var account = new ServiceAccount(new ApiClientId(clientId), AccessPartition.From(principal.Scope), Target(principal.Scope, scope ?? "workspace", targetId), displayName, description);
            return Map(await ServiceAccounts.CreateServiceAccountAsync(principal, account, cancellationToken), ServiceAccountView, "The service account");
        }, cancellationToken);

    [McpServerTool(Name = "access_set_service_account_enabled", Title = "Enable or disable service account", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ServiceAccountOutput))]
    [Description("Enables or disables a managed service account (fenced on optional expectedVersion). Disabling immediately blocks API key authentication and access decisions; audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IServiceAccountService))]
    public Task<CallToolResult> SetServiceAccountEnabled(
        [Description("Service account API client id.")] string clientId,
        [Description("True to enable, false to disable.")] bool isEnabled,
        [Description("Optional reason recorded in audit and metadata.")] string? reason = null,
        [Description("Optional expected version for optimistic concurrency.")] long? expectedVersion = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await ServiceAccounts.SetServiceAccountEnabledAsync(principal, new ApiClientId(clientId), isEnabled, reason, expectedVersion, cancellationToken),
            ServiceAccountView, "The service account"), cancellationToken);

    [McpServerTool(Name = "access_delete_service_account", Title = "Delete service account", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ServiceAccountDeletedOutput))]
    [Description("Deletes a managed service account and revokes all of its API keys and grants (fenced on optional expectedVersion); audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IServiceAccountService))]
    public Task<CallToolResult> DeleteServiceAccount(
        [Description("Service account API client id.")] string clientId,
        [Description("Optional expected version for optimistic concurrency.")] long? expectedVersion = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await ServiceAccounts.DeleteServiceAccountAsync(principal, new ApiClientId(clientId), expectedVersion, cancellationToken),
            deleted => new ServiceAccountDeletedOutput(deleted.Value), "The service account"), cancellationToken);

    [McpServerTool(Name = "access_list_api_keys", Title = "List service account API keys", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<ApiKeyOutput>))]
    [Description("Lists API key metadata (prefixes, expiry, usage and revocation status; never plaintext keys or hashes) for a service account.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IServiceAccountService))]
    public Task<CallToolResult> ListApiKeys(
        [Description("Service account API client id.")] string clientId,
        [Description("Include revoked and expired keys (default false).")] bool includeInactive = false,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await ServiceAccounts.ListApiKeysAsync(principal, new ApiClientId(clientId), includeInactive, cancellationToken),
            keys => new ToolList<ApiKeyOutput>([.. keys.Select(ApiKeyView)]), "The service account"), cancellationToken);

    [McpServerTool(Name = "access_issue_api_key", Title = "Issue service account API key", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(IssuedApiKeyOutput))]
    [Description("Issues a new tf_sa_* API key for a service account. The plaintext key is returned once in the response and never stored or logged; copy it immediately. Audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IServiceAccountService))]
    public Task<CallToolResult> IssueApiKey(
        [Description("Service account API client id.")] string clientId,
        [Description("Descriptive key name.")] string name,
        [Description("Optional expiration timestamp (ISO 8601 UTC).")] DateTimeOffset? expiresAtUtc = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await ServiceAccounts.IssueApiKeyAsync(principal, new ApiKeyCreateRequest(new ApiClientId(clientId), name, expiresAtUtc), cancellationToken),
            issued => new IssuedApiKeyOutput(ApiKeyView(issued.Key), issued.PlaintextKey, ApiKeyOnceWarning), "The service account"), cancellationToken);

    [McpServerTool(Name = "access_revoke_api_key", Title = "Revoke service account API key", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyOutput))]
    [Description("Revokes a service account API key immediately (fenced on optional expectedVersion); audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IServiceAccountService))]
    public Task<CallToolResult> RevokeApiKey(
        [Description("API key id.")] string keyId,
        [Description("Optional revocation reason.")] string? reason = null,
        [Description("Optional expected version for optimistic concurrency.")] long? expectedVersion = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await ServiceAccounts.RevokeApiKeyAsync(principal, new ApiKeyId(keyId), reason, expectedVersion, cancellationToken),
            ApiKeyView, "The API key"), cancellationToken);

    // Role federation mappings

    [McpServerTool(Name = "access_list_federation_mappings", Title = "List role federation mappings", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<RoleFederationMappingOutput>))]
    [Description("Lists dynamic external-role-to-TicketFlow-role federation mappings visible in the workspace.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IRoleFederationService))]
    public Task<CallToolResult> ListFederationMappings(
        [Description("Include disabled mappings (default true).")] bool includeDisabled = true,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await RoleFederation.ListMappingsAsync(principal, includeDisabled, cancellationToken),
            mappings => new ToolList<RoleFederationMappingOutput>([.. mappings.Select(FederationMappingView)]), "The role federation mapping"), cancellationToken);

    [McpServerTool(Name = "access_upsert_federation_mapping", Title = "Create or update role federation mapping", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(RoleFederationMappingOutput))]
    [Description("Creates or updates a dynamic mapping from an external identity provider role to a TicketFlow role. Enforces anti-escalation rules, tenant scope authority and optimistic concurrency; audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IRoleFederationService))]
    public Task<CallToolResult> UpsertFederationMapping(
        [Description("Identity provider name (for example 'identityflow', 'okta', 'local', or '*' for any provider).")] string provider,
        [Description("External role or group name carried by the identity provider.")] string externalRole,
        [Description("TicketFlow role id to grant.")] string role,
        [Description("Optional mapping id (generates a new id when omitted; pass an existing id to update).")] string? mappingId = null,
        [Description("Target scope kind: workspace (default), tenant, team or inbox.")] string? scope = null,
        [Description("Team or inbox id for team/inbox scopes.")] string? targetId = null,
        [Description("Whether the mapping is enabled (default true).")] bool isEnabled = true,
        [Description("Optional description.")] string? description = null,
        [Description("Optional expected version for optimistic concurrency (0 for new, or current version for update).")] long? expectedVersion = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var id = mappingId is null ? RoleFederationMappingId.NewId() : new RoleFederationMappingId(mappingId);
            var mapping = new RoleFederationMapping(provider, externalRole, new RoleId(role), AccessPartition.From(principal.Scope),
                Target(principal.Scope, scope ?? "workspace", targetId), description);
            return Map(await RoleFederation.UpsertMappingAsync(principal, id, mapping, isEnabled, expectedVersion, cancellationToken),
                FederationMappingView, "The role federation mapping");
        }, cancellationToken);

    [McpServerTool(Name = "access_delete_federation_mapping", Title = "Delete role federation mapping", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(RoleFederationMappingDeletedOutput))]
    [Description("Deletes a dynamic role federation mapping (fenced on optional expectedVersion); enforces anti-escalation rules and is audited.")]
    [TicketFlowTool(P.AccessManage, P.AccessManageTenant, AnyOf = true, Service = typeof(IRoleFederationService))]
    public Task<CallToolResult> DeleteFederationMapping(
        [Description("Role federation mapping id.")] string mappingId,
        [Description("Optional expected version for optimistic concurrency.")] long? expectedVersion = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await RoleFederation.DeleteMappingAsync(principal, new RoleFederationMappingId(mappingId), expectedVersion, cancellationToken),
            deleted => new RoleFederationMappingDeletedOutput(deleted.Value), "The role federation mapping"), cancellationToken);

    private static FieldPolicyOutput FieldPolicy(StoredAccessFieldPolicy stored) => new(
        stored.Policy.Field.Value,
        [.. stored.Policy.AppliesTo.Select(permission => permission.Value).Order(StringComparer.Ordinal)],
        [.. stored.Policy.UnmaskedRoles.Select(role => role.Value).Order(StringComparer.Ordinal)],
        stored.Policy.RevealPermission?.Value,
        stored.Revision);

    private static GrantSubject ParseSubject(string subjectKind, string subjectId) => subjectKind switch
    {
        "user" => new UserId(subjectId),
        "team" => new TeamId(subjectId),
        "apiClient" => new ApiClientId(subjectId),
        _ => throw new ArgumentException("Subject kinds are user, team or apiClient.", nameof(subjectKind)),
    };

    private static List<GrantSubject> OwnSubjects(PrincipalRef principal) => principal switch
    {
        UserPrincipal user => [user.UserId, .. user.Teams.Select(team => (GrantSubject)team)],
        ApiClientPrincipal client => [client.ClientId],
        AutomationPrincipal => [],
        SystemPrincipal => [],
    };

    private static GrantScope Target(TicketScope scope, string kind, string? id) => kind switch
    {
        "tenant" => TenantGrantScope.Instance,
        "workspace" => new WorkspaceGrantScope(scope.WorkspaceId),
        "team" => new TeamGrantScope(scope.WorkspaceId, new TeamId(id ?? throw new ArgumentException("'targetId' is required for team targets.", nameof(id)))),
        "inbox" => new InboxGrantScope(scope.WorkspaceId, new InboxId(id ?? throw new ArgumentException("'targetId' is required for inbox targets.", nameof(id)))),
        _ => throw new ArgumentException("Target kinds are tenant, workspace, team or inbox.", nameof(kind)),
    };

    private static GrantScope BreakGlassTarget(TicketScope scope, string kind, string? id) => kind switch
    {
        "workspace" => new WorkspaceGrantScope(scope.WorkspaceId),
        "team" => new TeamGrantScope(scope.WorkspaceId, new TeamId(id ?? throw new ArgumentException("'targetId' is required for team targets.", nameof(id)))),
        "inbox" => new InboxGrantScope(scope.WorkspaceId, new InboxId(id ?? throw new ArgumentException("'targetId' is required for inbox targets.", nameof(id)))),
        _ => throw new ArgumentException("Break-glass target kinds are workspace, team or inbox.", nameof(kind)),
    };

    private static AccessResource TargetResource(TicketScope scope, GrantScope target) => target switch
    {
        TeamGrantScope team => new(scope, team: team.Team),
        InboxGrantScope inbox => new(scope, inbox: inbox.Inbox),
        _ => new(scope),
    };

    private static GrantScopeOutput ScopeOutput(GrantScope scope) => scope switch
    {
        TenantGrantScope => new("tenant", null),
        WorkspaceGrantScope => new("workspace", null),
        TeamGrantScope team => new("team", team.Team.Value),
        InboxGrantScope inbox => new("inbox", inbox.Inbox.Value),
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
            ScopeOutput(grant.Scope),
            TicketFlowToolJson.Token(grant.Effect),
            grant.Condition is not null,
            grant.ValidFrom,
            grant.ValidUntil,
            TicketFlowToolJson.Token(grant.Kind),
            stored.Revision,
            grant.Justification);
    }

    private static ServiceAccountOutput ServiceAccountView(StoredServiceAccount stored) => new(
        stored.Account.ClientId.Value,
        stored.Account.DisplayName,
        stored.Account.Description,
        ScopeOutput(stored.Account.Scope),
        stored.IsEnabled,
        stored.Version,
        stored.CreatedAtUtc,
        stored.CreatedByPrincipalId,
        stored.UpdatedAtUtc,
        stored.UpdatedByPrincipalId,
        stored.DisabledAtUtc,
        stored.DisabledReason);

    private static ApiKeyOutput ApiKeyView(StoredApiKey stored) => new(
        stored.Id.Value,
        stored.ClientId.Value,
        stored.Name,
        stored.KeyPrefix,
        stored.Version,
        stored.CreatedAtUtc,
        stored.CreatedByPrincipalId,
        stored.ExpiresAtUtc,
        stored.LastUsedAtUtc,
        stored.RevokedAtUtc,
        stored.RevocationReason);

    private static RoleFederationMappingOutput FederationMappingView(StoredRoleFederationMapping stored) => new(
        stored.Id.Value,
        stored.Mapping.Provider,
        stored.Mapping.ExternalRole,
        stored.Mapping.RoleId.Value,
        ScopeOutput(stored.Mapping.Scope),
        stored.Mapping.Description,
        stored.IsEnabled,
        stored.Version,
        stored.CreatedAtUtc,
        stored.CreatedByPrincipalId,
        stored.UpdatedAtUtc,
        stored.UpdatedByPrincipalId);

    private static TicketFlowToolError Refused(TicketFailure refused, string notFoundWhat) => refused.Kind switch
    {
        TicketFailureKind.Forbidden => TicketFlowToolError.Forbidden(),
        TicketFailureKind.Invalid => TicketFlowToolError.Invalid(refused.Errors),
        TicketFailureKind.NotFound => TicketFlowToolError.NotFound(notFoundWhat),
        TicketFailureKind.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision) with { Errors = refused.Errors.Count > 0 ? refused.Errors : null },
        _ => TicketFlowToolError.Rejected(refused.Errors),
    };

    private static object Map<T>(TicketResult<AccessChangeApplied<T>> result, Func<T, object> success, string notFoundWhat) => result switch
    {
        TicketValue<AccessChangeApplied<T>> applied => success(applied.Value.Value),
        TicketFailure refused => Refused(refused, notFoundWhat),
    };

    private static object Map<T>(TicketResult<T> result, Func<T, object> success, string notFoundWhat) => result switch
    {
        TicketValue<T> listed => success(listed.Value),
        TicketFailure refused => Refused(refused, notFoundWhat),
    };
}
