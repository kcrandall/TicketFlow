using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Default <see cref="IAccessAdministrator"/>. Roles and field policies are managed in the actor's workspace scope and require
/// <c>access:manage</c>; workspace, team and inbox grants require <c>access:manage</c> on their target, tenant grants require
/// <c>access:manage-tenant</c>, and an actor can only grant allow roles whose permissions it already holds on the target.
/// Management permissions are evaluated from standard grants only. Each call records one audit event after the store write.
/// </summary>
public sealed class AccessAdministrator : IAccessAdministrator
{
    private readonly AccessEngine engine;
    private readonly IAccessGrantStore grants;
    private readonly IAccessRoleStore roles;
    private readonly IAccessFieldPolicyStore policies;
    private readonly IAccessAuditSink audit;
    private readonly IRoleFederationMappingStore? roleFederationMappings;
    private readonly AccessControlOptions options;

    public AccessAdministrator(
        IPermissionCatalog catalog,
        IAccessGrantStore grants,
        IAccessRoleStore roles,
        IAccessFieldPolicyStore policies,
        IAccessAuditSink audit,
        TimeProvider time,
        IOptions<AccessControlOptions> options,
        IServiceAccountStore? serviceAccounts = null,
        IRoleFederationMappingStore? roleFederationMappings = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(time);
        this.grants = grants ?? throw new ArgumentNullException(nameof(grants));
        this.roles = roles ?? throw new ArgumentNullException(nameof(roles));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.roleFederationMappings = roleFederationMappings;
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        engine = new(catalog, grants, roles, time, this.options, serviceAccounts, roleFederationMappings);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessRole>>> CreateRoleAsync(PrincipalRef actor, AccessRole role, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(role);
        var change = new Change(AccessAuditKind.RoleCreated, actor, Role: role.Id);
        var workspace = new AccessResource(actor.Scope);
        var (refusal, context) = await AuthorizeWithContextAsync(actor, AccessPermissions.AccessManage, workspace, cancellationToken).ConfigureAwait(false);
        refusal ??= ValidateRole(role) ?? RequireHeld(context!, role.Permissions, workspace, tenantAllowsOnly: false);
        if (refusal is not null)
            return await RefuseAsync<StoredAccessRole>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await roles.CreateAsync(actor.Scope, role, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The actor must hold every permission of both the current and the new definition, so it could have created every grant that
    /// references the role (custom roles are workspace-scoped and never resolve at tenant scope).
    /// </summary>
    public async Task<TicketResult<AccessChangeApplied<StoredAccessRole>>> UpdateRoleAsync(PrincipalRef actor, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(role);
        var change = new Change(AccessAuditKind.RoleUpdated, actor, Role: role.Id);
        var workspace = new AccessResource(actor.Scope);
        var (refusal, context) = await AuthorizeWithContextAsync(actor, AccessPermissions.AccessManage, workspace, cancellationToken).ConfigureAwait(false);
        refusal ??= ValidateRole(role) ?? ValidateRevision(expectedRevision);
        if (refusal is null)
        {
            var existing = await roles.GetAsync(actor.Scope, role.Id, cancellationToken).ConfigureAwait(false);
            refusal = Fence(existing?.Revision, expectedRevision)
                ?? RequireHeld(context!, existing!.Role.Permissions.Concat(role.Permissions), workspace, tenantAllowsOnly: false);
            if (refusal is null
                && ((existing!.Role.IncludesAllPermissions && !role.IncludesAllPermissions) || !existing.Role.Permissions.IsSubsetOf(role.Permissions))
                && await grants.HasLiveDenyForRoleAsync(AccessPartition.From(actor.Scope), actor.Scope.WorkspaceId, role.Id, engine.Now, cancellationToken).ConfigureAwait(false))
                refusal = new Refusal(TicketFailureKind.Conflict,
                    [$"Cannot remove permissions from custom role '{role.Id.Value}' while referenced by an active Deny grant."],
                    Code: "role-in-use");
        }
        if (refusal is not null)
            return await RefuseAsync<StoredAccessRole>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await roles.UpdateAsync(actor.Scope, role, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessRole>>> DeleteRoleAsync(PrincipalRef actor, RoleId role, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!role.IsInitialized)
            throw new ArgumentException("A role identifier is required.", nameof(role));
        var change = new Change(AccessAuditKind.RoleDeleted, actor, Role: role);
        var workspace = new AccessResource(actor.Scope);
        var (refusal, context) = await AuthorizeWithContextAsync(actor, AccessPermissions.AccessManage, workspace, cancellationToken).ConfigureAwait(false);
        refusal ??= (BuiltInRoles.IsBuiltIn(role) ? new Refusal(TicketFailureKind.Rejected, ["Built-in roles are immutable."]) : null)
            ?? ValidateRevision(expectedRevision);
        if (refusal is null)
        {
            var existing = await roles.GetAsync(actor.Scope, role, cancellationToken).ConfigureAwait(false);
            refusal = Fence(existing?.Revision, expectedRevision)
                ?? RequireHeld(context!, existing!.Role.Permissions, workspace, tenantAllowsOnly: false);
        }
        if (refusal is null
            && await grants.HasLiveDenyForRoleAsync(AccessPartition.From(actor.Scope), actor.Scope.WorkspaceId, role, engine.Now, cancellationToken).ConfigureAwait(false))
            refusal = new Refusal(TicketFailureKind.Conflict,
                [$"Cannot delete custom role '{role.Value}' while referenced by an active Deny grant."],
                Code: "role-in-use");
        if (refusal is null && roleFederationMappings is not null)
        {
            var mappings = await roleFederationMappings.ListForPartitionAsync(AccessPartition.From(actor.Scope), includeDisabled: true, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < mappings.Count; i++)
            {
                var mapping = mappings[i].Mapping;
                if (mapping.RoleId == role && (mapping.Workspace is null || mapping.Workspace == actor.Scope.WorkspaceId))
                {
                    refusal = new Refusal(TicketFailureKind.Conflict,
                        [$"Role '{role.Value}' cannot be deleted while referenced by role federation mapping '{mappings[i].Id.Value}'."],
                        Code: "role-in-use");
                    break;
                }
            }
        }
        if (refusal is not null)
            return await RefuseAsync<StoredAccessRole>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await roles.DeleteAsync(actor.Scope, role, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> CreateGrantAsync(PrincipalRef actor, AccessGrant grant, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(grant);
        var change = GrantChange(AccessAuditKind.GrantCreated, actor, grant);
        var refusal = await AuthorizeGrantAsync(actor, grant, context: null, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
            return await RefuseAsync<StoredAccessGrant>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await grants.CreateAsync(grant, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> UpdateGrantAsync(PrincipalRef actor, AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(grant);
        var change = GrantChange(AccessAuditKind.GrantUpdated, actor, grant);
        var (refusal, context) = await AuthorizeTargetWithContextAsync(actor, grant, context: null, cancellationToken).ConfigureAwait(false);
        refusal ??= ValidateRevision(expectedRevision);
        if (refusal is null)
        {
            var existing = await grants.GetAsync(grant.Partition, grant.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                (refusal, context) = await AuthorizeTargetWithContextAsync(actor, existing.Grant, context, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                    refusal = SanitizeExistingTargetRefusal(refusal);
                else if (Fence(existing.Revision, expectedRevision) is { } fenced)
                    refusal = fenced;
                else if (existing.Grant.Kind == GrantKind.BreakGlass)
                    refusal = new(TicketFailureKind.Invalid, ["Break-glass grants cannot be modified; revoke them instead."]);
                else
                    refusal = await RequireHeldForLiveDenyAsync(actor, existing.Grant, context!, cancellationToken).ConfigureAwait(false);
            }
            else
                refusal = Fence(null, expectedRevision);
        }
        refusal ??= await ValidateGrantAndPermissionsAsync(actor, grant, context!, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
            return await RefuseAsync<StoredAccessGrant>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await grants.UpdateAsync(grant, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> RevokeGrantAsync(PrincipalRef actor, GrantId grant, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!grant.IsInitialized)
            throw new ArgumentException("A grant identifier is required.", nameof(grant));
        var change = new Change(AccessAuditKind.GrantRevoked, actor, Grant: grant);
        var partition = AccessPartition.From(actor.Scope);
        var (refusal, context) = await AuthorizeAnyGrantManagementAsync(actor, context: null, cancellationToken).ConfigureAwait(false);
        refusal ??= ValidateRevision(expectedRevision);
        if (refusal is null)
        {
            var existing = await grants.GetAsync(partition, grant, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                (refusal, context) = await AuthorizeTargetWithContextAsync(actor, existing.Grant, context, cancellationToken).ConfigureAwait(false);
                if (refusal is not null)
                    refusal = SanitizeExistingTargetRefusal(refusal);
                else
                {
                    change = GrantChange(AccessAuditKind.GrantRevoked, actor, existing.Grant);
                    refusal = Fence(existing.Revision, expectedRevision)
                        ?? await RequireHeldForLiveDenyAsync(actor, existing.Grant, context!, cancellationToken).ConfigureAwait(false);
                }
            }
            else
                refusal = Fence(null, expectedRevision);
        }
        if (refusal is not null)
            return await RefuseAsync<StoredAccessGrant>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await grants.DeleteAsync(partition, grant, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TicketResult<AccessChangeApplied<StoredAccessGrant>>>> RevokeGrantsAsync(PrincipalRef actor, IReadOnlyList<(GrantId Id, long ExpectedRevision)> grants, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count == 0)
            return [];
        if (grants.Count == 1)
            return [await RevokeGrantAsync(actor, grants[0].Id, grants[0].ExpectedRevision, cancellationToken).ConfigureAwait(false)];

        for (var i = 0; i < grants.Count; i++)
            if (!grants[i].Id.IsInitialized)
                throw new ArgumentException("A grant identifier is required.", nameof(grants));

        var partition = AccessPartition.From(actor.Scope);
        var results = new TicketResult<AccessChangeApplied<StoredAccessGrant>>[grants.Count];
        var toDelete = new List<(int Index, StoredAccessGrant Existing, Change Change)>(grants.Count);
        var (baseRefusal, context) = await AuthorizeAnyGrantManagementAsync(actor, context: null, cancellationToken).ConfigureAwait(false);
        Dictionary<RoleId, AccessRole?>? customRoles = null;

        for (var i = 0; i < grants.Count; i++)
        {
            var (grantId, expectedRevision) = grants[i];
            var change = new Change(AccessAuditKind.GrantRevoked, actor, Grant: grantId);
            var refusal = baseRefusal ?? ValidateRevision(expectedRevision);
            StoredAccessGrant? existing = null;
            if (refusal is null)
            {
                existing = await this.grants.GetAsync(partition, grantId, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    (refusal, context) = await AuthorizeTargetWithContextAsync(actor, existing.Grant, context, cancellationToken).ConfigureAwait(false);
                    if (refusal is not null)
                        refusal = SanitizeExistingTargetRefusal(refusal);
                    else
                    {
                        change = GrantChange(AccessAuditKind.GrantRevoked, actor, existing.Grant);
                        refusal = Fence(existing.Revision, expectedRevision)
                            ?? await RequireHeldForLiveDenyAsync(actor, existing.Grant, context!, cancellationToken, customRoles ??= []).ConfigureAwait(false);
                    }
                }
                else
                    refusal = Fence(null, expectedRevision);
            }
            if (refusal is not null)
                results[i] = await RefuseAsync<StoredAccessGrant>(change, refusal, cancellationToken).ConfigureAwait(false);
            else
                toDelete.Add((i, existing!, change));
        }

        if (toDelete.Count > 0)
        {
            var batch = new (GrantId Id, long ExpectedRevision)[toDelete.Count];
            for (var i = 0; i < toDelete.Count; i++)
                batch[i] = (toDelete[i].Existing.Grant.Id, toDelete[i].Existing.Revision);
            var removed = await this.grants.DeleteManyAsync(partition, batch, cancellationToken).ConfigureAwait(false);
            var removedById = new Dictionary<GrantId, StoredAccessGrant>(removed.Count);
            for (var i = 0; i < removed.Count; i++)
                removedById[removed[i].Grant.Id] = removed[i];

            for (var i = 0; i < toDelete.Count; i++)
            {
                var (index, existing, change) = toDelete[i];
                if (removedById.TryGetValue(existing.Grant.Id, out var deleted))
                    results[index] = await AppliedAsync(change, deleted, cancellationToken).ConfigureAwait(false);
                else
                {
                    var current = await this.grants.GetAsync(partition, existing.Grant.Id, cancellationToken).ConfigureAwait(false);
                    Refusal miss;
                    if (current is null)
                        miss = OpaqueNotFoundRefusal;
                    else
                    {
                        var (currentRefusal, _) = await AuthorizeTargetWithContextAsync(actor, current.Grant, context, cancellationToken).ConfigureAwait(false);
                        miss = currentRefusal is not null
                            ? SanitizeExistingTargetRefusal(currentRefusal)
                            : new Refusal(TicketFailureKind.Conflict, ["The record changed concurrently."], CurrentRevision: current.Revision);
                    }
                    results[index] = await RefuseAsync<StoredAccessGrant>(change, miss, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return results;
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessFieldPolicy>>> CreateFieldPolicyAsync(PrincipalRef actor, AccessFieldPolicy policy, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        var change = new Change(AccessAuditKind.FieldPolicyCreated, actor, Field: policy.Field);
        var refusal = await AuthorizeAsync(actor, AccessPermissions.AccessManage, new AccessResource(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidatePolicy(policy);
        if (refusal is not null)
            return await RefuseAsync<StoredAccessFieldPolicy>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await policies.CreateAsync(actor.Scope, policy, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessFieldPolicy>>> UpdateFieldPolicyAsync(PrincipalRef actor, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        var change = new Change(AccessAuditKind.FieldPolicyUpdated, actor, Field: policy.Field);
        var refusal = await AuthorizeAsync(actor, AccessPermissions.AccessManage, new AccessResource(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidatePolicy(policy) ?? ValidateRevision(expectedRevision);
        if (refusal is not null)
            return await RefuseAsync<StoredAccessFieldPolicy>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await policies.UpdateAsync(actor.Scope, policy, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessFieldPolicy>>> DeleteFieldPolicyAsync(PrincipalRef actor, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!field.IsInitialized)
            throw new ArgumentException("A field path is required.", nameof(field));
        var change = new Change(AccessAuditKind.FieldPolicyDeleted, actor, Field: field);
        var refusal = await AuthorizeAsync(actor, AccessPermissions.AccessManage, new AccessResource(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refusal is not null)
            return await RefuseAsync<StoredAccessFieldPolicy>(change, refusal, cancellationToken).ConfigureAwait(false);
        return await CompleteAsync(change, await policies.DeleteAsync(actor.Scope, field, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> ActivateBreakGlassAsync(BreakGlassRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = request.Requester;
        var now = engine.Now;
        var target = request.Scope is TenantGrantScope ? null : AccessEngine.Target(actor.Scope, request.Scope);
        var change = new Change(AccessAuditKind.BreakGlassActivated, actor, Grant: request.Id, Role: request.Role, Resource: target,
            Justification: SafeJustification(request.Justification));
        GrantSubject? subject = actor switch
        {
            UserPrincipal user => user.UserId,
            ApiClientPrincipal client => client.ClientId,
            AutomationPrincipal => null,
            SystemPrincipal => null,
        };

        var errors = new List<string>();
        if (subject is null)
            errors.Add("Only users and API clients can activate break-glass access.");
        if (target is null || AccessEngine.WorkspaceOf(request.Scope) != actor.Scope.WorkspaceId)
            errors.Add("Break-glass grants must target the requester's workspace, one of its teams or one of its inboxes.");
        if (change.Justification is null || change.Justification.Trim().Length < options.MinimumBreakGlassJustificationLength)
            errors.Add($"A justification of at least {options.MinimumBreakGlassJustificationLength} characters is required.");
        if (request.Duration <= TimeSpan.Zero || request.Duration > options.MaximumBreakGlassDuration)
            errors.Add("The break-glass duration must be positive and within the configured maximum.");

        var refusal = errors.Count > 0 ? new Refusal(TicketFailureKind.Invalid, errors) : null;
        if (refusal is null)
        {
            var (saDenial, _) = await engine.CheckServiceAccountForGrantScopeAsync(actor, actor.Scope, request.Scope, cancellationToken).ConfigureAwait(false);
            if (saDenial is { } saReason)
                refusal = new Refusal(TicketFailureKind.Forbidden, ["The actor is not authorized."], saReason);
        }
        refusal ??= await AuthorizeAsync(actor, AccessPermissions.AccessBreakGlass, target!, cancellationToken).ConfigureAwait(false);
        if (refusal is null)
        {
            var role = BuiltInRoles.Find(request.Role) ?? (await roles.GetAsync(actor.Scope, request.Role, cancellationToken).ConfigureAwait(false))?.Role;
            if (role is null)
                refusal = new Refusal(TicketFailureKind.Invalid, ["The break-glass role does not exist."]);
            else if (role.IncludesAllPermissions || role.Permissions.Overlaps(ManagementPermissions))
                refusal = new Refusal(TicketFailureKind.Invalid, ["Break-glass roles cannot include access-management or break-glass permissions."]);
        }
        if (refusal is not null)
            return await RefuseAsync<StoredAccessGrant>(change, refusal, cancellationToken).ConfigureAwait(false);

        var grant = new AccessGrant(request.Id, AccessPartition.From(actor.Scope), subject!.Value, request.Role, request.Scope, GrantEffect.Allow, null,
            now, now + request.Duration, change.Justification, GrantKind.BreakGlass);
        return await CompleteAsync(change, await grants.CreateAsync(grant, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maximum conferrals or grants in one check.</summary>
    public const int MaximumCheckedGrants = 4096;

    public async Task<AccessCheckResult> CheckCanConferAsync(PrincipalRef actor, IEnumerable<GrantConferral> conferrals, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var items = Bounded(conferrals, nameof(conferrals));
        if (engine.Precheck(actor, AccessPermissions.AccessManage, actor.Scope) is { } reason)
            return Refused(["The actor is not authorized."], reason);
        var (serviceAccountDenial, serviceAccount) = await engine.CheckServiceAccountAsync(actor, actor.Scope, resource: null, cancellationToken).ConfigureAwait(false);
        if (serviceAccountDenial is { } saReason)
            return Refused(["The actor is not authorized."], saReason);
        var now = engine.Now;
        AccessContext? context = null;
        Dictionary<RoleId, AccessRole>? custom = null;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        Dictionary<(Permission, AccessResource, bool), bool>? decisionCache = null;
        foreach (var conferral in items)
        {
            var grant = conferral.Grant;
            if (grant.Effect != GrantEffect.Allow || grant.ValidUntil is { } until && until <= now)
                continue;
            var target = conferral.Target ?? AccessEngine.Target(actor.Scope, grant.Scope);
            if (!InActorScope(actor, grant) || target.Scope != actor.Scope)
                return Refused(["The grant is outside the actor's scope."], AccessDenialReason.ScopeMismatch);
            if (serviceAccount is not null
                && (!AccessEngine.ScopeContains(serviceAccount.Account.Scope, grant.Scope) || !serviceAccount.Account.AppliesTo(target)))
                return Refused(["The grant is outside the service account's scope."], AccessDenialReason.ScopeMismatch);
            AccessRole? role = BuiltInRoles.Find(grant.Role);
            if (role is null && grant.Scope is not TenantGrantScope)
            {
                if (custom is null)
                {
                    custom = [];
                    foreach (var stored in await roles.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false))
                        custom[stored.Role.Id] = stored.Role;
                }
                role = custom.GetValueOrDefault(grant.Role);
            }
            if (role is null)
                continue;
            context ??= await engine.LoadAsync(actor, actor.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);
            var tenantOnly = grant.Scope is TenantGrantScope;
            if (items.Count > 1)
                decisionCache ??= [];
            if (role.IncludesAllPermissions)
            {
                var defs = engine.Catalog.Definitions;
                for (var i = 0; i < defs.Count; i++)
                    CheckPermission(defs[i].Permission, context, target, tenantOnly, missing, decisionCache);
            }
            else
            {
                foreach (var permission in role.Permissions)
                    CheckPermission(permission, context, target, tenantOnly, missing, decisionCache);
            }
        }
        return missing.Count == 0
            ? AccessCheckPassed.Instance
            : Refused([.. missing.Select(permission => $"The actor does not hold '{permission}' on the target.")], AccessDenialReason.NoMatchingGrant);
    }

    private static void CheckPermission(Permission permission, AccessContext context, AccessResource target, bool tenantOnly, SortedSet<string> missing, Dictionary<(Permission, AccessResource, bool), bool>? cache)
    {
        if (missing.Contains(permission.Value))
            return;
        if (cache is not null)
        {
            var key = (permission, target, tenantOnly);
            if (!cache.TryGetValue(key, out var allowed))
                cache[key] = allowed = AccessEngine.EvaluateSimple(context, permission, target, includeBreakGlass: false, tenantAllowsOnly: tenantOnly, conservativeConditions: true).Allowed;
            if (!allowed)
                missing.Add(permission.Value);
            return;
        }
        if (!AccessEngine.EvaluateSimple(context, permission, target, includeBreakGlass: false, tenantAllowsOnly: tenantOnly, conservativeConditions: true).Allowed)
            missing.Add(permission.Value);
    }

    public async Task<AccessCheckResult> CheckCanRevokeAsync(PrincipalRef actor, IEnumerable<AccessGrant> grants, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var items = Bounded(grants, nameof(grants));
        if (items.Count == 0)
            return AccessCheckPassed.Instance;
        if (engine.Precheck(actor, AccessPermissions.AccessManage, actor.Scope) is { } reason)
            return Refused(["The actor is not authorized."], reason);
        var (serviceAccountDenial, serviceAccount) = await engine.CheckServiceAccountAsync(actor, actor.Scope, resource: null, cancellationToken).ConfigureAwait(false);
        if (serviceAccountDenial is { } saReason)
            return Refused(["The actor is not authorized."], saReason);
        var context = await engine.LoadAsync(actor, actor.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);
        Dictionary<RoleId, AccessRole?>? customRoles = null;
        for (var i = 0; i < items.Count; i++)
        {
            var grant = items[i];
            if (!InActorScope(actor, grant))
                return Refused(["The grant is outside the actor's scope."], AccessDenialReason.ScopeMismatch);
            if (serviceAccount is not null && !AccessEngine.ScopeContains(serviceAccount.Account.Scope, grant.Scope))
                return Refused(["The grant is outside the service account's scope."], AccessDenialReason.ScopeMismatch);
            var tenant = grant.Scope is TenantGrantScope;
            var permission = tenant ? AccessPermissions.AccessManageTenant : AccessPermissions.AccessManage;
            var result = AccessEngine.EvaluateSimple(context, permission, AccessEngine.Target(actor.Scope, grant.Scope), includeBreakGlass: false, tenantAllowsOnly: tenant);
            if (result.Denial is { } denial)
                return Refused([$"Permission '{permission}' is required on the grant's target."], denial);
            if (grant.Effect == GrantEffect.Deny
                && await RequireHeldForLiveDenyAsync(actor, grant, context, cancellationToken, customRoles ??= []).ConfigureAwait(false) is { } denyRefusal)
                return Refused(denyRefusal.Errors, denyRefusal.Denial ?? AccessDenialReason.NoMatchingGrant);
        }
        return AccessCheckPassed.Instance;
    }

    private static List<T> Bounded<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var items = new List<T>();
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value, parameterName);
            items.Add(value);
            if (items.Count > MaximumCheckedGrants)
                throw new ArgumentException($"At most {MaximumCheckedGrants} grants can be checked at once.", parameterName);
        }
        return items;
    }

    private static bool InActorScope(PrincipalRef actor, AccessGrant grant) =>
        grant.Partition == AccessPartition.From(actor.Scope) && (grant.Workspace is not { } workspace || workspace == actor.Scope.WorkspaceId);

    public async Task<TicketResult<IReadOnlyList<AccessRoleListing>>> ListRolesAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (await AuthorizeAsync(actor, AccessPermissions.AccessManage, new AccessResource(actor.Scope), cancellationToken).ConfigureAwait(false) is { } refusal)
            return new TicketFailure(refusal.Reason, refusal.Errors);
        var custom = await roles.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false);
        return new TicketValue<IReadOnlyList<AccessRoleListing>>([.. BuiltInRoles.All.Select(role => new AccessRoleListing(role, null)),
            .. custom.Select(stored => new AccessRoleListing(stored.Role, stored.Revision))]);
    }

    public async Task<TicketResult<IReadOnlyList<StoredAccessGrant>>> ListGrantsAsync(PrincipalRef actor, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AccessLimits.MaximumTargetPage);
        if (target is TenantGrantScope)
            return new TicketFailure(TicketFailureKind.Invalid, ["Tenant grants are not listed by target."]);
        AccessGrantTargets.Validate(target, limit, after);
        if (target is WorkspaceGrantScope workspace && workspace.Workspace != actor.Scope.WorkspaceId
            || target is TeamGrantScope team && team.Workspace != actor.Scope.WorkspaceId
            || target is InboxGrantScope inbox && inbox.Workspace != actor.Scope.WorkspaceId)
            return new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."]);
        var (saDenial, _) = await engine.CheckServiceAccountForGrantScopeAsync(actor, actor.Scope, target, cancellationToken).ConfigureAwait(false);
        if (saDenial is not null)
            return new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."]);
        if (await AuthorizeAsync(actor, AccessPermissions.AccessManage, AccessEngine.Target(actor.Scope, target), cancellationToken).ConfigureAwait(false) is { } refusal)
            return new TicketFailure(refusal.Reason, refusal.Errors);
        return new TicketValue<IReadOnlyList<StoredAccessGrant>>(await grants.ListForTargetAsync(AccessPartition.From(actor.Scope), target, limit, after, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<IReadOnlyList<StoredAccessFieldPolicy>>> ListFieldPoliciesAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (await AuthorizeAsync(actor, AccessPermissions.AccessManage, new AccessResource(actor.Scope), cancellationToken).ConfigureAwait(false) is { } refusal)
            return new TicketFailure(refusal.Reason, refusal.Errors);
        return new TicketValue<IReadOnlyList<StoredAccessFieldPolicy>>(await policies.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AccessCheckResult> CheckCanManageTenantAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var (saDenial, _) = await engine.CheckServiceAccountForGrantScopeAsync(actor, actor.Scope, TenantGrantScope.Instance, cancellationToken).ConfigureAwait(false);
        if (saDenial is { } saReason)
            return Refused(["The actor is not authorized."], saReason);
        var refusal = await AuthorizeAsync(actor, AccessPermissions.AccessManageTenant, new AccessResource(actor.Scope), cancellationToken, tenantAllowsOnly: true).ConfigureAwait(false);
        return refusal is null ? AccessCheckPassed.Instance : new TicketFailure(refusal.Reason, refusal.Errors);
    }

    public async Task<TicketResult<IReadOnlyList<StoredAccessGrant>>> ListTenantGrantsForSubjectsAsync(PrincipalRef actor, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count > AccessLimits.MaximumSubjectsPerQuery)
            throw new ArgumentException($"At most {AccessLimits.MaximumSubjectsPerQuery} subjects can be queried.", nameof(subjects));
        var (saDenial, _) = await engine.CheckServiceAccountForGrantScopeAsync(actor, actor.Scope, TenantGrantScope.Instance, cancellationToken).ConfigureAwait(false);
        if (saDenial is not null)
            return new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."]);
        if (await AuthorizeAsync(actor, AccessPermissions.AccessManageTenant, new AccessResource(actor.Scope), cancellationToken, tenantAllowsOnly: true).ConfigureAwait(false) is { } refusal)
            return new TicketFailure(refusal.Reason, refusal.Errors);
        if (subjects.Count == 0)
            return new TicketValue<IReadOnlyList<StoredAccessGrant>>([]);
        var listed = await grants.ListForSubjectsAsync(AccessPartition.From(actor.Scope), actor.Scope.WorkspaceId, subjects, cancellationToken).ConfigureAwait(false);
        var tenantGrants = new List<StoredAccessGrant>();
        for (var i = 0; i < listed.Count; i++)
            if (listed[i].Grant.Scope is TenantGrantScope)
                tenantGrants.Add(listed[i]);
        return new TicketValue<IReadOnlyList<StoredAccessGrant>>(tenantGrants);
    }

    private static TicketFailure Refused(IReadOnlyList<string> errors, AccessDenialReason reason) =>
        new(TicketFailureKind.Forbidden, errors);

    private static readonly Permission[] ManagementPermissions =
        [AccessPermissions.AccessManage, AccessPermissions.AccessManageTenant, AccessPermissions.AccessBreakGlass];

    private sealed record Refusal(TicketFailureKind Reason, IReadOnlyList<string> Errors, AccessDenialReason? Denial = null, long? CurrentRevision = null, string? Code = null);

    private static readonly Refusal OpaqueNotFoundRefusal = new(TicketFailureKind.NotFound, ["The record does not exist."]);

    private static Refusal SanitizeExistingTargetRefusal(Refusal refusal) =>
        refusal.Reason == TicketFailureKind.Forbidden || string.Equals(refusal.Code, "out-of-scope", StringComparison.Ordinal)
            ? OpaqueNotFoundRefusal
            : refusal;

    /// <summary>Refuses before any authorization decision is based on a record that is missing or at another revision.</summary>
    private static Refusal? Fence(long? currentRevision, long expectedRevision) => currentRevision switch
    {
        null => OpaqueNotFoundRefusal,
        { } current when current != expectedRevision => new(TicketFailureKind.Conflict, ["The record changed concurrently."], CurrentRevision: current),
        _ => null,
    };

    private sealed record Change(
        AccessAuditKind Kind,
        PrincipalRef Actor,
        GrantId? Grant = null,
        RoleId? Role = null,
        FieldPath? Field = null,
        AccessResource? Resource = null,
        string? Justification = null);

    private static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    private static string? SafeJustification(string justification) =>
        AccessGrant.IsValidJustification(justification) ? justification : null;

    private static Change GrantChange(AccessAuditKind kind, PrincipalRef actor, AccessGrant grant) =>
        new(kind, actor, Grant: grant.Id, Role: grant.Role,
            Resource: InActorScope(actor, grant) && grant.Workspace == actor.Scope.WorkspaceId ? AccessEngine.Target(actor.Scope, grant.Scope) : null,
            Justification: grant.Justification);

    private static Refusal? ValidateRevision(long expectedRevision) =>
        expectedRevision < 1 ? new(TicketFailureKind.Invalid, ["The expected revision must be at least 1."]) : null;

    private Refusal? ValidateRole(AccessRole role)
    {
        if (role.IsBuiltIn)
            return new(TicketFailureKind.Rejected, ["Built-in roles are immutable."]);
        var unknown = role.Permissions.Where(permission => !engine.Catalog.Contains(permission)).Order().ToList();
        return unknown.Count == 0 ? null : new(TicketFailureKind.Invalid, [.. unknown.Select(permission => $"Unknown permission '{permission}'.")]);
    }

    private Refusal? ValidatePolicy(AccessFieldPolicy policy)
    {
        var referenced = policy.RevealPermission is { } reveal ? policy.AppliesTo.Append(reveal) : policy.AppliesTo;
        var unknown = referenced.Where(permission => !engine.Catalog.Contains(permission)).Distinct().Order().ToList();
        return unknown.Count == 0 ? null : new(TicketFailureKind.Invalid, [.. unknown.Select(permission => $"Unknown permission '{permission}'.")]);
    }

    /// <remarks>With <c>tenantAllowsOnly</c>, only tenant-scoped allow grants count (tenant administration authority).</remarks>
    private async Task<Refusal?> AuthorizeAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken, bool tenantAllowsOnly = false) =>
        (await AuthorizeWithContextAsync(actor, permission, resource, cancellationToken, tenantAllowsOnly).ConfigureAwait(false)).Refusal;

    private async Task<(Refusal? Refusal, AccessContext? Context)> AuthorizeWithContextAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken, bool tenantAllowsOnly = false, AccessContext? context = null)
    {
        if (engine.Precheck(actor, permission, resource.Scope) is { } reason)
            return (new(TicketFailureKind.Forbidden, ["The actor is not authorized."], reason), context);
        var (serviceAccountDenial, _) = await engine.CheckServiceAccountAsync(actor, resource.Scope, resource, cancellationToken).ConfigureAwait(false);
        if (serviceAccountDenial is { } saReason)
            return (new(TicketFailureKind.Forbidden, ["The actor is not authorized."], saReason), context);
        context ??= await engine.LoadAsync(actor, resource.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);
        var result = AccessEngine.EvaluateSimple(context, permission, resource, includeBreakGlass: false, tenantAllowsOnly);
        return (result.Denial is { } denial ? new(TicketFailureKind.Forbidden, ["The actor is not authorized."], denial) : null, context);
    }

    /// <summary>No escalation: the actor must already hold every listed permission on the target from standard (never break-glass) grants.</summary>
    private static Refusal? RequireHeld(AccessContext context, IEnumerable<Permission> permissions, AccessResource target, bool tenantAllowsOnly)
    {
        HashSet<Permission>? checkedPerms = null;
        List<Permission>? missing = null;
        foreach (var permission in permissions)
        {
            if (checkedPerms is not null && !checkedPerms.Add(permission))
                continue;
            checkedPerms ??= [permission];
            if (!AccessEngine.EvaluateSimple(context, permission, target, includeBreakGlass: false, tenantAllowsOnly, conservativeConditions: true).Allowed)
                (missing ??= []).Add(permission);
        }
        if (missing is null)
            return null;
        missing.Sort();
        var errors = new string[missing.Count];
        for (var i = 0; i < missing.Count; i++)
            errors[i] = $"The actor does not hold '{missing[i]}' on the target.";
        return new(TicketFailureKind.Forbidden, errors, AccessDenialReason.NoMatchingGrant);
    }

    /// <summary>
    /// No escalation via lifting a deny: revoking or modifying a live <see cref="GrantEffect.Deny"/> grant requires the actor to hold every
    /// denied permission on the grant's target from standard grants (falling back to all catalog permissions when the role is missing or
    /// includes all permissions).
    /// </summary>
    private async Task<Refusal?> RequireHeldForLiveDenyAsync(
        PrincipalRef actor,
        AccessGrant grant,
        AccessContext context,
        CancellationToken cancellationToken,
        Dictionary<RoleId, AccessRole?>? customRoleCache = null)
    {
        if (grant.Effect != GrantEffect.Deny || (grant.ValidUntil is { } until && until <= engine.Now))
            return null;

        var role = BuiltInRoles.Find(grant.Role);
        if (role is null && grant.Scope is not TenantGrantScope)
        {
            if (customRoleCache is not null)
            {
                if (!customRoleCache.TryGetValue(grant.Role, out role))
                    customRoleCache[grant.Role] = role = (await roles.GetAsync(actor.Scope, grant.Role, cancellationToken).ConfigureAwait(false))?.Role;
            }
            else
                role = (await roles.GetAsync(actor.Scope, grant.Role, cancellationToken).ConfigureAwait(false))?.Role;
        }

        var deniedPermissions = role is null || role.IncludesAllPermissions
            ? engine.Catalog.Definitions.Select(static definition => definition.Permission)
            : role.Permissions;
        return RequireHeld(context, deniedPermissions, AccessEngine.Target(actor.Scope, grant.Scope), grant.Scope is TenantGrantScope);
    }

    /// <summary>
    /// Pre-authorizes that the actor holds at least one standard administrative grant in its scope before probing grant existence.
    /// </summary>
    private async Task<(Refusal? Refusal, AccessContext? Context)> AuthorizeAnyGrantManagementAsync(PrincipalRef actor, AccessContext? context, CancellationToken cancellationToken)
    {
        if (engine.Precheck(actor, AccessPermissions.AccessManage, actor.Scope) is { } reason)
            return (new(TicketFailureKind.Forbidden, ["The actor is not authorized."], reason), context);
        var (serviceAccountDenial, _) = await engine.CheckServiceAccountAsync(actor, actor.Scope, resource: null, cancellationToken).ConfigureAwait(false);
        if (serviceAccountDenial is { } saReason)
            return (new(TicketFailureKind.Forbidden, ["The actor is not authorized."], saReason), context);
        context ??= await engine.LoadAsync(actor, actor.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);
        var loadedGrants = context.Grants;
        for (var i = 0; i < loadedGrants.Count; i++)
        {
            var candidate = loadedGrants[i];
            var grant = candidate.Grant;
            if (grant.Effect != GrantEffect.Allow || grant.Kind == GrantKind.BreakGlass)
                continue;
            if (candidate.Includes(AccessPermissions.AccessManage)
                || (grant.Scope is TenantGrantScope && candidate.Includes(AccessPermissions.AccessManageTenant)))
                return (null, context);
        }
        return (new(TicketFailureKind.Forbidden, ["The actor is not authorized."], AccessDenialReason.NoMatchingGrant), context);
    }

    /// <summary>
    /// Authorizes management of a grant's target. Tenant grants need <c>access:manage-tenant</c> from a tenant-scoped grant; a
    /// workspace-scoped Owner cannot manage tenant grants.
    /// </summary>
    private async Task<(Refusal? Refusal, AccessContext? Context)> AuthorizeTargetWithContextAsync(PrincipalRef actor, AccessGrant grant, AccessContext? context, CancellationToken cancellationToken)
    {
        if (grant.Partition != AccessPartition.From(actor.Scope) || (grant.Workspace is { } workspace && workspace != actor.Scope.WorkspaceId))
            return (new(TicketFailureKind.Forbidden, ["The actor is not authorized."], AccessDenialReason.ScopeMismatch), context);
        var (saDenial, _) = await engine.CheckServiceAccountForGrantScopeAsync(actor, actor.Scope, grant.Scope, cancellationToken).ConfigureAwait(false);
        if (saDenial is { } saReason)
            return (new(TicketFailureKind.Forbidden, ["The actor is not authorized."], saReason), context);
        var tenant = grant.Scope is TenantGrantScope;
        var permission = tenant ? AccessPermissions.AccessManageTenant : AccessPermissions.AccessManage;
        return await AuthorizeWithContextAsync(actor, permission, AccessEngine.Target(actor.Scope, grant.Scope), cancellationToken, tenantAllowsOnly: tenant, context: context).ConfigureAwait(false);
    }

    private async Task<Refusal?> AuthorizeGrantAsync(PrincipalRef actor, AccessGrant grant, AccessContext? context, CancellationToken cancellationToken)
    {
        var (refusal, loadedContext) = await AuthorizeTargetWithContextAsync(actor, grant, context, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
            return refusal;
        return await ValidateGrantAndPermissionsAsync(actor, grant, loadedContext!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Refusal?> ValidateGrantAndPermissionsAsync(PrincipalRef actor, AccessGrant grant, AccessContext loadedContext, CancellationToken cancellationToken)
    {
        if (grant.Kind == GrantKind.BreakGlass)
            return new(TicketFailureKind.Invalid, ["Break-glass grants are created only through break-glass activation."]);

        var role = BuiltInRoles.Find(grant.Role) ?? (await roles.GetAsync(actor.Scope, grant.Role, cancellationToken).ConfigureAwait(false))?.Role;
        if (AccessGrantValidator.Validate(grant, role, engine.Now) is GrantInvalid invalid)
            return new(TicketFailureKind.Invalid, invalid.Errors);
        if (grant.Effect == GrantEffect.Deny)
            return null;
        var granted = role!.IncludesAllPermissions ? engine.Catalog.Definitions.Select(definition => definition.Permission) : role.Permissions;
        return RequireHeld(loadedContext, granted, AccessEngine.Target(actor.Scope, grant.Scope), grant.Scope is TenantGrantScope);
    }

    private async Task<TicketResult<AccessChangeApplied<T>>> RefuseAsync<T>(Change change, Refusal refusal, CancellationToken cancellationToken, long? currentRevision = null)
    {
        await RecordAsync(change, AccessAuditOutcome.Denied, refusal.Denial, cancellationToken).ConfigureAwait(false);
        return new TicketFailure(refusal.Reason, refusal.Errors, refusal.CurrentRevision ?? currentRevision, refusal.Code);
    }

    private Task<TicketResult<AccessChangeApplied<T>>> CompleteAsync<T>(Change change, StoreWriteResult<T, AccessWriteRejection> result, CancellationToken cancellationToken) => result switch
    {
        StoreApplied<T> applied => AppliedAsync(change, applied.Value, cancellationToken),
        StoreNotFound => RefuseAsync<T>(change, new(TicketFailureKind.NotFound, ["The record does not exist."]), cancellationToken),
        StoreConflict conflict => RefuseAsync<T>(change, new(TicketFailureKind.Conflict, ["The record changed concurrently."]), cancellationToken, conflict.CurrentRevision),
        StoreRejected<AccessWriteRejection> rejected => RefuseAsync<T>(change, new(TicketFailureKind.Rejected, [$"The store rejected the write: {rejected.Reason}."]), cancellationToken),
    };

    private async Task<TicketResult<AccessChangeApplied<T>>> AppliedAsync<T>(Change change, T value, CancellationToken cancellationToken)
    {
        var auditEvent = await RecordAsync(change, AccessAuditOutcome.Allowed, null, cancellationToken).ConfigureAwait(false);
        return new TicketValue<AccessChangeApplied<T>>(new AccessChangeApplied<T>(value, auditEvent.Id));
    }

    private async Task<AccessAuditEvent> RecordAsync(Change change, AccessAuditOutcome outcome, AccessDenialReason? denial, CancellationToken cancellationToken)
    {
        var permission = change.Kind == AccessAuditKind.BreakGlassActivated ? AccessPermissions.AccessBreakGlass : AccessPermissions.AccessManage;
        var auditEvent = AccessEngine.AuditEvent(engine.Now, change.Actor, change.Kind, outcome, permission, change.Resource, change.Grant, change.Role,
            change.Field, usedBreakGlass: change.Kind == AccessAuditKind.BreakGlassActivated && outcome == AccessAuditOutcome.Allowed, denial, change.Justification);
        await audit.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return auditEvent;
    }
}
