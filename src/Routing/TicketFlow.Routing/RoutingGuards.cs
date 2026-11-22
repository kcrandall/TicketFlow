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

    public static RoutingResult<T> FromWrite<T>(RoutingWriteResult<T> result) => result switch
    {
        RoutingWriteApplied<T> applied => new RoutingValue<T>(applied.Value),
        RoutingWriteNotFound => RoutingNotFound.Instance,
        RoutingWriteConflict conflict => new RoutingConflict(conflict.CurrentRevision),
        RoutingWriteRejected rejected => RoutingRefused.Rejected($"The store refused the write ({rejected.Reason})."),
    };

    public static void RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }
}
