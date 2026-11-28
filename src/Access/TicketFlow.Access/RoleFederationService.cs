using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Default <see cref="IRoleFederationService"/>. Enforces anti-escalation (actor must hold every permission of the
/// mapped role via standard grants), anti-break-glass, scope containment, audit logging, and exportable role catalogs.
/// </summary>
public sealed class RoleFederationService : IRoleFederationService
{
    private readonly IRoleFederationMappingStore mappingStore;
    private readonly IAccessRoleStore roleStore;
    private readonly IAccessAuditSink auditSink;
    private readonly IPermissionCatalog catalog;
    private readonly AccessEngine engine;

    public RoleFederationService(
        IRoleFederationMappingStore mappingStore,
        IAccessRoleStore roleStore,
        IAccessGrantStore grantStore,
        IAccessAuditSink auditSink,
        IPermissionCatalog catalog,
        TimeProvider? timeProvider = null,
        IOptions<AccessControlOptions>? options = null,
        IServiceAccountStore? serviceAccounts = null)
    {
        ArgumentNullException.ThrowIfNull(mappingStore);
        ArgumentNullException.ThrowIfNull(roleStore);
        ArgumentNullException.ThrowIfNull(grantStore);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(catalog);

        var resolvedOptions = options?.Value;
        resolvedOptions?.Validate();

        this.mappingStore = mappingStore;
        this.roleStore = roleStore;
        this.auditSink = auditSink;
        this.catalog = catalog;
        engine = new(catalog, grantStore, roleStore, timeProvider ?? TimeProvider.System, resolvedOptions, serviceAccounts, mappingStore);
    }

    public async ValueTask<TicketResult<IReadOnlyList<StoredRoleFederationMapping>>> ListMappingsAsync(
        PrincipalRef actor,
        bool includeDisabled = true,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var (authFailure, _) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (authFailure is not null)
            return authFailure;

        var partition = AccessPartition.From(actor.Scope);
        var mappings = await mappingStore.ListForPartitionAsync(partition, includeDisabled, cancellationToken).ConfigureAwait(false);
        var visible = new List<StoredRoleFederationMapping>(mappings.Count);
        for (var i = 0; i < mappings.Count; i++)
            if (IsVisibleInWorkspace(mappings[i].Mapping, actor.Scope))
                visible.Add(mappings[i]);

        return TicketResult.Ok<IReadOnlyList<StoredRoleFederationMapping>>(visible.AsReadOnly());
    }

    public async ValueTask<TicketResult<AccessChangeApplied<StoredRoleFederationMapping>>> UpsertMappingAsync(
        PrincipalRef actor,
        RoleFederationMappingId mappingId,
        RoleFederationMapping mapping,
        bool isEnabled = true,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(mapping);

        var requiredPermission = GetRequiredAdminPermission(mapping.Scope);
        var resourceId = mappingId.IsInitialized ? mappingId.Value : null;
        var targetResource = TargetForAudit(actor.Scope, mapping.Partition, mapping.Scope, resourceId);

        if (!mappingId.IsInitialized)
            return await RefuseAsync<StoredRoleFederationMapping>(actor, AccessAuditKind.RoleFederationMappingCreated, requiredPermission, targetResource, mapping.RoleId,
                TicketFailure.Invalid("A mapping identifier is required."), cancellationToken).ConfigureAwait(false);

        var (scopeFailure, context) = await AuthorizeScopeManagementAsync(actor, mapping.Partition, mapping.Scope, requiredPermission, context: null, cancellationToken).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await RefuseAsync<StoredRoleFederationMapping>(actor, AccessAuditKind.RoleFederationMappingCreated, requiredPermission, targetResource, mapping.RoleId,
                scopeFailure, cancellationToken).ConfigureAwait(false);

        if (expectedVersion is < 0)
            return await RefuseAsync<StoredRoleFederationMapping>(actor, AccessAuditKind.RoleFederationMappingCreated, requiredPermission, targetResource, mapping.RoleId,
                TicketFailure.Invalid("Expected version cannot be negative."), cancellationToken).ConfigureAwait(false);

        var existing = await mappingStore.FindAsync(mapping.Partition, mappingId, cancellationToken).ConfigureAwait(false);

        // Authorize existing mapping scope BEFORE evaluating expectedVersion or role validation to prevent cross-scope version/existence oracles.
        // Never copy metadata from an unauthorized existing mapping into the actor's audit log.
        if (existing is not null)
        {
            var existingRequiredPermission = GetRequiredAdminPermission(existing.Mapping.Scope);
            var (existingScopeFailure, _) = await AuthorizeScopeManagementAsync(actor, existing.Mapping.Partition, existing.Mapping.Scope, existingRequiredPermission, context, cancellationToken).ConfigureAwait(false);
            if (existingScopeFailure is not null)
                return await RefuseAsync<StoredRoleFederationMapping>(actor, AccessAuditKind.RoleFederationMappingCreated, requiredPermission, targetResource, mapping.RoleId,
                    AccessFailureSanitizer.ToOpaqueNotFound(existingScopeFailure), cancellationToken).ConfigureAwait(false);
        }

        if (expectedVersion is > 0 && existing is null)
            return await RefuseAsync<StoredRoleFederationMapping>(actor, AccessAuditKind.RoleFederationMappingCreated, requiredPermission, targetResource, mapping.RoleId,
                TicketFailure.NotFound, cancellationToken).ConfigureAwait(false);

        var auditKind = existing is null ? AccessAuditKind.RoleFederationMappingCreated : AccessAuditKind.RoleFederationMappingUpdated;

        if (expectedVersion is { } expectedUpsert)
        {
            var currentVersion = existing?.Version ?? 0;
            if (currentVersion != expectedUpsert)
                return await RefuseAsync<StoredRoleFederationMapping>(actor, auditKind, requiredPermission, targetResource, mapping.RoleId,
                    TicketFailure.Conflict(existing?.Version, [$"Role federation mapping '{mappingId.Value}' is at version {currentVersion}, expected {expectedUpsert}."], code: "concurrency-conflict"),
                    cancellationToken).ConfigureAwait(false);
        }

        var (rolePermissions, roleFailure) = await ResolveAndValidateRoleAsync(actor.Scope, mapping.RoleId, mapping.Scope, cancellationToken).ConfigureAwait(false);
        if (roleFailure is not null)
            return await RefuseAsync<StoredRoleFederationMapping>(actor, auditKind, requiredPermission, targetResource, mapping.RoleId, roleFailure, cancellationToken).ConfigureAwait(false);

        var escalationFailure = CheckPermissionsHeld(context!, rolePermissions, AccessEngine.Target(actor.Scope, mapping.Scope), mapping.Scope is TenantGrantScope);
        if (escalationFailure is not null)
            return await RefuseAsync<StoredRoleFederationMapping>(actor, auditKind, requiredPermission, targetResource, mapping.RoleId, escalationFailure, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            var existingRequiredPermission = GetRequiredAdminPermission(existing.Mapping.Scope);
            var existingRole = await FindRoleAsync(actor.Scope, existing.Mapping.RoleId, existing.Mapping.Scope, cancellationToken).ConfigureAwait(false);
            if (existingRole is not null)
            {
                var existingPerms = ExpandRolePermissions(existingRole);
                var existingEscalation = CheckPermissionsHeld(context!, existingPerms, AccessEngine.Target(actor.Scope, existing.Mapping.Scope), existing.Mapping.Scope is TenantGrantScope);
                if (existingEscalation is not null)
                    return await RefuseAsync<StoredRoleFederationMapping>(actor, auditKind, existingRequiredPermission, targetResource, existing.Mapping.RoleId, existingEscalation, cancellationToken).ConfigureAwait(false);
            }
        }

        var pinnedVersion = expectedVersion ?? (existing?.Version ?? 0);
        var upsertResult = await mappingStore.UpsertAsync(mappingId, mapping, isEnabled, FormatActorId(actor), pinnedVersion, cancellationToken).ConfigureAwait(false);
        if (upsertResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<StoredRoleFederationMapping>(actor, auditKind, requiredPermission, targetResource, mapping.RoleId, failure, cancellationToken).ConfigureAwait(false);

        upsertResult.TryGetValue(out TicketValue<StoredRoleFederationMapping> ok);
        var auditEvent = await RecordAuditAsync(actor, auditKind, AccessAuditOutcome.Allowed, requiredPermission, targetResource, ok.Value.Mapping.RoleId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<StoredRoleFederationMapping>(ok.Value, auditEvent.Id));
    }

    public async ValueTask<TicketResult<AccessChangeApplied<RoleFederationMappingId>>> DeleteMappingAsync(
        PrincipalRef actor,
        RoleFederationMappingId mappingId,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!mappingId.IsInitialized)
            return TicketFailure.Invalid("A mapping identifier is required.");

        var fallbackResource = new AccessResource(actor.Scope, resourceId: mappingId.Value);
        var (baseFailure, context) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (baseFailure is not null)
            return await RefuseAsync<RoleFederationMappingId>(actor, AccessAuditKind.RoleFederationMappingDeleted, AccessPermissions.AccessManage, fallbackResource, roleId: null, baseFailure, cancellationToken).ConfigureAwait(false);

        if (expectedVersion is < 0)
            return await RefuseAsync<RoleFederationMappingId>(actor, AccessAuditKind.RoleFederationMappingDeleted, AccessPermissions.AccessManage, fallbackResource, roleId: null,
                TicketFailure.Invalid("Expected version cannot be negative."), cancellationToken).ConfigureAwait(false);

        var partition = AccessPartition.From(actor.Scope);
        var existing = await mappingStore.FindAsync(partition, mappingId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return await RefuseAsync<RoleFederationMappingId>(actor, AccessAuditKind.RoleFederationMappingDeleted, AccessPermissions.AccessManage, fallbackResource, roleId: null, TicketFailure.NotFound, cancellationToken).ConfigureAwait(false);

        var requiredPermission = GetRequiredAdminPermission(existing.Mapping.Scope);
        var (scopeFailure, _) = await AuthorizeScopeManagementAsync(actor, existing.Mapping.Partition, existing.Mapping.Scope, requiredPermission, context, cancellationToken).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await RefuseAsync<RoleFederationMappingId>(actor, AccessAuditKind.RoleFederationMappingDeleted, AccessPermissions.AccessManage, fallbackResource, roleId: null,
                AccessFailureSanitizer.ToOpaqueNotFound(scopeFailure), cancellationToken).ConfigureAwait(false);

        var targetResource = TargetForAudit(actor.Scope, existing.Mapping.Partition, existing.Mapping.Scope, mappingId.Value);

        if (expectedVersion is { } expectedDelete && existing.Version != expectedDelete)
            return await RefuseAsync<RoleFederationMappingId>(actor, AccessAuditKind.RoleFederationMappingDeleted, requiredPermission, targetResource, existing.Mapping.RoleId,
                TicketFailure.Conflict(existing.Version, [$"Role federation mapping '{mappingId.Value}' is at version {existing.Version}, expected {expectedDelete}."], code: "concurrency-conflict"),
                cancellationToken).ConfigureAwait(false);

        var existingRole = await FindRoleAsync(actor.Scope, existing.Mapping.RoleId, existing.Mapping.Scope, cancellationToken).ConfigureAwait(false);
        if (existingRole is not null)
        {
            var existingPerms = ExpandRolePermissions(existingRole);
            var escalationFailure = CheckPermissionsHeld(context!, existingPerms, AccessEngine.Target(actor.Scope, existing.Mapping.Scope), existing.Mapping.Scope is TenantGrantScope);
            if (escalationFailure is not null)
                return await RefuseAsync<RoleFederationMappingId>(actor, AccessAuditKind.RoleFederationMappingDeleted, requiredPermission, targetResource, existing.Mapping.RoleId, escalationFailure, cancellationToken).ConfigureAwait(false);
        }

        var deleteResult = await mappingStore.DeleteAsync(partition, mappingId, expectedVersion ?? existing.Version, cancellationToken).ConfigureAwait(false);
        if (deleteResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<RoleFederationMappingId>(actor, AccessAuditKind.RoleFederationMappingDeleted, requiredPermission, targetResource, existing.Mapping.RoleId, failure, cancellationToken).ConfigureAwait(false);

        var auditEvent = await RecordAuditAsync(actor, AccessAuditKind.RoleFederationMappingDeleted, AccessAuditOutcome.Allowed, requiredPermission, targetResource, existing.Mapping.RoleId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<RoleFederationMappingId>(mappingId, auditEvent.Id));
    }

    public async ValueTask<TicketResult<FederatedRoleCatalog>> GetRoleCatalogAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var (authFailure, _) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (authFailure is not null)
            return authFailure;

        var partition = AccessPartition.From(actor.Scope);
        var customRoles = await roleStore.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false);
        var activeMappings = await mappingStore.ListForPartitionAsync(partition, includeDisabled: false, cancellationToken).ConfigureAwait(false);

        var mappingsByRole = new Dictionary<RoleId, List<FederatedRoleMappingSummary>>();
        for (var i = 0; i < activeMappings.Count; i++)
        {
            var stored = activeMappings[i];
            if (!stored.IsEnabled || !IsVisibleInWorkspace(stored.Mapping, actor.Scope))
                continue;

            if (!mappingsByRole.TryGetValue(stored.Mapping.RoleId, out var list))
                mappingsByRole[stored.Mapping.RoleId] = list = [];
            list.Add(RoleFederationSupport.ToSummary(stored));
        }

        var descriptors = new List<FederatedRoleDescriptor>(BuiltInRoles.All.Count + customRoles.Count);
        for (var i = 0; i < BuiltInRoles.All.Count; i++)
            descriptors.Add(CreateDescriptor(BuiltInRoles.All[i], mappingsByRole));

        var sortedCustom = customRoles.ToList();
        sortedCustom.Sort(static (left, right) => left.Role.Id.CompareTo(right.Role.Id));
        for (var i = 0; i < sortedCustom.Count; i++)
            descriptors.Add(CreateDescriptor(sortedCustom[i].Role, mappingsByRole));

        return TicketResult.Ok(new FederatedRoleCatalog(
            actor.Scope.TenantId,
            actor.Scope.ProjectId,
            actor.Scope.EnvironmentId,
            actor.Scope.AppId,
            actor.Scope.WorkspaceId,
            engine.Now,
            descriptors.AsReadOnly()));
    }

    private FederatedRoleDescriptor CreateDescriptor(AccessRole role, Dictionary<RoleId, List<FederatedRoleMappingSummary>> mappingsByRole)
    {
        var permissions = ExpandRolePermissions(role)
            .Select(static p => p.Value)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<FederatedRoleMappingSummary> roleMappings =
            mappingsByRole.TryGetValue(role.Id, out var list) ? list.AsReadOnly() : [];

        return new FederatedRoleDescriptor(
            role.Id,
            role.Name,
            role.IsBuiltIn,
            role.IncludesAllPermissions,
            permissions,
            roleMappings);
    }

    private async Task<AccessRole?> FindRoleAsync(TicketScope scope, RoleId roleId, GrantScope grantScope, CancellationToken cancellationToken)
    {
        if (BuiltInRoles.Find(roleId) is { } builtIn)
            return builtIn;
        if (grantScope is TenantGrantScope)
            return null;
        return (await roleStore.GetAsync(scope, roleId, cancellationToken).ConfigureAwait(false))?.Role;
    }

    private async Task<(IReadOnlyList<Permission> Permissions, TicketFailure? Failure)> ResolveAndValidateRoleAsync(
        TicketScope scope,
        RoleId roleId,
        GrantScope grantScope,
        CancellationToken cancellationToken)
    {
        if (grantScope is TenantGrantScope && !BuiltInRoles.IsBuiltIn(roleId))
            return ([], TicketFailure.Invalid("Tenant-scoped role federation mappings can only reference built-in roles."));

        var role = await FindRoleAsync(scope, roleId, grantScope, cancellationToken).ConfigureAwait(false);
        if (role is null)
            return ([], TicketFailure.NotFound);

        var expanded = ExpandRolePermissions(role);
        for (var i = 0; i < expanded.Length; i++)
            if (!catalog.Contains(expanded[i]))
                return ([], TicketFailure.Invalid($"Role '{roleId.Value}' references unknown permission '{expanded[i].Value}'."));

        return (expanded, null);
    }

    private Permission[] ExpandRolePermissions(AccessRole role)
    {
        if (role.IncludesAllPermissions)
        {
            var defs = catalog.Definitions;
            var all = new Permission[defs.Count];
            for (var i = 0; i < defs.Count; i++)
                all[i] = defs[i].Permission;
            return all;
        }

        return [.. role.Permissions];
    }

    private static TicketFailure? CheckPermissionsHeld(AccessContext context, IReadOnlyList<Permission> permissions, AccessResource target, bool tenantOnly)
    {
        List<Permission>? missing = null;
        for (var i = 0; i < permissions.Count; i++)
        {
            var permission = permissions[i];
            if (!AccessEngine.EvaluateSimple(context, permission, target, includeBreakGlass: false, tenantAllowsOnly: tenantOnly, ConditionEvaluationMode.ConservativeAll).Allowed)
                (missing ??= []).Add(permission);
        }

        if (missing is null)
            return null;

        missing.Sort();
        var errors = new string[missing.Count];
        for (var i = 0; i < missing.Count; i++)
            errors[i] = $"The actor does not hold '{missing[i]}' on the target.";
        return new TicketFailure(TicketFailureKind.Forbidden, errors, Code: "privilege-escalation");
    }

    private async Task<(TicketFailure? Failure, AccessContext? Context)> AuthorizeBaseAdminAsync(PrincipalRef actor, CancellationToken cancellationToken)
    {
        if (engine.Precheck(actor, AccessPermissions.AccessManage, actor.Scope) is { } precheck)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: precheck.ToString()), null);

        var workspaceTarget = new AccessResource(actor.Scope);
        var (saDenial, sa) = await engine.CheckServiceAccountAsync(actor, actor.Scope, workspaceTarget, cancellationToken).ConfigureAwait(false);
        if (saDenial is { } saReason)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: saReason.ToString()), null);

        var context = await engine.LoadAsync(actor, actor.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);

        var manage = AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManage, workspaceTarget, includeBreakGlass: false, conditionMode: ConditionEvaluationMode.ConservativeResources);
        if (manage.Allowed)
            return (null, context);
        if (manage.Denial == AccessDenialReason.ExplicitDeny)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is explicitly denied administrative permission."], Code: nameof(AccessDenialReason.ExplicitDeny)), context);

        var canManageTenantScope = sa is null || AccessEngine.ScopeContains(sa.Account.Scope, TenantGrantScope.Instance);
        if (canManageTenantScope)
        {
            var manageTenant = AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManageTenant, workspaceTarget, includeBreakGlass: false, tenantAllowsOnly: true, ConditionEvaluationMode.ConservativeResources);
            if (manageTenant.Allowed)
                return (null, context);
            if (manageTenant.Denial == AccessDenialReason.ExplicitDeny)
                return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is explicitly denied administrative permission."], Code: nameof(AccessDenialReason.ExplicitDeny)), context);
        }

        var viaBreakGlass = AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManage, workspaceTarget, includeBreakGlass: true, conditionMode: ConditionEvaluationMode.ConservativeResources).Allowed
            || (canManageTenantScope
                && AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManageTenant, workspaceTarget, includeBreakGlass: true, tenantAllowsOnly: true, ConditionEvaluationMode.ConservativeResources).Allowed);
        if (viaBreakGlass)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["Break-glass elevation cannot be used for role federation administration."], Code: "break-glass-not-allowed"), context);

        return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor lacks required administrative permission."], Code: "forbidden"), context);
    }

    private async Task<(TicketFailure? Failure, AccessContext? Context)> AuthorizeScopeManagementAsync(
        PrincipalRef actor,
        AccessPartition targetPartition,
        GrantScope targetGrantScope,
        Permission requiredPermission,
        AccessContext? context,
        CancellationToken cancellationToken)
    {
        var actorPartition = AccessPartition.From(actor.Scope);
        if (targetPartition != actorPartition)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The target partition is outside the actor's scope."], Code: "out-of-scope"), context);

        if (AccessEngine.WorkspaceOf(targetGrantScope) is { } targetWorkspace && targetWorkspace != actor.Scope.WorkspaceId)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The target workspace is outside the actor's scope."], Code: "out-of-scope"), context);

        if (engine.Precheck(actor, requiredPermission, actor.Scope) is { } precheck)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: precheck.ToString()), context);

        var (saDenial, _) = await engine.CheckServiceAccountForGrantScopeAsync(actor, actor.Scope, targetGrantScope, cancellationToken).ConfigureAwait(false);
        if (saDenial is { } saReason)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: saReason.ToString()), context);

        var targetResource = AccessEngine.Target(actor.Scope, targetGrantScope);
        context ??= await engine.LoadAsync(actor, actor.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);
        var tenantOnly = targetGrantScope is TenantGrantScope;
        var standard = AccessEngine.EvaluateSimple(context, requiredPermission, targetResource, includeBreakGlass: false, tenantAllowsOnly: tenantOnly, ConditionEvaluationMode.ConservativeResources);
        if (standard.Allowed)
            return (null, context);
        if (standard.Denial == AccessDenialReason.ExplicitDeny)
            return (new TicketFailure(TicketFailureKind.Forbidden, [$"The actor is explicitly denied '{requiredPermission}' on the target scope."], Code: nameof(AccessDenialReason.ExplicitDeny)), context);

        var withBreakGlass = AccessEngine.EvaluateSimple(context, requiredPermission, targetResource, includeBreakGlass: true, tenantAllowsOnly: tenantOnly, ConditionEvaluationMode.ConservativeResources);
        if (withBreakGlass.Allowed)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["Break-glass elevation cannot be used for role federation administration."], Code: "break-glass-not-allowed"), context);

        return (new TicketFailure(TicketFailureKind.Forbidden, [$"The actor lacks standard permission '{requiredPermission}' on the target scope."], Code: "forbidden"), context);
    }

    private async Task<TicketResult<AccessChangeApplied<T>>> RefuseAsync<T>(
        PrincipalRef actor,
        AccessAuditKind kind,
        Permission permission,
        AccessResource resource,
        RoleId? roleId,
        TicketFailure failure,
        CancellationToken cancellationToken)
    {
        var denialReason = failure.Code switch
        {
            "out-of-scope" or nameof(AccessDenialReason.ScopeMismatch) => AccessDenialReason.ScopeMismatch,
            nameof(AccessDenialReason.ExplicitDeny) => AccessDenialReason.ExplicitDeny,
            nameof(AccessDenialReason.ServiceAccountDisabled) => AccessDenialReason.ServiceAccountDisabled,
            nameof(AccessDenialReason.UnsupportedPrincipal) => AccessDenialReason.UnsupportedPrincipal,
            nameof(AccessDenialReason.UnknownPermission) => AccessDenialReason.UnknownPermission,
            _ when failure.Kind == TicketFailureKind.Forbidden => AccessDenialReason.NoMatchingGrant,
            _ => (AccessDenialReason?)null,
        };
        await RecordAuditAsync(actor, kind, AccessAuditOutcome.Denied, permission, resource, roleId, denialReason, cancellationToken).ConfigureAwait(false);
        return failure;
    }

    private async Task<AccessAuditEvent> RecordAuditAsync(
        PrincipalRef actor,
        AccessAuditKind kind,
        AccessAuditOutcome outcome,
        Permission permission,
        AccessResource resource,
        RoleId? roleId = null,
        AccessDenialReason? denialReason = null,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = AccessEngine.AuditEvent(
            engine.Now,
            actor,
            kind,
            outcome,
            permission,
            resource,
            role: roleId,
            denialReason: denialReason);
        await auditSink.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return auditEvent;
    }

    private static bool IsVisibleInWorkspace(RoleFederationMapping mapping, TicketScope actorScope) =>
        mapping.Partition == AccessPartition.From(actorScope)
        && (mapping.Workspace is not { } workspace || workspace == actorScope.WorkspaceId);

    private static Permission GetRequiredAdminPermission(GrantScope scope) =>
        scope is TenantGrantScope ? AccessPermissions.AccessManageTenant : AccessPermissions.AccessManage;

    private static AccessResource TargetForAudit(TicketScope actorScope, AccessPartition targetPartition, GrantScope grantScope, string? resourceId) =>
        targetPartition == AccessPartition.From(actorScope)
            ? grantScope switch
            {
                TeamGrantScope team when team.Workspace == actorScope.WorkspaceId => new(actorScope, resourceId: resourceId, team: team.Team),
                InboxGrantScope inbox when inbox.Workspace == actorScope.WorkspaceId => new(actorScope, resourceId: resourceId, inbox: inbox.Inbox),
                _ => new(actorScope, resourceId: resourceId),
            }
            : new(actorScope, resourceId: resourceId);

    private static string FormatActorId(PrincipalRef actor)
    {
        var accessActor = AccessActor.From(actor);
        var kind = accessActor.Kind switch
        {
            AccessActorKind.User => "user",
            AccessActorKind.ApiClient => "client",
            AccessActorKind.Automation => "automation",
            AccessActorKind.System => "system",
            _ => "actor",
        };
        var formatted = $"{kind}:{accessActor.Id}";
        return formatted.Length <= TicketScope.MaximumIdentifierLength ? formatted : accessActor.Id;
    }

    private static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }
}
