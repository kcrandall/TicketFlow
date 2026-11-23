using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace TicketFlow.Hosting.Worker;

public enum TicketFlowLoopStatus
{
    /// <summary>Registered but not started yet.</summary>
    Pending = 0,
    Running = 1,
    Disabled = 2,

    /// <summary>The loop's library service is not registered, so it does not run.</summary>
    NotConfigured = 3,
    Stopped = 4,
}

/// <summary>
/// A loop's health snapshot (no customer data). <see cref="ConsecutiveFailures"/> counts failed runs; <see cref="LastItemFailures"/> counts
/// isolated item failures (for example workspaces) of the last completed run.
/// </summary>
public sealed record TicketFlowLoopState(string Loop, TicketFlowLoopStatus Status, int ConsecutiveFailures, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt,
    int LastItems, int LastItemFailures = 0, DateTimeOffset? StartedAt = null, TimeSpan Interval = default);

/// <summary>Tracks every loop's state for <see cref="TicketFlowWorkerHealthCheck"/> and diagnostics.</summary>
public sealed class TicketFlowWorkerHealth(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, LoopSlot> loops = new(StringComparer.Ordinal);

    public IReadOnlyList<TicketFlowLoopState> Loops
    {
        get
        {
            var list = new List<TicketFlowLoopState>(loops.Count);
            foreach (var slot in loops.Values)
                list.Add(slot.Snapshot());
            list.Sort(static (left, right) => string.CompareOrdinal(left.Loop, right.Loop));
            return list;
        }
    }

    public TicketFlowLoopState? Find(string loop) => loops.TryGetValue(loop, out var slot) ? slot.Snapshot() : null;

    internal DateTimeOffset Now => time.GetUtcNow();

    internal void Register(string loop) => loops.TryAdd(loop, new LoopSlot(loop, TicketFlowLoopStatus.Pending));

    internal void Set(string loop, TicketFlowLoopStatus status) => Slot(loop, status).Set(status);

    internal void Started(string loop, TimeSpan interval) => Slot(loop, TicketFlowLoopStatus.Running).Started(time.GetUtcNow(), interval);

    internal void Succeeded(string loop, int items, int itemFailures = 0) => Slot(loop, TicketFlowLoopStatus.Running).Succeeded(time.GetUtcNow(), items, itemFailures);

    internal void Failed(string loop, int consecutive) => Slot(loop, TicketFlowLoopStatus.Running).Failed(time.GetUtcNow(), consecutive);

    private LoopSlot Slot(string loop, TicketFlowLoopStatus initialStatus) =>
        loops.TryGetValue(loop, out var existing) ? existing : loops.GetOrAdd(loop, static (name, status) => new LoopSlot(name, status), initialStatus);

    private sealed class LoopSlot(string loop, TicketFlowLoopStatus status)
    {
        private readonly Lock gate = new();
        private TicketFlowLoopStatus status = status;
        private int consecutiveFailures;
        private DateTimeOffset? lastSuccessAt;
        private DateTimeOffset? lastFailureAt;
        private int lastItems;
        private int lastItemFailures;
        private DateTimeOffset? startedAt;
        private TimeSpan interval;

        public void Set(TicketFlowLoopStatus value)
        {
            lock (gate)
                status = value;
        }

        public void Started(DateTimeOffset now, TimeSpan intervalValue)
        {
            lock (gate)
            {
                status = TicketFlowLoopStatus.Running;
                startedAt = now;
                interval = intervalValue;
            }
        }

        public void Succeeded(DateTimeOffset now, int items, int itemFailures)
        {
            lock (gate)
            {
                consecutiveFailures = 0;
                lastSuccessAt = now;
                lastItems = items;
                lastItemFailures = itemFailures;
            }
        }

        public void Failed(DateTimeOffset now, int consecutive)
        {
            lock (gate)
            {
                consecutiveFailures = consecutive;
                lastFailureAt = now;
            }
        }

        public TicketFlowLoopState Snapshot()
        {
            lock (gate)
                return new TicketFlowLoopState(loop, status, consecutiveFailures, lastSuccessAt, lastFailureAt, lastItems, lastItemFailures, startedAt, interval);
        }
    }
}

/// <summary>
/// Unhealthy when a running loop failed <see cref="TicketFlowWorkerOptions.UnhealthyAfterFailures"/> runs in a row. Degraded after any
/// failed run, when the last run isolated item failures, or when a running loop has not completed a run for
/// <see cref="TicketFlowWorkerOptions.StaleAfterIntervals"/> intervals (at least one minute). Data lists each loop's status and counters.
/// </summary>
public sealed class TicketFlowWorkerHealthCheck(TicketFlowWorkerHealth health, IOptions<TicketFlowWorkerOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = health.Now;
        var loops = health.Loops;
        var data = new Dictionary<string, object>(loops.Count, StringComparer.Ordinal);
        bool unhealthy = false, anyFailed = false, anyItemFailures = false, anyStale = false;
        for (var i = 0; i < loops.Count; i++)
        {
            var state = loops[i];
            data[state.Loop] = $"{state.Status}:{state.ConsecutiveFailures}:{state.LastItemFailures}";
            if (state.Status != TicketFlowLoopStatus.Running)
                continue;
            if (state.ConsecutiveFailures >= settings.UnhealthyAfterFailures)
                unhealthy = true;
            else if (state.ConsecutiveFailures > 0)
                anyFailed = true;
            if (state.LastItemFailures > 0)
                anyItemFailures = true;
            if (Stale(state, now, settings.StaleAfterIntervals))
                anyStale = true;
        }
        if (unhealthy)
            return Task.FromResult(HealthCheckResult.Unhealthy("A TicketFlow worker loop keeps failing.", data: data));
        if (anyFailed)
            return Task.FromResult(HealthCheckResult.Degraded("A TicketFlow worker loop failed recently.", data: data));
        if (anyItemFailures)
            return Task.FromResult(HealthCheckResult.Degraded("A TicketFlow worker loop skipped failing items.", data: data));
        if (anyStale)
            return Task.FromResult(HealthCheckResult.Degraded("A TicketFlow worker loop has not completed a run recently.", data: data));
        return Task.FromResult(HealthCheckResult.Healthy("TicketFlow worker loops are healthy.", data));
    }

    private static bool Stale(TicketFlowLoopState state, DateTimeOffset now, int intervals)
    {
        if ((state.LastSuccessAt ?? state.StartedAt) is not { } since || state.Interval <= TimeSpan.Zero)
            return false;
        var limit = state.Interval * intervals;
        return now - since > (limit < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : limit);
    }
}
