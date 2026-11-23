using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Routing;

/// <summary>
/// Default <see cref="IRoutingQueueService"/>: plans a <c>conversation:read</c> filter and lets the queue store apply it inside its
/// bounded query, so a principal sees exactly the queued conversations whose point check would pass (no post-filtering).
/// </summary>
public sealed class RoutingQueueService(IAccessQueryPlanner planner, IRoutingQueueStore queue) : IRoutingQueueService
{
    public async Task<TicketResult<RoutingQueueListPage>> ListAsync(PrincipalRef actor, RoutingQueueQuery query, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var filter = await planner.PlanAsync(new AccessQuery(actor, RoutingPermissions.QueueRead, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return TicketFailure.Forbidden;
        var page = await queue.ListAsync(actor.Scope, query, filter, cancellationToken).ConfigureAwait(false);
        return new TicketValue<RoutingQueueListPage>(new RoutingQueueListPage(
            [.. page.Entries.Select(stored => stored.Entry).Select(entry => new RoutingQueueItem(entry.Conversation, entry.Snapshot.Inbox, entry.TargetInbox,
                entry.Snapshot.Team, entry.Snapshot.WaitingSince, entry.QueuedAt, entry.OverflowStage, entry.WaitExceeded))],
            page.Next));
    }
}
