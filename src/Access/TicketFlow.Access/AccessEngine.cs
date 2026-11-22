using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>A valid grant with its resolved role; <see cref="Role"/> is null when the role cannot be resolved.</summary>
internal sealed record ResolvedGrant(AccessGrant Grant, AccessRole? Role);

/// <summary>Grants loaded once for one principal in one workspace scope at one instant.</summary>
internal sealed class AccessContext(PrincipalRef principal, TicketScope scope, DateTimeOffset now, IReadOnlyList<ResolvedGrant> grants)
{
    public PrincipalRef Principal { get; } = principal;
    public UserPrincipal? User { get; } = principal.Value as UserPrincipal;
    public TicketScope Scope { get; } = scope;
    public DateTimeOffset Now { get; } = now;
    public IReadOnlyList<ResolvedGrant> Grants { get; } = grants;
}

/// <summary>Result of the core point evaluation. Standard and break-glass allows are kept apart so callers can tell when break-glass changed an outcome.</summary>
internal sealed record PointResult(AccessDenialReason? Denial, IReadOnlyList<ResolvedGrant> StandardAllows, IReadOnlyList<ResolvedGrant> BreakGlassAllows)
{
    public bool Allowed => Denial is null;

    /// <summary>Allowed only because of a break-glass grant.</summary>
    public bool UsedBreakGlass => Allowed && StandardAllows.Count == 0;

    public static PointResult Deny(AccessDenialReason reason) => new(reason, [], []);
}

/// <summary>
/// The shared decision core. Rules: the principal's scope must equal the resource scope; only valid grants for the principal's
/// subjects that are tenant-wide or name the workspace are considered; any applicable deny whose role includes the permission
/// (or whose role cannot be resolved) and whose condition is not false denies; otherwise an applicable allow whose role includes
/// the permission and whose condition is true allows.
/// </summary>
internal sealed class AccessEngine(IPermissionCatalog catalog, IAccessGrantStore grants, IAccessRoleStore roles, TimeProvider time)
{
    public IPermissionCatalog Catalog { get; } = catalog;

    public DateTimeOffset Now => time.GetUtcNow();

    public AccessDenialReason? Precheck(PrincipalRef principal, Permission permission, TicketScope scope)
    {
        if (!Catalog.Contains(permission))
            return AccessDenialReason.UnknownPermission;
        if (Subjects(principal) is null)
            return AccessDenialReason.UnsupportedPrincipal;
        return principal.Scope == scope ? null : AccessDenialReason.ScopeMismatch;
    }

    public static IReadOnlyList<GrantSubject>? Subjects(PrincipalRef principal) => principal switch
    {
        UserPrincipal user => [(GrantSubject)user.UserId, .. user.Teams.Select(team => (GrantSubject)team)],
        ApiClientPrincipal client => [(GrantSubject)client.ClientId],
        AutomationPrincipal => null,
        SystemPrincipal => null,
    };

    public async Task<AccessContext> LoadAsync(PrincipalRef principal, TicketScope scope, CancellationToken cancellationToken)
    {
        var subjects = Subjects(principal) ?? throw new InvalidOperationException("The principal cannot hold grants.");
        var now = time.GetUtcNow();
        var partition = AccessPartition.From(scope);
        var stored = await grants.ListForSubjectsAsync(partition, scope.WorkspaceId, subjects, cancellationToken).ConfigureAwait(false);
        var subjectSet = subjects.ToHashSet();
        var valid = new List<AccessGrant>(stored.Count);
        foreach (var entry in stored)
        {
            var grant = entry.Grant;
            // A store returning grants outside the requested partition, workspace or subjects violates its contract; fail closed.
            if (grant.Partition != partition || (grant.Workspace is { } workspace && workspace != scope.WorkspaceId) || !subjectSet.Contains(grant.Subject))
                throw new InvalidOperationException("The grant store returned a grant outside the requested scope or subjects.");
            if (grant.IsValidAt(now))
                valid.Add(grant);
        }

        // Custom roles are resolved with one store round trip per load. Tenant grants never resolve custom roles.
        Dictionary<RoleId, AccessRole>? custom = null;
        if (valid.Any(grant => grant.Scope is not TenantGrantScope && !BuiltInRoles.IsBuiltIn(grant.Role)))
        {
            custom = [];
            foreach (var role in await roles.ListAsync(scope, cancellationToken).ConfigureAwait(false))
                custom[role.Role.Id] = role.Role;
        }
        var resolved = new List<ResolvedGrant>(valid.Count);
        foreach (var grant in valid)
        {
            var role = BuiltInRoles.Find(grant.Role)
                ?? (grant.Scope is TenantGrantScope ? null : custom?.GetValueOrDefault(grant.Role));
            resolved.Add(new(grant, role));
        }
        return new(principal, scope, now, resolved);
    }

    /// <remarks>With <c>tenantAllowsOnly</c>, only allow grants whose own scope is tenant-wide count (tenant administration authority); denies of every scope still apply.</remarks>
    public static PointResult Evaluate(AccessContext context, Permission permission, AccessResource resource, bool includeBreakGlass, bool tenantAllowsOnly = false)
    {
        if (resource.Scope != context.Scope)
            return PointResult.Deny(AccessDenialReason.ScopeMismatch);
        List<ResolvedGrant> standard = [];
        List<ResolvedGrant> breakGlass = [];
        foreach (var candidate in context.Grants)
        {
            var grant = candidate.Grant;
            if (!includeBreakGlass && grant.Kind == GrantKind.BreakGlass)
                continue;
            if (!ScopeMatches(grant.Scope, resource))
                continue;
            switch (grant.Effect)
            {
                case GrantEffect.Deny:
                    if ((candidate.Role is null || candidate.Role.Includes(permission)) && Evaluate(grant.Condition, context, resource) != false)
                        return PointResult.Deny(AccessDenialReason.ExplicitDeny);
                    break;
                case GrantEffect.Allow:
                    if (tenantAllowsOnly && grant.Scope is not TenantGrantScope)
                        break;
                    if (candidate.Role?.Includes(permission) == true && Evaluate(grant.Condition, context, resource) == true)
                        (grant.Kind == GrantKind.BreakGlass ? breakGlass : standard).Add(candidate);
                    break;
            }
        }
        if (standard.Count == 0 && breakGlass.Count == 0)
            return PointResult.Deny(AccessDenialReason.NoMatchingGrant);
        return new(null, standard, breakGlass);
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
        AndCondition and => Kleene.And(and.Operands, operand => Evaluate(operand, context, resource)),
        OrCondition or => Kleene.Or(or.Operands, operand => Evaluate(operand, context, resource)),
        NotCondition not => !Evaluate(not.Operand, context, resource),
        AssigneeIsSelfCondition => context.User is { } user ? resource.AssigneeId == user.UserId : null,
        TeamInPrincipalTeamsCondition => context.User is { } user ? resource.TeamId is { } team && user.Teams.Contains(team) : null,
        AttributeEqualsCondition equals => resource.Attributes.TryGetValue(equals.Key, out var value) ? value == equals.Value : null,
        AttributeInCondition @in => resource.Attributes.TryGetValue(@in.Key, out var value) ? @in.Values.Contains(value, StringComparer.Ordinal) : null,
        HasLabelCondition label => resource.Labels?.Contains(label.Label),
        TimeWindowCondition window => window.Contains(context.Now),
    };

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
