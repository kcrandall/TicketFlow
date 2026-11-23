using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketFlow.Assist;
using TicketFlow.Automation;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Events;
using TicketFlow.Reporting;
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

internal static partial class TicketFlowBuiltInLoops
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
        {
            var follower = services.GetRequiredService<AutomationContactFollower>();
            var batch = Math.Min(loop.BatchSize, AutomationContactFollower.MaximumBatch);
            return ForEachWorkspaceAsync(services, TicketFlowLoops.AutomationContacts, loop, (scope, token) =>
                follower.CatchUpAsync(scope, batch, token), cancellationToken);
        }),
        new(TicketFlowLoops.ContactMerges, [typeof(ConversationContactMergeFollower), typeof(IWorkspaceDirectory), typeof(IWorkerCheckpointStore)], (services, loop, cancellationToken) =>
        {
            var checkpoints = services.GetRequiredService<IWorkerCheckpointStore>();
            var follower = services.GetRequiredService<ConversationContactMergeFollower>();
            var batch = loop.BatchSize;
            return ForEachWorkspaceAsync(services, TicketFlowLoops.ContactMerges, loop, async (scope, token) =>
            {
                var checkpoint = await checkpoints.GetAsync(scope, ContactMergeCheckpoint, token).ConfigureAwait(false);
                var position = await follower.CatchUpAsync(scope, checkpoint ?? 0, batch, token).ConfigureAwait(false);
                if (position <= (checkpoint ?? 0))
                    return 0;
                // A lost race means another node advanced the checkpoint over the same (idempotent) changes.
                return await checkpoints.SetAsync(scope, ContactMergeCheckpoint, position, checkpoint, token).ConfigureAwait(false) ? 1 : 0;
            }, cancellationToken);
        }),
        new(TicketFlowLoops.SurveyDispatch, [typeof(ISurveyDispatcher), typeof(IWorkspaceDirectory)], (services, loop, cancellationToken) =>
        {
            var dispatcher = services.GetRequiredService<ISurveyDispatcher>();
            var batch = Math.Min(loop.BatchSize, SurveyLimits.MaximumDuePerRead);
            return ForEachWorkspaceAsync(services, TicketFlowLoops.SurveyDispatch, loop, async (scope, token) =>
            {
                var summary = await dispatcher.DispatchDueAsync(scope, batch, token).ConfigureAwait(false);
                return summary.Sent + summary.Retrying + summary.Failed + summary.Cancelled;
            }, cancellationToken);
        }),
        new(TicketFlowLoops.LiveChatPruning, [typeof(ILiveChatSessionStore)], async (services, loop, cancellationToken) =>
        {
            var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
            return new TicketFlowLoopRun(await services.GetRequiredService<ILiveChatSessionStore>()
                .PruneExpiredAsync(now, Math.Min(loop.BatchSize, ILiveChatSessionStore.MaximumPrune), cancellationToken).ConfigureAwait(false));
        }),
        new(TicketFlowLoops.AssistMaintenance, [typeof(AssistMaintenance)], async (services, _, cancellationToken) =>
            new TicketFlowLoopRun(await services.GetRequiredService<AssistMaintenance>().RunOnceAsync(cancellationToken).ConfigureAwait(false))),
        new(TicketFlowLoops.EventDelivery, [typeof(EventDeliveryWorker)], async (services, _, cancellationToken) =>
        {
            var result = await services.GetRequiredService<EventDeliveryWorker>().RunOnceAsync(cancellationToken).ConfigureAwait(false);
            return new TicketFlowLoopRun(result.Delivered + result.Advanced + result.Retried + result.DeadLettered);
        }),
        new(TicketFlowLoops.EventRetention, [typeof(EventRetentionWorker)], async (services, _, cancellationToken) =>
        {
            var result = await services.GetRequiredService<EventRetentionWorker>().RunOnceAsync(cancellationToken).ConfigureAwait(false);
            return new TicketFlowLoopRun(result.EventsPruned + result.DeadLettersPruned);
        }),
        new(TicketFlowLoops.EventContacts, [typeof(EventContactFollower), typeof(IWorkspaceDirectory)], (services, loop, cancellationToken) =>
        {
            var follower = services.GetRequiredService<EventContactFollower>();
            var batch = Math.Min(loop.BatchSize, EventContactFollower.MaximumBatch);
            return ForEachWorkspaceAsync(services, TicketFlowLoops.EventContacts, loop, (scope, token) =>
                follower.CatchUpAsync(scope, batch, token), cancellationToken);
        }),
        new(TicketFlowLoops.EventSurveys, [typeof(EventSurveyFollower), typeof(IWorkspaceDirectory)], (services, loop, cancellationToken) =>
        {
            var follower = services.GetRequiredService<EventSurveyFollower>();
            return ForEachWorkspaceAsync(services, TicketFlowLoops.EventSurveys, loop, (scope, token) =>
                follower.CatchUpAsync(scope, token), cancellationToken);
        }),
        new(TicketFlowLoops.ReportingRetention, [typeof(ReportingRetentionWorker)], async (services, _, cancellationToken) =>
        {
            var result = await services.GetRequiredService<ReportingRetentionWorker>().RunOnceAsync(cancellationToken).ConfigureAwait(false);
            return new TicketFlowLoopRun(result.FactsPruned + result.RollupsPruned);
        }),
    ];

    /// <summary>
    /// Runs <paramref name="run"/> for up to <see cref="TicketFlowWorkerOptions.MaximumWorkspacesPerRun"/> workspaces of
    /// <see cref="IWorkspaceDirectory"/>, continuing from where the previous run stopped and wrapping at the end, so every workspace is
    /// visited across consecutive runs. A failing workspace is backed off on its own (exponentially from the loop's
    /// <see cref="TicketFlowLoopOptions.InitialBackoff"/> to <see cref="TicketFlowLoopOptions.MaximumBackoff"/>) and skipped until then;
    /// other workspaces keep the loop's cadence. Failures are reported as item failures (Degraded health), never as a failed run.
    /// </summary>
    public static async Task<TicketFlowLoopRun> ForEachWorkspaceAsync(IServiceProvider services, string loop, TicketFlowLoopOptions options,
        Func<TicketScope, CancellationToken, Task<int>> run, CancellationToken cancellationToken)
    {
        var directory = services.GetRequiredService<IWorkspaceDirectory>();
        var maximum = services.GetRequiredService<IOptions<TicketFlowWorkerOptions>>().Value.MaximumWorkspacesPerRun;
        var state = services.GetRequiredService<WorkspaceLoopStates>().For(loop);
        var time = services.GetRequiredService<TimeProvider>();
        ILogger? logger = null;
        int items = 0, failures = 0, visited = 0;
        var (after, skip) = state.Position;
        var startedAtBeginning = after is null && skip == 0;
        var wrapped = false;
        HashSet<TicketScope>? seen = startedAtBeginning ? null : [];
        while (true)
        {
            var page = await directory.ListWorkspacesAsync(after, Math.Min(200, IWorkspaceDirectory.MaximumPage), cancellationToken).ConfigureAwait(false);
            var index = 0;
            foreach (var scope in page.Scopes)
            {
                if (index++ < skip)
                    continue;
                if (visited >= maximum || (seen is not null && !seen.Add(scope)))
                {
                    state.Position = (after, index - 1);
                    return new TicketFlowLoopRun(items, failures);
                }
                visited++;
                if (!state.Due(scope, time))
                    continue;
                try
                {
                    items += await run(scope, cancellationToken).ConfigureAwait(false);
                    state.Succeeded(scope);
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failures++;
                    state.Failed(scope, time.GetUtcNow(), options);
                    logger ??= services.GetRequiredService<ILoggerFactory>().CreateLogger("TicketFlow.Hosting.Worker");
                    LogWorkspaceFailed(logger, loop, error.GetType().Name);
                }
            }
            skip = 0;
            if (page.Next is not null)
            {
                after = page.Next;
                continue;
            }
            // End of the directory: wrap once only when this run started mid-directory.
            after = null;
            if (wrapped || startedAtBeginning || page.Scopes.Count == 0)
                break;
            wrapped = true;
        }
        state.Position = (after, 0);
        return new TicketFlowLoopRun(items, failures);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "TicketFlow worker loop {Loop} failed for one workspace with {Error}; it is retried after a backoff.")]
    private static partial void LogWorkspaceFailed(ILogger logger, string loop, string error);
}

/// <summary>Per-loop directory position and per-workspace backoff of the per-workspace loops (process-local).</summary>
internal sealed class WorkspaceLoopStates
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WorkspaceLoopState> loops = new(StringComparer.Ordinal);

    public WorkspaceLoopState For(string loop) => loops.GetOrAdd(loop, static _ => new WorkspaceLoopState());
}

internal sealed class WorkspaceLoopState
{
    private const int MaximumTracked = 100_000;
    private readonly Lock gate = new();
    private readonly Dictionary<TicketScope, (int Failures, DateTimeOffset RetryAt)> backoff = [];
    private int backoffCount;
    private (string? After, int Skip) position;

    public (string? After, int Skip) Position
    {
        get { lock (gate) return position; }
        set { lock (gate) position = value; }
    }

    public bool Due(TicketScope scope, TimeProvider time)
    {
        if (Volatile.Read(ref backoffCount) == 0)
            return true;
        var now = time.GetUtcNow();
        lock (gate)
            return !backoff.TryGetValue(scope, out var entry) || entry.RetryAt <= now;
    }

    public void Succeeded(TicketScope scope)
    {
        if (Volatile.Read(ref backoffCount) == 0)
            return;
        lock (gate)
            if (backoff.Remove(scope))
                Volatile.Write(ref backoffCount, backoff.Count);
    }

    public void Failed(TicketScope scope, DateTimeOffset now, TicketFlowLoopOptions options)
    {
        lock (gate)
        {
            var failures = backoff.TryGetValue(scope, out var entry) ? entry.Failures + 1 : 1;
            if (failures == 1 && backoff.Count >= MaximumTracked)
                return;
            var delay = TimeSpan.FromMilliseconds(Math.Min(options.InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 30)), options.MaximumBackoff.TotalMilliseconds));
            backoff[scope] = (failures, now + delay);
            Volatile.Write(ref backoffCount, backoff.Count);
        }
    }
}
