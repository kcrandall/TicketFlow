using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Automation;

internal static class AutomationGuards
{
    public static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    public static async Task<bool> PermittedAsync(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false)).IsPermitted;

    public static Task<bool> PermittedAsync(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, CancellationToken cancellationToken) =>
        PermittedAsync(evaluator, actor, permission, new AccessResource(actor.Scope), cancellationToken);

    public static AutomationResult<T> Map<T>(AutomationWriteResult<T> result) => result switch
    {
        AutomationWriteApplied<T> applied => new AutomationValue<T>(applied.Value),
        AutomationWriteNotFound => AutomationNotFound.Instance,
        AutomationWriteConflict conflict => new AutomationConflict(conflict.CurrentRevision),
        AutomationWriteRejected rejected => new AutomationRefused(AutomationRefusal.Rejected, [rejected.Reason switch
        {
            AutomationWriteRejection.CapacityExceeded => "The workspace capacity would be exceeded.",
            AutomationWriteRejection.DuplicateShortcut => "Another canned response uses the shortcut.",
            _ => "The store rejected the write.",
        }]),
    };
}
