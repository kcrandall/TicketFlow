using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.Workspaces.WorkspaceGuards;

namespace TicketFlow.Workspaces;

/// <summary>Default <see cref="IBusinessCalendarService"/>: reads need <c>workspace:read</c>, writes need <c>calendar:manage</c>.</summary>
public sealed class BusinessCalendarService(IBusinessCalendarStore store, IAccessEvaluator evaluator) : IBusinessCalendarService
{
    private readonly IBusinessCalendarStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<TicketResult<StoredBusinessCalendar>> GetAsync(PrincipalRef actor, BusinessCalendarId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        if (await ReadAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await store.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } calendar
            ? new TicketValue<StoredBusinessCalendar>(calendar)
            : NotFound;
    }

    public async Task<TicketResult<IReadOnlyList<StoredBusinessCalendar>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (await ReadAsync(actor, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return new TicketValue<IReadOnlyList<StoredBusinessCalendar>>(await store.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredBusinessCalendar>> CreateAsync(PrincipalRef actor, BusinessCalendar calendar, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(calendar);
        var refused = await ManageAsync(actor, cancellationToken).ConfigureAwait(false) ?? ValidateTimeZone(calendar.TimeZoneId);
        if (refused is not null)
            return refused;
        return Map(await store.CreateAsync(actor.Scope, calendar, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredBusinessCalendar>> UpdateAsync(PrincipalRef actor, BusinessCalendar calendar, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(calendar);
        var refused = await ManageAsync(actor, cancellationToken).ConfigureAwait(false) ?? ValidateRevision(expectedRevision) ?? ValidateTimeZone(calendar.TimeZoneId);
        if (refused is not null)
            return refused;
        return Map(await store.UpdateAsync(actor.Scope, calendar, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredBusinessCalendar>> DeleteAsync(PrincipalRef actor, BusinessCalendarId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        var refused = await ManageAsync(actor, cancellationToken).ConfigureAwait(false) ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        return Map(await store.DeleteAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private Task<TicketFailure?> ReadAsync(PrincipalRef actor, CancellationToken cancellationToken) =>
        AuthorizeAsync(evaluator, actor, WorkspacePermissions.WorkspaceRead, WorkspaceResources.Workspace(actor.Scope), cancellationToken);

    private Task<TicketFailure?> ManageAsync(PrincipalRef actor, CancellationToken cancellationToken) =>
        AuthorizeAsync(evaluator, actor, WorkspacePermissions.CalendarManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken);

    private static void RequireId(BusinessCalendarId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A calendar identifier is required.", nameof(id));
    }
}
