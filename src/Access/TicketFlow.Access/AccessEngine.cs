using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>A valid grant with its resolved role; <see cref="Role"/> is null when the role cannot be resolved.</summary>
internal sealed record ResolvedGrant(
    AccessGrant Grant,
    AccessRole? Role,
    IReadOnlySet<Permission>? EffectivePermissions = null,
    bool IsRoleStripped = false)
{
    public bool Includes(Permission permission) =>
        EffectivePermissions is not null
            ? EffectivePermissions.Contains(permission)
            : Role?.Includes(permission) == true;
}

/// <summary>Grants loaded once for one principal in one workspace scope at one instant.</summary>
internal sealed class AccessContext(PrincipalRef principal, TicketScope scope, DateTimeOffset now, IReadOnlyList<ResolvedGrant> grants)
{
    public PrincipalRef Principal { get; } = principal;
    public UserPrincipal? User { get; } = principal as UserPrincipal;
    public TicketScope Scope { get; } = scope;
    public DateTimeOffset Now { get; } = now;
    public IReadOnlyList<ResolvedGrant> Grants { get; } = grants;
}

/// <summary>Result of the core point evaluation. Standard and break-glass allows are kept apart so callers can tell when break-glass changed an outcome.</summary>
internal readonly struct PointResult(AccessDenialReason? denial, IReadOnlyList<ResolvedGrant> standardAllows, IReadOnlyList<ResolvedGrant> breakGlassAllows)
{
    public AccessDenialReason? Denial { get; } = denial;
    public IReadOnlyList<ResolvedGrant> StandardAllows { get; } = standardAllows;
    public IReadOnlyList<ResolvedGrant> BreakGlassAllows { get; } = breakGlassAllows;

    public bool Allowed => Denial is null;

    /// <summary>Allowed only because of a break-glass grant.</summary>
    public bool UsedBreakGlass => Allowed && StandardAllows.Count == 0;

    public static PointResult Deny(AccessDenialReason reason) => new(reason, [], []);
}

/// <summary>Allocation-free point evaluation result when callers do not need the matched grant lists.</summary>
internal readonly struct SimplePointResult(AccessDenialReason? denial, bool usedBreakGlass)
{
    public AccessDenialReason? Denial { get; } = denial;
    public bool Allowed => Denial is null;
    public bool UsedBreakGlass { get; } = usedBreakGlass;

    public static SimplePointResult Deny(AccessDenialReason reason) => new(reason, false);
}

/// <summary>
/// The shared decision core. Rules: the principal's scope must equal the resource scope; only valid grants for the principal's
/// subjects that are tenant-wide or name the workspace are considered; any applicable deny whose role includes the permission
/// (or whose role cannot be resolved) and whose condition is not false denies; otherwise an applicable allow whose role includes
/// the permission and whose condition is true allows.
/// </summary>
internal sealed class AccessEngine(
    IPermissionCatalog catalog,
    IAccessGrantStore grants,
    IAccessRoleStore roles,
    TimeProvider time,
    AccessControlOptions? options = null,
    IServiceAccountStore? serviceAccounts = null,
    IRoleFederationMappingStore? roleFederationMappings = null)
{
    private readonly AccessControlOptions options = options ?? new AccessControlOptions();

    public IPermissionCatalog Catalog { get; } = catalog;

    public AccessControlOptions Options => options;

    public DateTimeOffset Now => time.GetUtcNow();

    public async ValueTask<(AccessDenialReason? Denial, StoredServiceAccount? ServiceAccount)> CheckServiceAccountAsync(
        PrincipalRef principal,
        TicketScope scope,
        AccessResource? resource,
        CancellationToken cancellationToken)
    {
        if (principal is not ApiClientPrincipal client || serviceAccounts is null)
            return (null, null);

        var stored = await serviceAccounts.FindAsync(AccessPartition.From(scope), client.ClientId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return (null, null);

        if (!stored.IsEnabled)
            return (AccessDenialReason.ServiceAccountDisabled, stored);

        var withinScope = resource is not null
            ? stored.Account.AppliesTo(resource)
            : stored.Account.AppliesTo(scope);
        return withinScope ? (null, stored) : (AccessDenialReason.ScopeMismatch, stored);
    }

    public async ValueTask<(AccessDenialReason? Denial, StoredServiceAccount? ServiceAccount)> CheckServiceAccountForGrantScopeAsync(
        PrincipalRef principal,
        TicketScope scope,
        GrantScope targetGrantScope,
        CancellationToken cancellationToken)
    {
        if (principal is not ApiClientPrincipal client || serviceAccounts is null)
            return (null, null);

        var partition = AccessPartition.From(scope);
        var stored = await serviceAccounts.FindAsync(partition, client.ClientId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return (null, null);

        if (!stored.IsEnabled)
            return (AccessDenialReason.ServiceAccountDisabled, stored);

        if (stored.Account.Partition != partition || !ScopeContains(stored.Account.Scope, targetGrantScope))
            return (AccessDenialReason.ScopeMismatch, stored);

        return (null, stored);
    }

    public static bool ScopeContains(GrantScope container, GrantScope candidate) => container switch
    {
        TenantGrantScope => true,
        WorkspaceGrantScope ws => candidate switch
        {
            WorkspaceGrantScope c => c.Workspace == ws.Workspace,
            TeamGrantScope c => c.Workspace == ws.Workspace,
            InboxGrantScope c => c.Workspace == ws.Workspace,
            TenantGrantScope => false,
        },
        TeamGrantScope team => candidate is TeamGrantScope c && c.Workspace == team.Workspace && c.Team == team.Team,
        InboxGrantScope inbox => candidate is InboxGrantScope c && c.Workspace == inbox.Workspace && c.Inbox == inbox.Inbox,
    };

    public static bool IsAdministrativePermission(Permission permission) =>
        permission == AccessPermissions.AccessManage
        || permission == AccessPermissions.AccessManageTenant
        || permission == AccessPermissions.AccessBreakGlass;

    public static bool RequiresTenantAllowOnly(Permission permission) =>
        permission == AccessPermissions.AccessManageTenant;

    public AccessDenialReason? Precheck(PrincipalRef principal, Permission permission, TicketScope scope) =>
        Precheck(principal, Catalog.Contains(permission), scope);

    public static AccessDenialReason? Precheck(PrincipalRef principal, bool knownPermission, TicketScope scope)
    {
        if (!knownPermission)
            return AccessDenialReason.UnknownPermission;
        if (principal is not (UserPrincipal or ApiClientPrincipal))
            return AccessDenialReason.UnsupportedPrincipal;
        return principal.Scope == scope ? null : AccessDenialReason.ScopeMismatch;
    }

    public static IReadOnlyList<GrantSubject>? Subjects(PrincipalRef principal) => principal switch
    {
        UserPrincipal user => BuildUserSubjects(user),
        ApiClientPrincipal client => [(GrantSubject)client.ClientId],
        AutomationPrincipal => null,
        SystemPrincipal => null,
    };

    private static GrantSubject[] BuildUserSubjects(UserPrincipal user)
    {
        var result = new GrantSubject[user.Teams.Count + 1];
        result[0] = user.UserId;
        var index = 1;
        foreach (var team in user.Teams)
            result[index++] = team;
        return result;
    }

    private static bool SubjectMatches(PrincipalRef principal, GrantSubject subject) => principal switch
    {
        UserPrincipal user => subject switch
        {
            UserId id => id == user.UserId,
            TeamId team => user.Teams.Contains(team),
            ApiClientId => false,
        },
        ApiClientPrincipal client => subject switch
        {
            ApiClientId id => id == client.ClientId,
            UserId => false,
            TeamId => false,
        },
        AutomationPrincipal => false,
        SystemPrincipal => false,
    };

    public async Task<AccessContext> LoadAsync(PrincipalRef principal, TicketScope scope, CancellationToken cancellationToken, bool forAdministration = false)
    {
        var subjects = Subjects(principal) ?? throw new InvalidOperationException("The principal cannot hold grants.");
        var now = time.GetUtcNow();
        var partition = AccessPartition.From(scope);
        var stored = await grants.ListForSubjectsAsync(partition, scope.WorkspaceId, subjects, cancellationToken).ConfigureAwait(false);
        var ignoreLocalAllows = options.RoleFederation.Mode == RoleFederationMode.FederatedOnly;
        var preserveLocalAdminOnly = ignoreLocalAllows && !options.RoleFederation.AllowAdministrativeRoles;
        var valid = new List<AccessGrant>(stored.Count);
        RoleId? singleCustomRole = null;
        var multipleCustomRoles = false;
        for (var i = 0; i < stored.Count; i++)
        {
            var grant = stored[i].Grant;
            // A store returning grants outside the requested partition, workspace or subjects violates its contract; fail closed.
            if (grant.Partition != partition || (grant.Workspace is { } workspace && workspace != scope.WorkspaceId) || !SubjectMatches(principal, grant.Subject))
                throw new InvalidOperationException("The grant store returned a grant outside the requested scope or subjects.");
            if (!grant.IsValidAt(now))
                continue;
            if (ignoreLocalAllows && grant.Effect == GrantEffect.Allow && grant.Kind != GrantKind.BreakGlass && !preserveLocalAdminOnly)
                continue;
            valid.Add(grant);
            if (!multipleCustomRoles && grant.Scope is not TenantGrantScope && !BuiltInRoles.IsBuiltIn(grant.Role))
            {
                if (singleCustomRole is null)
                    singleCustomRole = grant.Role;
                else if (singleCustomRole.Value != grant.Role)
                    multipleCustomRoles = true;
            }
        }

        // Custom roles are resolved with one store round trip per load. Tenant grants never resolve custom roles.
        AccessRole? resolvedSingleCustom = null;
        Dictionary<RoleId, AccessRole>? custom = null;
        if (multipleCustomRoles)
        {
            var list = await roles.ListAsync(scope, cancellationToken).ConfigureAwait(false);
            custom = new Dictionary<RoleId, AccessRole>(list.Count);
            for (var i = 0; i < list.Count; i++)
                custom[list[i].Role.Id] = list[i].Role;
        }
        else if (singleCustomRole is { } roleId)
            resolvedSingleCustom = (await roles.GetAsync(scope, roleId, cancellationToken).ConfigureAwait(false))?.Role;

        var resolved = new List<ResolvedGrant>(valid.Count);
        for (var i = 0; i < valid.Count; i++)
        {
            var grant = valid[i];
            var role = BuiltInRoles.Find(grant.Role)
                ?? (grant.Scope is TenantGrantScope
                    ? null
                    : custom is not null
                        ? custom.GetValueOrDefault(grant.Role)
                        : resolvedSingleCustom is not null && resolvedSingleCustom.Id == grant.Role ? resolvedSingleCustom : null);
            if (preserveLocalAdminOnly && grant.Effect == GrantEffect.Allow && grant.Kind != GrantKind.BreakGlass)
            {
                if (role is null)
                    continue;
                HashSet<Permission>? adminPermissions = null;
                if (role.Includes(AccessPermissions.AccessManage) && Catalog.Contains(AccessPermissions.AccessManage))
                    (adminPermissions ??= []).Add(AccessPermissions.AccessManage);
                if (role.Includes(AccessPermissions.AccessManageTenant) && Catalog.Contains(AccessPermissions.AccessManageTenant))
                    (adminPermissions ??= []).Add(AccessPermissions.AccessManageTenant);
                if (role.Includes(AccessPermissions.AccessBreakGlass) && Catalog.Contains(AccessPermissions.AccessBreakGlass))
                    (adminPermissions ??= []).Add(AccessPermissions.AccessBreakGlass);
                if (adminPermissions is null)
                    continue;
                resolved.Add(new(grant, role, adminPermissions, IsRoleStripped: true));
                continue;
            }
            resolved.Add(new(grant, role));
        }

        if (options.RoleFederation.Mode != RoleFederationMode.Disabled)
        {
            var federated = await RoleFederationSupport.ResolveFederatedGrantsAsync(
                principal,
                scope,
                options.RoleFederation,
                roleFederationMappings,
                roles,
                Catalog,
                custom,
                cancellationToken).ConfigureAwait(false);
            if (federated.Count > 0)
                resolved.AddRange(federated);
        }

        return new(principal, scope, now, resolved);
    }

    /// <remarks>With <c>tenantAllowsOnly</c>, only allow grants whose own scope is tenant-wide count (tenant administration authority); denies of every scope still apply.</remarks>
    public static PointResult Evaluate(AccessContext context, Permission permission, AccessResource resource, bool includeBreakGlass, bool tenantAllowsOnly = false)
    {
        if (resource.Scope != context.Scope)
            return PointResult.Deny(AccessDenialReason.ScopeMismatch);
        List<ResolvedGrant>? standard = null;
        List<ResolvedGrant>? breakGlass = null;
        var grants = context.Grants;
        for (var i = 0; i < grants.Count; i++)
        {
            var candidate = grants[i];
            var grant = candidate.Grant;
            if (!includeBreakGlass && grant.Kind == GrantKind.BreakGlass)
                continue;
            if (!ScopeMatches(grant.Scope, resource))
                continue;
            switch (grant.Effect)
            {
                case GrantEffect.Deny:
                    if ((candidate.Role is null || candidate.Includes(permission)) && Evaluate(grant.Condition, context, resource) != false)
                        return PointResult.Deny(AccessDenialReason.ExplicitDeny);
                    break;
                case GrantEffect.Allow:
                    if (tenantAllowsOnly && grant.Scope is not TenantGrantScope)
                        break;
                    if (candidate.Includes(permission) && Evaluate(grant.Condition, context, resource) == true)
                    {
                        if (grant.Kind == GrantKind.BreakGlass)
                            (breakGlass ??= []).Add(candidate);
                        else
                            (standard ??= []).Add(candidate);
                    }
                    break;
            }
        }
        if (standard is null && breakGlass is null)
            return PointResult.Deny(AccessDenialReason.NoMatchingGrant);
        return new(null, (IReadOnlyList<ResolvedGrant>?)standard ?? [], (IReadOnlyList<ResolvedGrant>?)breakGlass ?? []);
    }

    /// <summary>Allocation-free variant of point evaluation for callers that only need Allowed/Denial/UsedBreakGlass.</summary>
    public static SimplePointResult EvaluateSimple(AccessContext context, Permission permission, AccessResource resource, bool includeBreakGlass, bool tenantAllowsOnly = false)
    {
        if (resource.Scope != context.Scope)
            return SimplePointResult.Deny(AccessDenialReason.ScopeMismatch);
        var hasStandard = false;
        var hasBreakGlass = false;
        var grants = context.Grants;
        for (var i = 0; i < grants.Count; i++)
        {
            var candidate = grants[i];
            var grant = candidate.Grant;
            if (!includeBreakGlass && grant.Kind == GrantKind.BreakGlass)
                continue;
            if (!ScopeMatches(grant.Scope, resource))
                continue;
            switch (grant.Effect)
            {
                case GrantEffect.Deny:
                    if ((candidate.Role is null || candidate.Includes(permission)) && Evaluate(grant.Condition, context, resource) != false)
                        return SimplePointResult.Deny(AccessDenialReason.ExplicitDeny);
                    break;
                case GrantEffect.Allow:
                    if (tenantAllowsOnly && grant.Scope is not TenantGrantScope)
                        break;
                    if (grant.Kind == GrantKind.BreakGlass)
                    {
                        if (!hasBreakGlass && !hasStandard && candidate.Includes(permission) && Evaluate(grant.Condition, context, resource) == true)
                            hasBreakGlass = true;
                    }
                    else if (!hasStandard && candidate.Includes(permission) && Evaluate(grant.Condition, context, resource) == true)
                        hasStandard = true;
                    break;
            }
        }
        if (!hasStandard && !hasBreakGlass)
            return SimplePointResult.Deny(AccessDenialReason.NoMatchingGrant);
        return new(null, !hasStandard);
    }

    public static bool ScopeMatches(GrantScope scope, AccessResource resource) => scope switch
    {
        TenantGrantScope => true,
        WorkspaceGrantScope workspace => workspace.Workspace == resource.Scope.WorkspaceId,
        TeamGrantScope team => team.Workspace == resource.Scope.WorkspaceId && resource.TeamId == team.Team,
        InboxGrantScope inbox => inbox.Workspace == resource.Scope.WorkspaceId && resource.InboxId == inbox.Inbox,
    };

    /// <summary>Kleene evaluation of a grant condition; <see langword="null"/> means unknown.</summary>
    public static bool? Evaluate(AccessCondition? condition, AccessContext context, AccessResource resource) => condition switch
    {
        null => true,
        AndCondition and => EvaluateAnd(and.Operands, context, resource),
        OrCondition or => EvaluateOr(or.Operands, context, resource),
        NotCondition not => !Evaluate(not.Operand, context, resource),
        AssigneeIsSelfCondition => context.User is { } user ? resource.AssigneeId == user.UserId : null,
        TeamInPrincipalTeamsCondition => context.User is { } user ? resource.TeamId is { } team && user.Teams.Contains(team) : null,
        AttributeEqualsCondition equals => resource.Attributes.TryGetValue(equals.Key, out var value) ? string.Equals(value, equals.Value, global::System.StringComparison.Ordinal) : null,
        AttributeInCondition @in => resource.Attributes.TryGetValue(@in.Key, out var value) ? @in.ContainsValue(value) : null,
        HasLabelCondition label => resource.Labels?.Contains(label.Label),
        TimeWindowCondition window => window.Contains(context.Now),
    };

    private static bool? EvaluateAnd(IReadOnlyList<AccessCondition> operands, AccessContext context, AccessResource resource)
    {
        var unknown = false;
        for (var i = 0; i < operands.Count; i++)
        {
            var value = Evaluate(operands[i], context, resource);
            if (value == false)
                return false;
            unknown |= value is null;
        }
        return unknown ? null : true;
    }

    private static bool? EvaluateOr(IReadOnlyList<AccessCondition> operands, AccessContext context, AccessResource resource)
    {
        var unknown = false;
        for (var i = 0; i < operands.Count; i++)
        {
            var value = Evaluate(operands[i], context, resource);
            if (value == true)
                return true;
            unknown |= value is null;
        }
        return unknown ? null : false;
    }

    public static WorkspaceId? WorkspaceOf(GrantScope scope) => scope switch
    {
        TenantGrantScope => null,
        WorkspaceGrantScope workspace => workspace.Workspace,
        TeamGrantScope team => team.Workspace,
        InboxGrantScope inbox => inbox.Workspace,
    };

    /// <summary>The resource a grant change targets, used to authorize the actor.</summary>
    public static AccessResource Target(TicketScope actorScope, GrantScope scope) => scope switch
    {
        TenantGrantScope => new(actorScope),
        WorkspaceGrantScope => new(actorScope),
        TeamGrantScope team => new(actorScope, team: team.Team),
        InboxGrantScope inbox => new(actorScope, inbox: inbox.Inbox),
    };

    public static AccessAuditEvent AuditEvent(
        DateTimeOffset now,
        PrincipalRef actor,
        AccessAuditKind kind,
        AccessAuditOutcome outcome,
        Permission? permission = null,
        AccessResource? resource = null,
        GrantId? grant = null,
        RoleId? role = null,
        FieldPath? field = null,
        bool usedBreakGlass = false,
        AccessDenialReason? denialReason = null,
        string? justification = null) =>
        new(Guid.CreateVersion7(now), now, actor.Scope, kind, outcome, AccessActor.From(actor), permission is { IsInitialized: true } ? permission : null,
            resource?.ResourceId, resource?.TeamId, resource?.InboxId, grant, role, field, usedBreakGlass, denialReason, justification);
}
