using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Point authorization. Every feature read or mutation authorizes through this interface.</summary>
public interface IAccessEvaluator
{
    /// <summary>
    /// Decides <paramref name="request"/>. Unknown permissions, scope mismatches, unsupported principals and missing attributes fail
    /// closed. Sensitive permissions and break-glass use are audited before the decision is returned.
    /// </summary>
    Task<AccessDecision> AuthorizeAsync(AccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>Authorizes revealing one masked field. Every call is audited (granted or refused) without the field value.</summary>
    Task<RevealResult> RevealAsync(RevealRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a batch decision snapshot for <paramref name="principal"/> in <paramref name="scope"/> and the given permissions (1-32),
    /// so list pages and multi-permission reads decide many resources without a store round trip per decision. The default
    /// implementation delegates each decision to <see cref="AuthorizeAsync"/>.
    /// </summary>
    Task<IAccessDecisionContext> LoadDecisionContextAsync(PrincipalRef principal, TicketScope scope, IEnumerable<Permission> permissions, CancellationToken cancellationToken = default) =>
        Task.FromResult<IAccessDecisionContext>(new PointAccessDecisionContext(this, principal, scope, permissions));
}

/// <summary>
/// Plans list/search filters. For any resource in the query scope, <see cref="AccessFilterEvaluator.Matches"/> over the planned
/// filter equals <see cref="AccessDecision.IsPermitted"/> of the corresponding point check at the same instant.
/// </summary>
public interface IAccessQueryPlanner
{
    /// <summary>Returns <see cref="DenyAllFilter"/> whenever a point check would fail before considering resources.</summary>
    Task<AccessFilter> PlanAsync(AccessQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The subset of <paramref name="permissions"/> (1-32) that <paramref name="principal"/> holds on <em>some</em> resource of
    /// <paramref name="scope"/> — under a workspace, team or inbox grant, conditional or not: the permissions whose plan is not
    /// <see cref="DenyAllFilter"/>. For navigation and UI gating only; services still authorize each resource. Audit semantics equal
    /// <see cref="PlanAsync"/> (sensitive permissions and break-glass use record one plan event each). The default implementation plans
    /// each permission; implementations may load grants once.
    /// </summary>
    async Task<IReadOnlySet<Permission>> HeldAnywhereAsync(PrincipalRef principal, TicketScope scope, IEnumerable<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var held = new HashSet<Permission>();
        foreach (var permission in AccessDecisionContexts.Validate(principal, scope, permissions))
            if (await PlanAsync(new AccessQuery(principal, permission, scope), cancellationToken).ConfigureAwait(false) is not DenyAllFilter)
                held.Add(permission);
        return held;
    }
}

/// <summary>The change was authorized, validated, stored and audited.</summary>
public readonly record struct AccessChangeApplied<T>(T Value, Guid AuditEventId);

/// <summary>Request for a time-bound elevated grant for the requesting principal itself.</summary>
public sealed record BreakGlassRequest
{
    public BreakGlassRequest(PrincipalRef requester, GrantId id, RoleId role, GrantScope scope, TimeSpan duration, string justification)
    {
        if (requester is null)
            throw new ArgumentException("A requester is required.", nameof(requester));
        if (!id.IsInitialized || !role.IsInitialized)
            throw new ArgumentException("Grant and role identifiers are required.");
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(justification);
        Requester = requester;
        Id = id;
        Role = role;
        Scope = scope;
        Duration = duration;
        Justification = justification;
    }

    public PrincipalRef Requester { get; }
    public GrantId Id { get; }
    public RoleId Role { get; }
    public GrantScope Scope { get; }
    public TimeSpan Duration { get; }
    public string Justification { get; }
}

/// <summary>
/// Authorized, validated and audited changes to roles, grants and field policies. Every call records exactly one audit event.
/// Store interfaces remain available for provisioning, but writes made directly through them are not authorized or audited.
/// </summary>
public interface IAccessAdministrator
{
    Task<TicketResult<AccessChangeApplied<StoredAccessRole>>> CreateRoleAsync(PrincipalRef actor, AccessRole role, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessRole>>> UpdateRoleAsync(PrincipalRef actor, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessRole>>> DeleteRoleAsync(PrincipalRef actor, RoleId role, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> CreateGrantAsync(PrincipalRef actor, AccessGrant grant, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> UpdateGrantAsync(PrincipalRef actor, AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> RevokeGrantAsync(PrincipalRef actor, GrantId grant, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Revokes multiple grants in one authorized batch (loading the actor's grants once).</summary>
    async Task<IReadOnlyList<TicketResult<AccessChangeApplied<StoredAccessGrant>>>> RevokeGrantsAsync(PrincipalRef actor, IReadOnlyList<(GrantId Id, long ExpectedRevision)> grants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var results = new TicketResult<AccessChangeApplied<StoredAccessGrant>>[grants.Count];
        for (var i = 0; i < grants.Count; i++)
            results[i] = await RevokeGrantAsync(actor, grants[i].Id, grants[i].ExpectedRevision, cancellationToken).ConfigureAwait(false);
        return results;
    }

    Task<TicketResult<AccessChangeApplied<StoredAccessFieldPolicy>>> CreateFieldPolicyAsync(PrincipalRef actor, AccessFieldPolicy policy, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessFieldPolicy>>> UpdateFieldPolicyAsync(PrincipalRef actor, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessFieldPolicy>>> DeleteFieldPolicyAsync(PrincipalRef actor, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<AccessChangeApplied<StoredAccessGrant>>> ActivateBreakGlassAsync(BreakGlassRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only check (nothing is written or audited) that <paramref name="actor"/> could confer every live allow grant in
    /// <paramref name="conferrals"/>: like <see cref="CreateGrantAsync"/>'s escalation rule, the actor must hold every permission of
    /// each grant's role on the conferral target from standard (never break-glass) grants, counting only tenant-scoped allows for
    /// tenant grants. Deny, expired and unresolvable-role grants confer nothing and are skipped. Use it before changes that extend
    /// existing grants to new principals, such as adding team members. The actor's grants are loaded once.
    /// </summary>
    Task<AccessCheckResult> CheckCanConferAsync(PrincipalRef actor, IEnumerable<GrantConferral> conferrals, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only check (nothing is written or audited) that <paramref name="actor"/> holds the authority <see cref="RevokeGrantAsync"/>
    /// requires for every grant in <paramref name="grants"/>: <c>access:manage</c> on the grant's target, or
    /// <c>access:manage-tenant</c> from a tenant-scoped grant for tenant grants, from standard grants only. Use it before changes that
    /// stop existing grants from applying, such as removing team members while the team is the subject of a deny grant.
    /// </summary>
    Task<AccessCheckResult> CheckCanRevokeAsync(PrincipalRef actor, IEnumerable<AccessGrant> grants, CancellationToken cancellationToken = default);

    /// <summary>Read-only, unaudited listing of the built-in and custom roles of the actor's workspace; requires <c>access:manage</c> (standard grants).</summary>
    Task<TicketResult<IReadOnlyList<AccessRoleListing>>> ListRolesAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only, unaudited page of the grants whose scope equals <paramref name="target"/> (workspace, team or inbox of the actor's
    /// workspace; ordered by identifier, after <paramref name="after"/>, 1-<see cref="AccessLimits.MaximumTargetPage"/>); requires
    /// <c>access:manage</c> on the target (standard grants).
    /// </summary>
    Task<TicketResult<IReadOnlyList<StoredAccessGrant>>> ListGrantsAsync(PrincipalRef actor, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default);

    /// <summary>Read-only, unaudited listing of the actor's workspace field policies; requires <c>access:manage</c> (standard grants).</summary>
    Task<TicketResult<IReadOnlyList<StoredAccessFieldPolicy>>> ListFieldPoliciesAsync(PrincipalRef actor, CancellationToken cancellationToken = default);
}

/// <summary>A role as listed: built-in roles have no revision.</summary>
public sealed record AccessRoleListing(AccessRole Role, long? Revision);

/// <summary>
/// An allow grant whose permissions a change would extend, and the resource they would apply to. <see cref="Target"/> defaults to
/// the grant's own scope target; supply a narrower resource when a condition (for example
/// <see cref="TeamInPrincipalTeamsCondition"/>) makes the grant newly apply there.
/// </summary>
public sealed record GrantConferral
{
    public GrantConferral(AccessGrant grant, AccessResource? target = null)
    {
        Grant = grant ?? throw new ArgumentNullException(nameof(grant));
        Target = target;
    }

    public AccessGrant Grant { get; }
    public AccessResource? Target { get; }
}

/// <summary>The checked change is within the actor's authority.</summary>
public sealed record AccessCheckPassed
{
    private AccessCheckPassed()
    {
    }

    public static AccessCheckPassed Instance { get; } = new();
}

/// <summary>Outcome of a read-only administrative check. Switch over every case without a discard arm.</summary>
public union AccessCheckResult(AccessCheckPassed, TicketFailure);
