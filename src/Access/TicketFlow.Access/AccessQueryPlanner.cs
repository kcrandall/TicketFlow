namespace TicketFlow.Access;

/// <summary>
/// Default <see cref="IAccessQueryPlanner"/>. The plan is <c>scope ∧ (allow₁ ∨ … ∨ allowₙ) ∧ ¬deny₁ ∧ … ∧ ¬denyₘ</c> where each term is
/// the grant's scope predicate conjoined with its condition. Self and team references are bound to the principal and time windows
/// and validity are evaluated once at plan time, so the filter equals point checks at the planning instant.
/// </summary>
public sealed class AccessQueryPlanner : IAccessQueryPlanner
{
    private readonly AccessEngine engine;
    private readonly IAccessAuditSink audit;

    public AccessQueryPlanner(IPermissionCatalog catalog, IAccessGrantStore grants, IAccessRoleStore roles, IAccessAuditSink audit, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(time);
        engine = new(catalog, grants, roles, time);
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<AccessFilter> PlanAsync(AccessQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var permission = query.Permission;
        var sensitive = engine.Catalog.Find(permission)?.IsSensitive == true;
        if (engine.Precheck(query.Principal, permission, query.Scope) is { } reason)
        {
            if (sensitive)
                await RecordAsync(query, DenyAllFilter.Instance, false, reason, engine.Now, cancellationToken).ConfigureAwait(false);
            return DenyAllFilter.Instance;
        }

        var context = await engine.LoadAsync(query.Principal, query.Scope, cancellationToken).ConfigureAwait(false);
        var allows = new List<AccessFilter>();
        var denies = new List<AccessFilter>();
        var usedBreakGlass = false;
        foreach (var candidate in context.Grants)
        {
            var grant = candidate.Grant;
            // Grant conditions are bounded (≤ 64 nodes), so individual terms cannot exceed the filter limit.
            switch (grant.Effect)
            {
                case GrantEffect.Deny:
                    if (candidate.Role is null || candidate.Role.Includes(permission))
                        denies.Add(AccessFilters.Not(AccessFilters.And(ScopeTerm(grant.Scope), Translate(grant.Condition, context))));
                    break;
                case GrantEffect.Allow:
                    if (candidate.Role?.Includes(permission) != true)
                        break;
                    var term = AccessFilters.And(ScopeTerm(grant.Scope), Translate(grant.Condition, context));
                    // Allow terms sit in a positive position under the root conjunction, so an unknown term can never make a row
                    // true and is dropped like false. Deny terms keep unknown: ¬unknown must stay unknown (fail closed).
                    if (term is DenyAllFilter or UnknownFilter)
                        break;
                    allows.Add(term);
                    usedBreakGlass |= grant.Kind == GrantKind.BreakGlass;
                    break;
            }
        }

        AccessFilter filter;
        try
        {
            filter = AccessFilters.And([new ScopeFilter(query.Scope), AccessFilters.Or(allows), .. denies]);
        }
        catch (AccessFilterLimitExceededException)
        {
            // Fail closed: an oversized plan is never truncated or weakened. Always audited.
            await RecordAsync(query, DenyAllFilter.Instance, false, AccessDenialReason.FilterLimitExceeded, context.Now, cancellationToken).ConfigureAwait(false);
            return DenyAllFilter.Instance;
        }
        if (filter is DenyAllFilter)
            usedBreakGlass = false;
        if (sensitive || usedBreakGlass)
            await RecordAsync(query, filter, usedBreakGlass, filter is DenyAllFilter ? AccessDenialReason.NoMatchingGrant : null, context.Now, cancellationToken).ConfigureAwait(false);
        return filter;
    }

    private static AccessFilter ScopeTerm(GrantScope scope) => scope switch
    {
        TenantGrantScope => AllowAllFilter.Instance,
        WorkspaceGrantScope => AllowAllFilter.Instance,
        TeamGrantScope team => new TeamInFilter([team.Team]),
        InboxGrantScope inbox => new InboxInFilter([inbox.Inbox]),
    };

    private static AccessFilter Translate(AccessCondition? condition, AccessContext context) => condition switch
    {
        null => AllowAllFilter.Instance,
        AndCondition and => AccessFilters.And(and.Operands.Select(operand => Translate(operand, context))),
        OrCondition or => AccessFilters.Or(or.Operands.Select(operand => Translate(operand, context))),
        NotCondition not => AccessFilters.Not(Translate(not.Operand, context)),
        AssigneeIsSelfCondition => context.User is { } user ? new AssigneeIsFilter(user.UserId) : UnknownFilter.Instance,
        TeamInPrincipalTeamsCondition => context.User is { } user
            ? user.Teams.Count == 0 ? DenyAllFilter.Instance : new TeamInFilter(user.Teams)
            : UnknownFilter.Instance,
        AttributeEqualsCondition equals => new AttributeEqualsFilter(equals.Key, equals.Value),
        AttributeInCondition @in => new AttributeInFilter(@in.Key, @in.Values),
        HasLabelCondition label => new HasLabelFilter(label.Label),
        TimeWindowCondition window => window.Contains(context.Now) ? AllowAllFilter.Instance : DenyAllFilter.Instance,
    };

    private Task RecordAsync(AccessQuery query, AccessFilter filter, bool usedBreakGlass, AccessDenialReason? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        audit.RecordAsync(AccessEngine.AuditEvent(now, query.Principal, AccessAuditKind.FilterPlanned,
            filter is DenyAllFilter ? AccessAuditOutcome.Denied : AccessAuditOutcome.Allowed, query.Permission, usedBreakGlass: usedBreakGlass, denialReason: reason), cancellationToken);
}
