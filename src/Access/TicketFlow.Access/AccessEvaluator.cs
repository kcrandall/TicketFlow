using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Default <see cref="IAccessEvaluator"/>. Masking: for an allowed permission, every field policy in the resource scope that applies
/// to the permission masks its field unless an applicable allow grant uses one of the policy's unmasked roles.
/// </summary>
public sealed class AccessEvaluator : IAccessEvaluator
{
    private readonly AccessEngine engine;
    private readonly IAccessFieldPolicyStore policies;
    private readonly IAccessAuditSink audit;

    public AccessEvaluator(
        IPermissionCatalog catalog,
        IAccessGrantStore grants,
        IAccessRoleStore roles,
        IAccessFieldPolicyStore policies,
        IAccessAuditSink audit,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(time);
        engine = new(catalog, grants, roles, time);
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<AccessDecision> AuthorizeAsync(AccessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var permission = request.Permission;
        var resource = request.Resource;
        var sensitive = engine.Catalog.Find(permission)?.IsSensitive == true;
        if (engine.Precheck(request.Principal, permission, resource.Scope) is { } reason)
        {
            AccessDecision rejected = new AccessDenied(permission, reason);
            if (sensitive)
                await RecordDecisionAsync(request, rejected, engine.Now, cancellationToken).ConfigureAwait(false);
            return rejected;
        }

        var context = await engine.LoadAsync(request.Principal, resource.Scope, cancellationToken).ConfigureAwait(false);
        var result = AccessEngine.Evaluate(context, permission, resource, includeBreakGlass: true);
        var decision = result.Denial is { } denial
            ? new AccessDenied(permission, denial)
            : await MaskAsync(context, permission, resource, result, cancellationToken).ConfigureAwait(false);
        if (sensitive || UsedBreakGlass(decision))
            await RecordDecisionAsync(request, decision, context.Now, cancellationToken).ConfigureAwait(false);
        return decision;
    }

    public async Task<RevealResult> RevealAsync(RevealRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resource = request.Resource;
        if (engine.Precheck(request.Principal, request.ReadPermission, resource.Scope) is not null)
            return await RefuseAsync(request, RevealRefusal.ReadDenied, engine.Now, cancellationToken).ConfigureAwait(false);

        var context = await engine.LoadAsync(request.Principal, resource.Scope, cancellationToken).ConfigureAwait(false);
        var read = AccessEngine.Evaluate(context, request.ReadPermission, resource, includeBreakGlass: true);
        if (!read.Allowed)
            return await RefuseAsync(request, RevealRefusal.ReadDenied, context.Now, cancellationToken).ConfigureAwait(false);

        var policy = (await policies.GetAsync(resource.Scope, request.Field, cancellationToken).ConfigureAwait(false))?.Policy;
        if (policy is null || !policy.AppliesTo.Contains(request.ReadPermission))
            return await RefuseAsync(request, RevealRefusal.NoPolicy, context.Now, cancellationToken).ConfigureAwait(false);

        var usedBreakGlass = read.UsedBreakGlass;
        switch (Unmasking(policy, read))
        {
            case MaskOutcome.UnmaskedByStandard:
                break;
            case MaskOutcome.UnmaskedByBreakGlass:
                usedBreakGlass = true;
                break;
            case MaskOutcome.Masked:
                if (policy.RevealPermission is not { } revealPermission || !engine.Catalog.Contains(revealPermission))
                    return await RefuseAsync(request, RevealRefusal.NotRevealable, context.Now, cancellationToken).ConfigureAwait(false);
                var reveal = AccessEngine.Evaluate(context, revealPermission, resource, includeBreakGlass: true);
                if (!reveal.Allowed)
                    return await RefuseAsync(request, RevealRefusal.RevealDenied, context.Now, cancellationToken).ConfigureAwait(false);
                usedBreakGlass |= reveal.UsedBreakGlass;
                break;
        }

        var granted = AccessEngine.AuditEvent(context.Now, request.Principal, AccessAuditKind.Reveal, AccessAuditOutcome.Allowed, request.ReadPermission,
            resource, field: request.Field, usedBreakGlass: usedBreakGlass, justification: request.Justification);
        await audit.RecordAsync(granted, cancellationToken).ConfigureAwait(false);
        return new RevealGranted(request.Field, granted.Id);
    }

    private enum MaskOutcome
    {
        Masked,
        UnmaskedByStandard,
        UnmaskedByBreakGlass,
    }

    /// <summary>Masking is decided from standard allows first, so a break-glass grant that changes the outcome is detected and audited.</summary>
    private static MaskOutcome Unmasking(AccessFieldPolicy policy, PointResult result)
    {
        if (result.StandardAllows.Any(allow => policy.UnmaskedRoles.Contains(allow.Grant.Role)))
            return MaskOutcome.UnmaskedByStandard;
        return result.BreakGlassAllows.Any(allow => policy.UnmaskedRoles.Contains(allow.Grant.Role)) ? MaskOutcome.UnmaskedByBreakGlass : MaskOutcome.Masked;
    }

    private async Task<AccessDecision> MaskAsync(AccessContext context, Permission permission, AccessResource resource, PointResult result, CancellationToken cancellationToken) =>
        Mask(context, permission, resource, result, await policies.ListAsync(resource.Scope, cancellationToken).ConfigureAwait(false));

    private AccessDecision Mask(AccessContext context, Permission permission, AccessResource resource, PointResult result, IReadOnlyList<StoredAccessFieldPolicy> stored)
    {
        var usedBreakGlass = result.UsedBreakGlass;
        var masked = new List<MaskedField>();
        foreach (var entry in stored)
        {
            var policy = entry.Policy;
            if (!policy.AppliesTo.Contains(permission))
                continue;
            switch (Unmasking(policy, result))
            {
                case MaskOutcome.UnmaskedByStandard:
                    continue;
                case MaskOutcome.UnmaskedByBreakGlass:
                    usedBreakGlass = true;
                    continue;
                case MaskOutcome.Masked:
                    var revealable = false;
                    if (policy.RevealPermission is { } revealPermission && engine.Catalog.Contains(revealPermission))
                    {
                        var reveal = AccessEngine.Evaluate(context, revealPermission, resource, includeBreakGlass: true);
                        revealable = reveal.Allowed;
                        usedBreakGlass |= reveal.UsedBreakGlass;
                    }
                    masked.Add(new(policy.Field, revealable));
                    break;
            }
        }
        return masked.Count == 0 ? new AccessAllowed(permission, usedBreakGlass) : new AccessMasked(permission, masked, usedBreakGlass);
    }

    /// <summary>
    /// Loads grants, roles and field policies once. Each decision then evaluates in memory; sensitive permissions are still audited
    /// per decision and break-glass use once per permission per context.
    /// </summary>
    public async Task<IAccessDecisionContext> LoadDecisionContextAsync(PrincipalRef principal, TicketScope scope, IEnumerable<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var loaded = AccessDecisionContexts.Validate(principal, scope, permissions);
        var prechecks = loaded.ToDictionary(permission => permission, permission => engine.Precheck(principal, permission, scope));
        AccessContext? context = null;
        IReadOnlyList<StoredAccessFieldPolicy> stored = [];
        if (prechecks.Values.Any(reason => reason is null))
        {
            context = await engine.LoadAsync(principal, scope, cancellationToken).ConfigureAwait(false);
            stored = await policies.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        return new BatchContext(this, principal, scope, loaded, prechecks, context, stored);
    }

    private sealed class BatchContext(AccessEvaluator owner, PrincipalRef principal, TicketScope scope, IReadOnlySet<Permission> permissions,
        IReadOnlyDictionary<Permission, AccessDenialReason?> prechecks, AccessContext? context, IReadOnlyList<StoredAccessFieldPolicy> policies) : IAccessDecisionContext
    {
        private readonly Lock gate = new();
        private readonly HashSet<Permission> breakGlassAudited = [];

        public PrincipalRef Principal { get; } = principal;
        public TicketScope Scope { get; } = scope;
        public IReadOnlySet<Permission> Permissions { get; } = permissions;

        public ValueTask<AccessDecision> DecideAsync(Permission permission, AccessResource resource, CancellationToken cancellationToken = default)
        {
            AccessDecisionContexts.RequireLoaded(Permissions, permission);
            ArgumentNullException.ThrowIfNull(resource);
            var sensitive = owner.engine.Catalog.Find(permission)?.IsSensitive == true;
            AccessDecision decision;
            if (prechecks[permission] is { } reason)
                decision = new AccessDenied(permission, reason);
            else if (resource.Scope != Scope)
                decision = new AccessDenied(permission, AccessDenialReason.ScopeMismatch);
            else
            {
                var result = AccessEngine.Evaluate(context!, permission, resource, includeBreakGlass: true);
                decision = result.Denial is { } denial ? new AccessDenied(permission, denial) : owner.Mask(context!, permission, resource, result, policies);
            }
            var audit = sensitive;
            if (!audit && UsedBreakGlass(decision))
                lock (gate)
                    audit = breakGlassAudited.Add(permission);
            if (!audit)
                return ValueTask.FromResult(decision);
            return RecordAsync(permission, resource, decision, cancellationToken);
        }

        private async ValueTask<AccessDecision> RecordAsync(Permission permission, AccessResource resource, AccessDecision decision, CancellationToken cancellationToken)
        {
            await owner.RecordDecisionAsync(new AccessRequest(Principal, permission, resource), decision, context?.Now ?? owner.engine.Now, cancellationToken).ConfigureAwait(false);
            return decision;
        }
    }

    private static bool UsedBreakGlass(AccessDecision decision) => decision switch
    {
        AccessAllowed allowed => allowed.UsedBreakGlass,
        AccessDenied => false,
        AccessMasked masked => masked.UsedBreakGlass,
    };

    private Task RecordDecisionAsync(AccessRequest request, AccessDecision decision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (outcome, usedBreakGlass, reason) = decision switch
        {
            AccessAllowed allowed => (AccessAuditOutcome.Allowed, allowed.UsedBreakGlass, (AccessDenialReason?)null),
            AccessDenied denied => (AccessAuditOutcome.Denied, false, denied.Reason),
            AccessMasked masked => (AccessAuditOutcome.Masked, masked.UsedBreakGlass, null),
        };
        return audit.RecordAsync(AccessEngine.AuditEvent(now, request.Principal, AccessAuditKind.Decision, outcome, request.Permission, request.Resource,
            usedBreakGlass: usedBreakGlass, denialReason: reason), cancellationToken);
    }

    private async Task<RevealResult> RefuseAsync(RevealRequest request, RevealRefusal reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var refused = AccessEngine.AuditEvent(now, request.Principal, AccessAuditKind.Reveal, AccessAuditOutcome.Denied, request.ReadPermission,
            request.Resource, field: request.Field, justification: request.Justification);
        await audit.RecordAsync(refused, cancellationToken).ConfigureAwait(false);
        return new RevealRefused(request.Field, reason, refused.Id);
    }
}
