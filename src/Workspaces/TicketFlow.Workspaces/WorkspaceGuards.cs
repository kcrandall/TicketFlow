using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>Shared authorization, validation and result mapping for workspace services.</summary>
internal static class WorkspaceGuards
{
    public static void RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    /// <summary>Point check through Access. Masked decisions permit the action: workspace records define no maskable fields.</summary>
    public static async Task<WorkspaceRefused?> AuthorizeAsync(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision switch
        {
            AccessAllowed => null,
            AccessMasked => null,
            AccessDenied => new WorkspaceRefused(WorkspaceRefusal.Forbidden, [$"Permission '{permission}' is required."]),
        };
    }

    public static WorkspaceRefused? ValidateRevision(long expectedRevision) =>
        expectedRevision < 1 ? Invalid("Expected revisions start at 1.") : null;

    public static WorkspaceRefused? ValidateTimeZone(string timeZoneId) =>
        WorkspaceTimeZones.TryResolve(timeZoneId, out _) ? null : Invalid("The time zone is not known on this host.");

    public static WorkspaceRefused Invalid(string error) => new(WorkspaceRefusal.Invalid, [error]);

    public static WorkspaceRefused NotFound { get; } = new(WorkspaceRefusal.NotFound, ["The record does not exist."]);

    public static WorkspaceRefused Conflict(long currentRevision) =>
        new(WorkspaceRefusal.Conflict, ["The record changed or already exists."], currentRevision);

    /// <summary>Compare-and-set precheck on a loaded record.</summary>
    public static WorkspaceRefused? Fence(long? currentRevision, long expectedRevision) => currentRevision switch
    {
        null => NotFound,
        { } current when current != expectedRevision => Conflict(current),
        _ => null,
    };

    public static WorkspaceResult<T> Map<T>(WorkspaceWriteResult<T> result) => result switch
    {
        WorkspaceWriteApplied<T> applied => new WorkspaceSucceeded<T>(applied.Value),
        WorkspaceWriteNotFound => NotFound,
        WorkspaceWriteConflict conflict => Conflict(conflict.CurrentRevision),
        WorkspaceWriteRejected rejected => new WorkspaceRefused(WorkspaceRefusal.Rejected, [rejected.Reason switch
        {
            WorkspaceWriteRejection.CapacityExceeded => "A workspace capacity limit would be exceeded.",
            WorkspaceWriteRejection.TeamMembershipLimitExceeded => $"A member would belong to more than {WorkspaceLimits.MaximumTeamsPerUser} teams.",
            WorkspaceWriteRejection.ChannelBindingInUse => "Another inbox is already bound to this channel configuration.",
            _ => "The store rejected the write.",
        }]),
    };

    public static WorkspaceResult<T> Map<T>(AccessChangeResult<T> result) => result switch
    {
        AccessChangeApplied<T> applied => new WorkspaceSucceeded<T>(applied.Value),
        AccessChangeRefused refused => new WorkspaceRefused(refused.Reason switch
        {
            AccessChangeRefusal.Forbidden => WorkspaceRefusal.Forbidden,
            AccessChangeRefusal.Invalid => WorkspaceRefusal.Invalid,
            AccessChangeRefusal.NotFound => WorkspaceRefusal.NotFound,
            AccessChangeRefusal.Conflict => WorkspaceRefusal.Conflict,
            AccessChangeRefusal.Rejected => WorkspaceRefusal.Rejected,
            _ => WorkspaceRefusal.Rejected,
        }, refused.Errors, refused.CurrentRevision),
    };
}
