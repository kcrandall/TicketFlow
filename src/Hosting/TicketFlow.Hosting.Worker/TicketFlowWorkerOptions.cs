namespace TicketFlow.Hosting.Worker;

/// <summary>Scheduling of one loop: whether it runs, how often, how much per run and how it backs off after failures.</summary>
public sealed class TicketFlowLoopOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Delay between runs that found no work (and after a drained burst).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Items per run where the library call takes a batch size (claims, catch-up reads, prunes).</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>First failure backoff, doubled per consecutive failure up to <see cref="MaximumBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Consecutive runs started without waiting while each run reports work (bounded catch-up), before waiting <see cref="Interval"/>.</summary>
    public int MaximumDrainRuns { get; set; } = 10;

    internal void Validate(string name)
    {
        if (Interval <= TimeSpan.Zero || Interval > TimeSpan.FromDays(1) || BatchSize is < 1 or > 10_000 || InitialBackoff <= TimeSpan.Zero
            || MaximumBackoff < InitialBackoff || MaximumBackoff > TimeSpan.FromDays(1) || MaximumDrainRuns is < 0 or > 1000)
            throw new InvalidOperationException($"TicketFlow worker loop options for '{name}' are invalid.");
    }

    internal static TicketFlowLoopOptions Create(TimeSpan interval, int batchSize) => new() { Interval = interval, BatchSize = batchSize };
}

/// <summary>
/// Options of every built-in loop. Loops whose library service is not registered disable themselves. Batch sizes are clamped to the
/// library maximum of each call.
/// </summary>
public sealed class TicketFlowWorkerOptions
{
    /// <summary>All registered outbox consumers (channels.dispatch, routing, sla, automation, realtime, surveys.resolved, ...); batch from <c>ConversationOutboxDispatcherOptions</c>.</summary>
    public TicketFlowLoopOptions Outbox { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromSeconds(1), 50);

    /// <summary>Deletes completed outbox entries older than <see cref="OutboxRetention"/> (batch ≤ 10 000).</summary>
    public TicketFlowLoopOptions OutboxPruning { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromHours(1), 5000);

    /// <summary>Age after which fully delivered outbox entries are pruned (default 7 days).</summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary><c>RoutingMaintenanceWorker.RunOnceAsync</c> (batch from <c>RoutingOptions</c>).</summary>
    public TicketFlowLoopOptions RoutingMaintenance { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromSeconds(5), 100);

    /// <summary><c>SlaTimerScheduler.RunOnceAsync</c> (batch from <c>SlaOptions</c>).</summary>
    public TicketFlowLoopOptions SlaTimers { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromSeconds(5), 100);

    /// <summary><c>AutomationScheduler.RunOnceAsync</c> (batch from <c>AutomationOptions</c>).</summary>
    public TicketFlowLoopOptions AutomationSchedules { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromSeconds(15), 20);

    /// <summary><c>AutomationContactFollower.CatchUpAsync</c> per workspace (batch ≤ 500).</summary>
    public TicketFlowLoopOptions AutomationContacts { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromSeconds(10), 100);

    /// <summary><c>ConversationContactMergeFollower.CatchUpAsync</c> per workspace with a durable checkpoint.</summary>
    public TicketFlowLoopOptions ContactMerges { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromSeconds(10), 1000);

    /// <summary><c>ISurveyDispatcher.DispatchDueAsync</c> per workspace (batch ≤ 500).</summary>
    public TicketFlowLoopOptions SurveyDispatch { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromSeconds(30), 100);

    /// <summary><c>ILiveChatSessionStore.PruneExpiredAsync</c> (batch ≤ 10 000).</summary>
    public TicketFlowLoopOptions LiveChatPruning { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromHours(1), 1000);

    /// <summary><c>AssistMaintenance.RunOnceAsync</c> (abandoned usage reservations).</summary>
    public TicketFlowLoopOptions AssistMaintenance { get; } = TicketFlowLoopOptions.Create(TimeSpan.FromMinutes(5), 500);

    /// <summary>Options of loops added with <c>AddTicketFlowWorkerLoop</c>, keyed by loop name (created with defaults on first use).</summary>
    public IDictionary<string, TicketFlowLoopOptions> Custom { get; } = new Dictionary<string, TicketFlowLoopOptions>(StringComparer.Ordinal);

    /// <summary>Workspaces enumerated per run of per-workspace loops (default 10 000).</summary>
    public int MaximumWorkspacesPerRun { get; set; } = 10_000;

    /// <summary>Relative random spread applied to every delay (0-0.5, default 0.2), so nodes do not run in lockstep.</summary>
    public double Jitter { get; set; } = 0.2;

    /// <summary>Consecutive failed runs after which the health check reports the loop unhealthy (default 5; fewer report degraded).</summary>
    public int UnhealthyAfterFailures { get; set; } = 5;

    internal void Validate()
    {
        if (OutboxRetention < TimeSpan.FromMinutes(1) || MaximumWorkspacesPerRun < 1 || Jitter is < 0 or > 0.5 || double.IsNaN(Jitter) || UnhealthyAfterFailures < 1)
            throw new InvalidOperationException("TicketFlow worker options are invalid.");
    }

    internal TicketFlowLoopOptions For(string name) => name switch
    {
        TicketFlowLoops.Outbox => Outbox,
        TicketFlowLoops.OutboxPruning => OutboxPruning,
        TicketFlowLoops.RoutingMaintenance => RoutingMaintenance,
        TicketFlowLoops.SlaTimers => SlaTimers,
        TicketFlowLoops.AutomationSchedules => AutomationSchedules,
        TicketFlowLoops.AutomationContacts => AutomationContacts,
        TicketFlowLoops.ContactMerges => ContactMerges,
        TicketFlowLoops.SurveyDispatch => SurveyDispatch,
        TicketFlowLoops.LiveChatPruning => LiveChatPruning,
        TicketFlowLoops.AssistMaintenance => AssistMaintenance,
        _ => Custom.TryGetValue(name, out var custom) ? custom : Custom[name] = new TicketFlowLoopOptions(),
    };
}

/// <summary>Built-in loop names (used in options, health data, logs and metric tags).</summary>
public static class TicketFlowLoops
{
    public const string Outbox = "outbox";
    public const string OutboxPruning = "outbox-pruning";
    public const string RoutingMaintenance = "routing-maintenance";
    public const string SlaTimers = "sla-timers";
    public const string AutomationSchedules = "automation-schedules";
    public const string AutomationContacts = "automation-contacts";
    public const string ContactMerges = "contact-merges";
    public const string SurveyDispatch = "survey-dispatch";
    public const string LiveChatPruning = "livechat-pruning";
    public const string AssistMaintenance = "assist-maintenance";
}
