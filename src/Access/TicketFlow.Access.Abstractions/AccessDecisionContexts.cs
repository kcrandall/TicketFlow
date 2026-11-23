using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// A batch of point decisions for one principal, one scope and a fixed set of permissions, evaluated against a snapshot (grants,
/// roles and field policies loaded once, validity evaluated at load time). <see cref="DecideAsync"/> equals
/// <see cref="IAccessEvaluator.AuthorizeAsync"/> at the load instant and completes synchronously unless an audit event must be
/// persisted: decisions for sensitive permissions and every decision that relies on a break-glass grant are audited per resource,
/// exactly as with point checks. Contexts are short-lived (one request); do not cache them.
/// </summary>
public interface IAccessDecisionContext
{
    PrincipalRef Principal { get; }
    TicketScope Scope { get; }

    /// <summary>The permissions this context can decide.</summary>
    IReadOnlySet<Permission> Permissions { get; }

    /// <summary>Decides <paramref name="permission"/> (one of <see cref="Permissions"/>) for <paramref name="resource"/>.</summary>
    /// <exception cref="ArgumentException">The permission was not loaded into this context.</exception>
    ValueTask<AccessDecision> DecideAsync(Permission permission, AccessResource resource, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fallback <see cref="IAccessDecisionContext"/> that delegates every decision to <see cref="IAccessEvaluator.AuthorizeAsync"/>.
/// Used by evaluators that do not implement batch loading.
/// </summary>
public sealed class PointAccessDecisionContext : IAccessDecisionContext
{
    private readonly IAccessEvaluator evaluator;

    public PointAccessDecisionContext(IAccessEvaluator evaluator, PrincipalRef principal, TicketScope scope, IEnumerable<Permission> permissions)
    {
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        Principal = principal;
        Scope = scope;
        Permissions = AccessDecisionContexts.Validate(principal, scope, permissions);
    }

    public PrincipalRef Principal { get; }
    public TicketScope Scope { get; }
    public IReadOnlySet<Permission> Permissions { get; }

    public ValueTask<AccessDecision> DecideAsync(Permission permission, AccessResource resource, CancellationToken cancellationToken = default)
    {
        AccessDecisionContexts.RequireLoaded(Permissions, permission);
        return new ValueTask<AccessDecision>(evaluator.AuthorizeAsync(new AccessRequest(Principal, permission, resource), cancellationToken));
    }
}

public static class AccessDecisionContexts
{
    /// <summary>Maximum permissions loaded into one context.</summary>
    public const int MaximumPermissions = 32;

    /// <summary>Validates and snapshots a context's principal, scope and 1-<see cref="MaximumPermissions"/> permissions.</summary>
    public static IReadOnlySet<Permission> Validate(PrincipalRef principal, TicketScope scope, IEnumerable<Permission> permissions)
    {
        if (principal.Value is null)
            throw new ArgumentException("A principal is required.", nameof(principal));
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(permissions);
        var snapshot = new HashSet<Permission>();
        foreach (var permission in permissions)
        {
            if (!permission.IsInitialized)
                throw new ArgumentException("Permissions must be initialized.", nameof(permissions));
            snapshot.Add(permission);
        }
        if (snapshot.Count is 0 or > MaximumPermissions)
            throw new ArgumentException($"A decision context loads 1-{MaximumPermissions} permissions.", nameof(permissions));
        return snapshot;
    }

    public static void RequireLoaded(IReadOnlySet<Permission> permissions, Permission permission)
    {
        if (!permissions.Contains(permission))
            throw new ArgumentException("The permission was not loaded into this decision context.", nameof(permission));
    }
}
