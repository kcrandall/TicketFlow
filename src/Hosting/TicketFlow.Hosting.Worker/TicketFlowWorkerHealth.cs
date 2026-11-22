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

/// <summary>A loop's health snapshot (no customer data).</summary>
public sealed record TicketFlowLoopState(string Loop, TicketFlowLoopStatus Status, int ConsecutiveFailures, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, int LastItems);

/// <summary>Tracks every loop's state for <see cref="TicketFlowWorkerHealthCheck"/> and diagnostics.</summary>
public sealed class TicketFlowWorkerHealth(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, TicketFlowLoopState> loops = new(StringComparer.Ordinal);

    public IReadOnlyList<TicketFlowLoopState> Loops => [.. loops.Values.OrderBy(state => state.Loop, StringComparer.Ordinal)];

    public TicketFlowLoopState? Find(string loop) => loops.GetValueOrDefault(loop);

    internal void Register(string loop) => loops.TryAdd(loop, new TicketFlowLoopState(loop, TicketFlowLoopStatus.Pending, 0, null, null, 0));

    internal void Set(string loop, TicketFlowLoopStatus status) =>
        loops.AddOrUpdate(loop, name => new TicketFlowLoopState(name, status, 0, null, null, 0), (_, state) => state with { Status = status });

    internal void Succeeded(string loop, int items) =>
        loops.AddOrUpdate(loop, name => new TicketFlowLoopState(name, TicketFlowLoopStatus.Running, 0, time.GetUtcNow(), null, items),
            (_, state) => state with { ConsecutiveFailures = 0, LastSuccessAt = time.GetUtcNow(), LastItems = items });

    internal void Failed(string loop, int consecutive) =>
        loops.AddOrUpdate(loop, name => new TicketFlowLoopState(name, TicketFlowLoopStatus.Running, consecutive, null, time.GetUtcNow(), 0),
            (_, state) => state with { ConsecutiveFailures = consecutive, LastFailureAt = time.GetUtcNow() });
}

/// <summary>
/// Unhealthy when a running loop failed <see cref="TicketFlowWorkerOptions.UnhealthyAfterFailures"/> times in a row, degraded after any
/// consecutive failure, healthy otherwise. Data lists each loop's status and failure count.
/// </summary>
public sealed class TicketFlowWorkerHealthCheck(TicketFlowWorkerHealth health, IOptions<TicketFlowWorkerOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var loops = health.Loops;
        var data = loops.ToDictionary(state => state.Loop, state => (object)$"{state.Status}:{state.ConsecutiveFailures}", StringComparer.Ordinal);
        var worst = loops.Where(state => state.Status == TicketFlowLoopStatus.Running).Select(state => state.ConsecutiveFailures).DefaultIfEmpty(0).Max();
        if (worst >= options.Value.UnhealthyAfterFailures)
            return Task.FromResult(HealthCheckResult.Unhealthy("A TicketFlow worker loop keeps failing.", data: data));
        if (worst > 0)
            return Task.FromResult(HealthCheckResult.Degraded("A TicketFlow worker loop failed recently.", data: data));
        return Task.FromResult(HealthCheckResult.Healthy("TicketFlow worker loops are healthy.", data));
    }
}
