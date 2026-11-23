using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Routing;

internal static class RoutingGuards
{
    public static async Task<bool> PermittedAsync(IAccessEvaluator access, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken)
    {
        var decision = await access.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted;
    }

    public static TicketResult<T> FromWrite<T>(StoreWriteResult<T, RoutingWriteRejection> result) => result switch
    {
        StoreApplied<T> applied => new TicketValue<T>(applied.Value),
        StoreNotFound => TicketFailure.NotFound,
        StoreConflict conflict => TicketFailure.Conflict(conflict.CurrentRevision),
        StoreRejected<RoutingWriteRejection> rejected => TicketFailure.Rejected($"The store refused the write ({rejected.Reason})."),
    };

    public static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }
}
