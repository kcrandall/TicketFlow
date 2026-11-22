using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>Shared authorization, validation and result mapping for help center services.</summary>
internal static class HelpCenterGuards
{
    public static void RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    /// <summary>Point check through Access. Masked decisions permit the action: help center records define no maskable fields.</summary>
    public static async Task<HelpCenterRefused?> AuthorizeAsync(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision switch
        {
            AccessAllowed => null,
            AccessMasked => null,
            AccessDenied => new HelpCenterRefused(HelpCenterRefusal.Forbidden, [$"Permission '{permission}' is required."]),
        };
    }

    /// <summary>
    /// List authorization through the query planner. Help center records share one access shape per workspace
    /// (<see cref="HelpCenterResources"/>), so the plan is constant over rows: evaluating it once against that shape is exact and
    /// nothing is fetched before the decision.
    /// </summary>
    public static async Task<bool> CanListAsync(IAccessQueryPlanner planner, PrincipalRef actor, Permission permission, CancellationToken cancellationToken)
    {
        var filter = await planner.PlanAsync(new AccessQuery(actor, permission, actor.Scope), cancellationToken).ConfigureAwait(false);
        return filter is not DenyAllFilter && AccessFilterEvaluator.Matches(filter, HelpCenterResources.Workspace(actor.Scope));
    }

    public static HelpCenterRefused? ValidateRevision(long expectedRevision) =>
        expectedRevision < 1 ? Invalid("Expected revisions start at 1.") : null;

    public static HelpCenterRefused Invalid(string error) => new(HelpCenterRefusal.Invalid, [error]);

    public static HelpCenterRefused NotFound { get; } = new(HelpCenterRefusal.NotFound, ["The record does not exist."]);

    public static HelpCenterRefused Conflict(long currentRevision) =>
        new(HelpCenterRefusal.Conflict, ["The record changed or already exists."], currentRevision);

    /// <summary>Compare-and-set precheck on a loaded record.</summary>
    public static HelpCenterRefused? Fence(long? currentRevision, long expectedRevision) => currentRevision switch
    {
        null => NotFound,
        { } current when current != expectedRevision => Conflict(current),
        _ => null,
    };

    public static HelpCenterResult<T> Map<T>(HelpCenterWriteResult<T> result) => result switch
    {
        HelpCenterWriteApplied<T> applied => new HelpCenterSucceeded<T>(applied.Value),
        HelpCenterWriteNotFound => NotFound,
        HelpCenterWriteConflict conflict => Conflict(conflict.CurrentRevision),
        HelpCenterWriteRejected rejected => new HelpCenterRefused(HelpCenterRefusal.Rejected, [rejected.Reason switch
        {
            HelpCenterWriteRejection.CapacityExceeded => "A help center capacity limit would be exceeded.",
            HelpCenterWriteRejection.SlugConflict => "The slug is already in use.",
            HelpCenterWriteRejection.InvalidReference => "A referenced portal or collection does not exist, belongs to another portal, or cannot change.",
            HelpCenterWriteRejection.DepthExceeded => $"Collections nest at most {HelpCenterLimits.MaximumCollectionDepth} levels.",
            HelpCenterWriteRejection.NotEmpty => "The portal or collection still contains collections or articles.",
            HelpCenterWriteRejection.InvalidRevision => "The article revision is not valid.",
            _ => "The store rejected the write.",
        }]),
    };

    public static HelpCenterRefused? ValidateLocales(Portal portal, IEnumerable<HelpCenterLocale> locales) =>
        locales.All(portal.Supports) ? null : Invalid("Every translation must use a locale the portal supports.");
}
