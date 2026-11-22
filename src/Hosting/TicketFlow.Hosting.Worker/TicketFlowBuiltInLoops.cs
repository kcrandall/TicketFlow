using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketFlow.Assist;
using TicketFlow.Automation;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.Worker;

/// <summary>Tracks whether this process registered the outbox consumers (consumers receive entries committed after registration).</summary>
internal sealed class OutboxRegistration
{
    public bool Registered { get; set; }
}

internal static class TicketFlowBuiltInLoops
{
    /// <summary>Name of the contact-merge catch-up checkpoint in <see cref="IWorkerCheckpointStore"/>.</summary>
    public const string ContactMergeCheckpoint = "contact-merges";

    public static IEnumerable<TicketFlowLoopDefinition> All() =>
    [
        new(TicketFlowLoops.Outbox, [typeof(ConversationOutboxDispatcher), typeof(IConversationOutbox)], async (services, _, cancellationToken) =>
        {
            var dispatcher = services.GetRequiredService<ConversationOutboxDispatcher>();
            var registration = services.GetRequiredService<OutboxRegistration>();
            if (!registration.Registered)
            {
                await dispatcher.RegisterAsync(cancellationToken).ConfigureAwait(false);
                registration.Registered = true;
            }
            var result = await dispatcher.RunOnceAsync(cancellationToken).ConfigureAwait(false);
            return new TicketFlowLoopRun(result.Handled + result.Retried + result.DeadLettered);
        }),
        new(TicketFlowLoops.OutboxPruning, [typeof(IConversationOutbox)], async (services, loop, cancellationToken) =>
        {
            var options = services.GetRequiredService<IOptions<TicketFlowWorkerOptions>>().Value;
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            return new TicketFlowLoopRun(await services.GetRequiredService<IConversationOutbox>()
                .PruneAsync(now - options.OutboxRetention, Math.Min(loop.BatchSize, IConversationOutbox.MaximumPrune), cancellationToken).ConfigureAwait(false));
        }),
        new(TicketFlowLoops.RoutingMaintenance, [typeof(RoutingMaintenanceWorker)], async (services, _, cancellationToken) =>
        {
            var result = await services.GetRequiredService<RoutingMaintenanceWorker>().RunOnceAsync(cancellationToken).ConfigureAwait(false);
            // Still-queued conversations are not progress; counting them would spin the drain loop.
            return new TicketFlowLoopRun(result.Routed + result.Overflowed + result.WaitExceeded + result.PresenceExpired + result.Reassigned + result.Pruned);
        }),
        new(TicketFlowLoops.SlaTimers, [typeof(SlaTimerScheduler)], async (services, _, cancellationToken) =>
            new TicketFlowLoopRun((await services.GetRequiredService<SlaTimerScheduler>().RunOnceAsync(cancellationToken).ConfigureAwait(false)).Evaluated)),
        new(TicketFlowLoops.AutomationSchedules, [typeof(AutomationScheduler)], async (services, _, cancellationToken) =>
            new TicketFlowLoopRun((await services.GetRequiredService<AutomationScheduler>().RunOnceAsync(cancellationToken).ConfigureAwait(false)).Fired)),
        new(TicketFlowLoops.AutomationContacts, [typeof(AutomationContactFollower), typeof(IWorkspaceDirectory)], (services, loop, cancellationToken) =>
            ForEachWorkspaceAsync(services, TicketFlowLoops.AutomationContacts, (scope, token) =>
                services.GetRequiredService<AutomationContactFollower>().CatchUpAsync(scope, Math.Min(loop.BatchSize, AutomationContactFollower.MaximumBatch), token), cancellationToken)),
        new(TicketFlowLoops.ContactMerges, [typeof(ConversationContactMergeFollower), typeof(IWorkspaceDirectory), typeof(IWorkerCheckpointStore)], (services, loop, cancellationToken) =>
            ForEachWorkspaceAsync(services, TicketFlowLoops.ContactMerges, async (scope, token) =>
            {
                var checkpoints = services.GetRequiredService<IWorkerCheckpointStore>();
                var checkpoint = await checkpoints.GetAsync(scope, ContactMergeCheckpoint, token).ConfigureAwait(false);
                var position = await services.GetRequiredService<ConversationContactMergeFollower>().CatchUpAsync(scope, checkpoint ?? 0, loop.BatchSize, token).ConfigureAwait(false);
                if (position <= (checkpoint ?? 0))
                    return 0;
                // A lost race means another node advanced the checkpoint over the same (idempotent) changes.
                return await checkpoints.SetAsync(scope, ContactMergeCheckpoint, position, checkpoint, token).ConfigureAwait(false) ? 1 : 0;
            }, cancellationToken)),
        new(TicketFlowLoops.SurveyDispatch, [typeof(ISurveyDispatcher), typeof(IWorkspaceDirectory)], (services, loop, cancellationToken) =>
            ForEachWorkspaceAsync(services, TicketFlowLoops.SurveyDispatch, async (scope, token) =>
            {
                var summary = await services.GetRequiredService<ISurveyDispatcher>().DispatchDueAsync(scope, Math.Min(loop.BatchSize, SurveyLimits.MaximumDuePerRead), token).ConfigureAwait(false);
                return summary.Sent + summary.Retrying + summary.Failed + summary.Cancelled;
            }, cancellationToken)),
        new(TicketFlowLoops.LiveChatPruning, [typeof(ILiveChatSessionStore)], async (services, loop, cancellationToken) =>
        {
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            return new TicketFlowLoopRun(await services.GetRequiredService<ILiveChatSessionStore>()
                .PruneExpiredAsync(now, Math.Min(loop.BatchSize, ILiveChatSessionStore.MaximumPrune), cancellationToken).ConfigureAwait(false));
        }),
        new(TicketFlowLoops.AssistMaintenance, [typeof(AssistMaintenance)], async (services, _, cancellationToken) =>
            new TicketFlowLoopRun(await services.GetRequiredService<AssistMaintenance>().RunOnceAsync(cancellationToken).ConfigureAwait(false))),
    ];

    /// <summary>
    /// Runs <paramref name="run"/> for every workspace of <see cref="IWorkspaceDirectory"/> (bounded by
    /// <see cref="TicketFlowWorkerOptions.MaximumWorkspacesPerRun"/>). A failing workspace is counted and skipped so others still progress.
    /// </summary>
    public static async Task<TicketFlowLoopRun> ForEachWorkspaceAsync(IServiceProvider services, string loop, Func<TicketScope, CancellationToken, Task<int>> run, CancellationToken cancellationToken)
    {
        var directory = services.GetRequiredService<IWorkspaceDirectory>();
        var maximum = services.GetRequiredService<IOptions<TicketFlowWorkerOptions>>().Value.MaximumWorkspacesPerRun;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TicketFlow.Hosting.Worker");
        int items = 0, failures = 0, visited = 0;
        string? after = null;
        do
        {
            var page = await directory.ListWorkspacesAsync(after, Math.Min(200, IWorkspaceDirectory.MaximumPage), cancellationToken).ConfigureAwait(false);
            foreach (var scope in page.Scopes)
            {
                if (visited++ >= maximum)
                    return new TicketFlowLoopRun(items, failures);
                try
                {
                    items += await run(scope, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failures++;
                    logger.LogWarning("TicketFlow worker loop {Loop} failed for one workspace with {Error}.", loop, error.GetType().Name);
                }
            }
            after = page.Next;
        }
        while (after is not null);
        return new TicketFlowLoopRun(items, failures);
    }
}
