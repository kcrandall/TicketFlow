using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Automation;

internal static class AutomationGuards
{
    public static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    public static async Task<bool> PermittedAsync(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false)).IsPermitted;

    public static Task<bool> PermittedAsync(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, CancellationToken cancellationToken) =>
        PermittedAsync(evaluator, actor, permission, new AccessResource(actor.Scope), cancellationToken);

    public static TicketResult<T> Map<T>(StoreWriteResult<T, AutomationWriteRejection> result) => result switch
    {
        StoreApplied<T> applied => new TicketValue<T>(applied.Value),
        StoreNotFound => TicketFailure.NotFound,
        StoreConflict conflict => TicketFailure.Conflict(conflict.CurrentRevision),
        StoreRejected<AutomationWriteRejection> rejected => new TicketFailure(TicketFailureKind.Rejected, [rejected.Reason switch
        {
            AutomationWriteRejection.CapacityExceeded => "The workspace capacity would be exceeded.",
            AutomationWriteRejection.DuplicateShortcut => "Another canned response uses the shortcut.",
            _ => "The store rejected the write.",
        }]),
    };
}
