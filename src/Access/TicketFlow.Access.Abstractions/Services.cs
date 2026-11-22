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
}

/// <summary>
/// Plans list/search filters. For any resource in the query scope, <see cref="AccessFilterEvaluator.Matches"/> over the planned
/// filter equals <see cref="AccessDecision.IsPermitted"/> of the corresponding point check at the same instant.
/// </summary>
public interface IAccessQueryPlanner
{
    /// <summary>Returns <see cref="DenyAllFilter"/> whenever a point check would fail before considering resources.</summary>
    Task<AccessFilter> PlanAsync(AccessQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The change was authorized, validated, stored and audited.</summary>
public sealed record AccessChangeApplied<T>(T Value, Guid AuditEventId);

/// <summary>The change was refused. <see cref="Errors"/> are non-sensitive validation messages.</summary>
public sealed record AccessChangeRefused(AccessChangeRefusal Reason, IReadOnlyList<string> Errors, long? CurrentRevision = null);

public enum AccessChangeRefusal
{
    Forbidden = 1,
    Invalid = 2,
    NotFound = 3,
    Conflict = 4,
    Rejected = 5,
}

/// <summary>Outcome of an administrative change. Switch over every case without a discard arm.</summary>
public union AccessChangeResult<T>(AccessChangeApplied<T>, AccessChangeRefused);

/// <summary>Request for a time-bound elevated grant for the requesting principal itself.</summary>
public sealed record BreakGlassRequest
{
    public BreakGlassRequest(PrincipalRef requester, GrantId id, RoleId role, GrantScope scope, TimeSpan duration, string justification)
    {
        if (requester.Value is null)
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
    Task<AccessChangeResult<StoredAccessRole>> CreateRoleAsync(PrincipalRef actor, AccessRole role, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessRole>> UpdateRoleAsync(PrincipalRef actor, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessRole>> DeleteRoleAsync(PrincipalRef actor, RoleId role, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessGrant>> CreateGrantAsync(PrincipalRef actor, AccessGrant grant, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessGrant>> UpdateGrantAsync(PrincipalRef actor, AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessGrant>> RevokeGrantAsync(PrincipalRef actor, GrantId grant, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessFieldPolicy>> CreateFieldPolicyAsync(PrincipalRef actor, AccessFieldPolicy policy, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessFieldPolicy>> UpdateFieldPolicyAsync(PrincipalRef actor, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessFieldPolicy>> DeleteFieldPolicyAsync(PrincipalRef actor, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessChangeResult<StoredAccessGrant>> ActivateBreakGlassAsync(BreakGlassRequest request, CancellationToken cancellationToken = default);
}
