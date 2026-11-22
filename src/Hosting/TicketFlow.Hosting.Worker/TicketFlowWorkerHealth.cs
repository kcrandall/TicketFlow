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
    private readonly ConcurrentDictionary<string, TicketFlowLoopState> loops = new(StringComparer.Ordinal);

    public IReadOnlyList<TicketFlowLoopState> Loops => [.. loops.Values.OrderBy(state => state.Loop, StringComparer.Ordinal)];

    public TicketFlowLoopState? Find(string loop) => loops.GetValueOrDefault(loop);

    internal DateTimeOffset Now => time.GetUtcNow();

    internal void Register(string loop) => loops.TryAdd(loop, new TicketFlowLoopState(loop, TicketFlowLoopStatus.Pending, 0, null, null, 0));

    internal void Set(string loop, TicketFlowLoopStatus status) =>
        loops.AddOrUpdate(loop, name => new TicketFlowLoopState(name, status, 0, null, null, 0), (_, state) => state with { Status = status });

    internal void Started(string loop, TimeSpan interval) =>
        loops.AddOrUpdate(loop, name => new TicketFlowLoopState(name, TicketFlowLoopStatus.Running, 0, null, null, 0, 0, time.GetUtcNow(), interval),
            (_, state) => state with { Status = TicketFlowLoopStatus.Running, StartedAt = time.GetUtcNow(), Interval = interval });

    internal void Succeeded(string loop, int items, int itemFailures = 0) =>
        loops.AddOrUpdate(loop, name => new TicketFlowLoopState(name, TicketFlowLoopStatus.Running, 0, time.GetUtcNow(), null, items, itemFailures),
            (_, state) => state with { ConsecutiveFailures = 0, LastSuccessAt = time.GetUtcNow(), LastItems = items, LastItemFailures = itemFailures });

    internal void Failed(string loop, int consecutive) =>
        loops.AddOrUpdate(loop, name => new TicketFlowLoopState(name, TicketFlowLoopStatus.Running, consecutive, null, time.GetUtcNow(), 0),
            (_, state) => state with { ConsecutiveFailures = consecutive, LastFailureAt = time.GetUtcNow() });
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
        var data = loops.ToDictionary(state => state.Loop, state => (object)$"{state.Status}:{state.ConsecutiveFailures}:{state.LastItemFailures}", StringComparer.Ordinal);
        var running = loops.Where(state => state.Status == TicketFlowLoopStatus.Running).ToList();
        if (running.Any(state => state.ConsecutiveFailures >= settings.UnhealthyAfterFailures))
            return Task.FromResult(HealthCheckResult.Unhealthy("A TicketFlow worker loop keeps failing.", data: data));
        if (running.Any(state => state.ConsecutiveFailures > 0))
            return Task.FromResult(HealthCheckResult.Degraded("A TicketFlow worker loop failed recently.", data: data));
        if (running.Any(state => state.LastItemFailures > 0))
            return Task.FromResult(HealthCheckResult.Degraded("A TicketFlow worker loop skipped failing items.", data: data));
        if (running.Any(state => Stale(state, now, settings.StaleAfterIntervals)))
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
