using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>Shared authorization, validation and result mapping for workspace services.</summary>
internal static class WorkspaceGuards
{
    public static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    /// <summary>Point check through Access. Masked decisions permit the action: workspace records define no maskable fields.</summary>
    public static async Task<TicketFailure?> AuthorizeAsync(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision switch
        {
            AccessAllowed => null,
            AccessMasked => null,
            AccessDenied => new TicketFailure(TicketFailureKind.Forbidden, [$"Permission '{permission}' is required."]),
        };
    }

    public static TicketFailure? ValidateRevision(long expectedRevision) =>
        expectedRevision < 1 ? Invalid("Expected revisions start at 1.") : null;

    public static TicketFailure? ValidateTimeZone(string timeZoneId) =>
        WorkspaceTimeZones.TryResolve(timeZoneId, out _) ? null : Invalid("The time zone is not known on this host.");

    public static TicketFailure Invalid(string error) => new(TicketFailureKind.Invalid, [error]);

    public static TicketFailure NotFound { get; } = new(TicketFailureKind.NotFound, ["The record does not exist."]);

    public static TicketFailure Conflict(long? currentRevision) =>
        new(TicketFailureKind.Conflict, ["The record changed or already exists."], currentRevision);

    /// <summary>Compare-and-set precheck on a loaded record.</summary>
    public static TicketFailure? Fence(long? currentRevision, long expectedRevision) => currentRevision switch
    {
        null => NotFound,
        { } current when current != expectedRevision => Conflict(current),
        _ => null,
    };

    public static TicketResult<T> Map<T>(StoreWriteResult<T, WorkspaceWriteRejection> result) => result switch
    {
        StoreApplied<T> applied => new TicketValue<T>(applied.Value),
        StoreNotFound => NotFound,
        StoreConflict conflict => Conflict(conflict.CurrentRevision),
        StoreRejected<WorkspaceWriteRejection> rejected => new TicketFailure(TicketFailureKind.Rejected, [rejected.Reason switch
        {
            WorkspaceWriteRejection.CapacityExceeded => "A workspace capacity limit would be exceeded.",
            WorkspaceWriteRejection.TeamMembershipLimitExceeded => $"A member would belong to more than {WorkspaceLimits.MaximumTeamsPerUser} teams.",
            WorkspaceWriteRejection.ChannelBindingInUse => "Another inbox is already bound to this channel configuration.",
            _ => "The store rejected the write.",
        }]),
    };

    public static TicketResult<T> Map<T>(TicketResult<AccessChangeApplied<T>> result) => result switch
    {
        TicketValue<AccessChangeApplied<T>> applied => new TicketValue<T>(applied.Value.Value),
        TicketFailure failure => failure,
    };
}
