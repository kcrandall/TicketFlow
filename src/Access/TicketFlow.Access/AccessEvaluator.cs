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
        var definition = engine.Catalog.Find(permission);
        var sensitive = definition?.IsSensitive == true;
        if (AccessEngine.Precheck(request.Principal, definition is not null, resource.Scope) is { } reason)
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
                var reveal = AccessEngine.EvaluateSimple(context, revealPermission, resource, includeBreakGlass: true);
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
        var unmasked = policy.UnmaskedRoles;
        if (unmasked.Count == 0)
            return MaskOutcome.Masked;
        var standard = result.StandardAllows;
        for (var i = 0; i < standard.Count; i++)
            if (unmasked.Contains(standard[i].Grant.Role))
                return MaskOutcome.UnmaskedByStandard;
        var breakGlass = result.BreakGlassAllows;
        for (var i = 0; i < breakGlass.Count; i++)
            if (unmasked.Contains(breakGlass[i].Grant.Role))
                return MaskOutcome.UnmaskedByBreakGlass;
        return MaskOutcome.Masked;
    }

    private async Task<AccessDecision> MaskAsync(AccessContext context, Permission permission, AccessResource resource, PointResult result, CancellationToken cancellationToken)
    {
        var stored = await policies.ListAsync(resource.Scope, cancellationToken).ConfigureAwait(false);
        return stored.Count == 0 ? new AccessAllowed(permission, result.UsedBreakGlass) : Mask(context, permission, resource, result, stored);
    }

    private AccessDecision Mask(AccessContext context, Permission permission, AccessResource resource, PointResult result, IReadOnlyList<StoredAccessFieldPolicy> stored)
    {
        var usedBreakGlass = result.UsedBreakGlass;
        List<MaskedField>? masked = null;
        Permission? cachedRevealPerm = null;
        SimplePointResult cachedReveal = default;
        Dictionary<Permission, SimplePointResult>? revealCache = null;

        for (var i = 0; i < stored.Count; i++)
        {
            var policy = stored[i].Policy;
            if (!policy.AppliesTo.Contains(permission))
                continue;
            EvaluatePolicy(context, resource, result, policy, ref usedBreakGlass, ref masked, ref cachedRevealPerm, ref cachedReveal, ref revealCache);
        }
        return masked is null ? new AccessAllowed(permission, usedBreakGlass) : new AccessMasked(permission, masked, usedBreakGlass);
    }

    private AccessDecision MaskFiltered(AccessContext context, Permission permission, AccessResource resource, PointResult result, AccessFieldPolicy[] applicable)
    {
        var usedBreakGlass = result.UsedBreakGlass;
        List<MaskedField>? masked = null;
        Permission? cachedRevealPerm = null;
        SimplePointResult cachedReveal = default;
        Dictionary<Permission, SimplePointResult>? revealCache = null;

        for (var i = 0; i < applicable.Length; i++)
            EvaluatePolicy(context, resource, result, applicable[i], ref usedBreakGlass, ref masked, ref cachedRevealPerm, ref cachedReveal, ref revealCache);
        return masked is null ? new AccessAllowed(permission, usedBreakGlass) : new AccessMasked(permission, masked, usedBreakGlass);
    }

    private void EvaluatePolicy(
        AccessContext context,
        AccessResource resource,
        PointResult result,
        AccessFieldPolicy policy,
        ref bool usedBreakGlass,
        ref List<MaskedField>? masked,
        ref Permission? cachedRevealPerm,
        ref SimplePointResult cachedReveal,
        ref Dictionary<Permission, SimplePointResult>? revealCache)
    {
        switch (Unmasking(policy, result))
        {
            case MaskOutcome.UnmaskedByStandard:
                return;
            case MaskOutcome.UnmaskedByBreakGlass:
                usedBreakGlass = true;
                return;
            case MaskOutcome.Masked:
                var revealable = false;
                if (policy.RevealPermission is { } revealPermission && engine.Catalog.Contains(revealPermission))
                {
                    SimplePointResult reveal;
                    if (cachedRevealPerm == revealPermission)
                        reveal = cachedReveal;
                    else if (revealCache is not null && revealCache.TryGetValue(revealPermission, out var cached))
                        reveal = cached;
                    else
                    {
                        reveal = AccessEngine.EvaluateSimple(context, revealPermission, resource, includeBreakGlass: true);
                        if (cachedRevealPerm is null)
                        {
                            cachedRevealPerm = revealPermission;
                            cachedReveal = reveal;
                        }
                        else
                        {
                            revealCache ??= new Dictionary<Permission, SimplePointResult> { [cachedRevealPerm.Value] = cachedReveal };
                            revealCache[revealPermission] = reveal;
                        }
                    }
                    revealable = reveal.Allowed;
                    usedBreakGlass |= reveal.UsedBreakGlass;
                }
                (masked ??= []).Add(new(policy.Field, revealable));
                return;
        }
    }

    /// <summary>
    /// Loads grants, roles and field policies once. Each decision then evaluates in memory; sensitive permissions and every decision
    /// that relies on break-glass are still audited per decision, exactly like <see cref="AuthorizeAsync"/>.
    /// </summary>
    public async Task<IAccessDecisionContext> LoadDecisionContextAsync(PrincipalRef principal, TicketScope scope, IEnumerable<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var loaded = AccessDecisionContexts.Validate(principal, scope, permissions);
        var entries = new Dictionary<Permission, LoadedPermission>(loaded.Count);
        var anyAllowed = false;
        foreach (var permission in loaded)
        {
            var definition = engine.Catalog.Find(permission);
            var precheck = AccessEngine.Precheck(principal, definition is not null, scope);
            anyAllowed |= precheck is null;
            entries[permission] = new(precheck, definition?.IsSensitive == true, null);
        }

        AccessContext? context = null;
        if (anyAllowed)
        {
            var contextTask = engine.LoadAsync(principal, scope, cancellationToken);
            var policiesTask = policies.ListAsync(scope, cancellationToken);
            await Task.WhenAll(contextTask, policiesTask).ConfigureAwait(false);
            context = await contextTask.ConfigureAwait(false);
            var stored = await policiesTask.ConfigureAwait(false);
            if (stored.Count > 0)
            {
                foreach (var permission in loaded)
                {
                    var entry = entries[permission];
                    if (entry.Precheck is not null)
                        continue;
                    List<AccessFieldPolicy>? matching = null;
                    for (var i = 0; i < stored.Count; i++)
                    {
                        var policy = stored[i].Policy;
                        if (policy.AppliesTo.Contains(permission))
                            (matching ??= []).Add(policy);
                    }
                    if (matching is not null)
                        entries[permission] = entry with { Policies = [.. matching] };
                }
            }
        }
        return new BatchContext(this, principal, scope, loaded, entries, context);
    }

    private readonly record struct LoadedPermission(AccessDenialReason? Precheck, bool Sensitive, AccessFieldPolicy[]? Policies);

    private sealed class BatchContext(AccessEvaluator owner, PrincipalRef principal, TicketScope scope, IReadOnlySet<Permission> permissions,
        Dictionary<Permission, LoadedPermission> entries, AccessContext? context) : IAccessDecisionContext
    {
        public PrincipalRef Principal { get; } = principal;
        public TicketScope Scope { get; } = scope;
        public IReadOnlySet<Permission> Permissions { get; } = permissions;

        public ValueTask<AccessDecision> DecideAsync(Permission permission, AccessResource resource, CancellationToken cancellationToken = default)
        {
            if (!entries.TryGetValue(permission, out var entry))
                throw new ArgumentException("The permission was not loaded into this decision context.", nameof(permission));
            ArgumentNullException.ThrowIfNull(resource);
            AccessDecision decision;
            if (entry.Precheck is { } reason)
                decision = new AccessDenied(permission, reason);
            else if (resource.Scope != Scope)
                decision = new AccessDenied(permission, AccessDenialReason.ScopeMismatch);
            else if (entry.Policies is null)
            {
                var simple = AccessEngine.EvaluateSimple(context!, permission, resource, includeBreakGlass: true);
                decision = simple.Denial is { } denial ? new AccessDenied(permission, denial) : new AccessAllowed(permission, simple.UsedBreakGlass);
            }
            else
            {
                var result = AccessEngine.Evaluate(context!, permission, resource, includeBreakGlass: true);
                decision = result.Denial is { } denial ? new AccessDenied(permission, denial) : owner.MaskFiltered(context!, permission, resource, result, entry.Policies);
            }
            // Same audit semantics as AuthorizeAsync: sensitive permissions and every decision relying on break-glass are recorded
            // per resource; standard-grant decisions of non-sensitive permissions stay on the synchronous fast path.
            if (!entry.Sensitive && !UsedBreakGlass(decision))
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
