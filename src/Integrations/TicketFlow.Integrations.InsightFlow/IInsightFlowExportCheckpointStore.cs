using TicketFlow.Core;
using TicketFlow.Reporting;

namespace TicketFlow.Integrations.InsightFlow;

/// <summary>Per-workspace export cursor persisted between InsightFlow export runs.</summary>
public sealed record InsightFlowExportCheckpoint(DateTimeOffset From, ReportFactCursor? Cursor, long TotalExported, DateTimeOffset UpdatedAt);

/// <summary>Durable checkpoint store for <see cref="InsightFlowReportingExporter"/> with compare-and-set updates.</summary>
public interface IInsightFlowExportCheckpointStore
{
    Task<InsightFlowExportCheckpoint?> GetAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<bool> SetAsync(TicketScope scope, InsightFlowExportCheckpoint next, InsightFlowExportCheckpoint? expected, CancellationToken cancellationToken = default);
}

/// <summary>In-memory development and test checkpoint store.</summary>
public sealed class InMemoryInsightFlowExportCheckpointStore : IInsightFlowExportCheckpointStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<TicketScope, InsightFlowExportCheckpoint> checkpoints = [];

    public Task<InsightFlowExportCheckpoint?> GetAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(checkpoints.TryGetValue(scope, out var value) ? value : null);
    }

    public Task<bool> SetAsync(TicketScope scope, InsightFlowExportCheckpoint next, InsightFlowExportCheckpoint? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(next);
        lock (gate)
        {
            checkpoints.TryGetValue(scope, out var current);
            if (!Equals(current, expected))
                return Task.FromResult(false);
            checkpoints[scope] = next;
            return Task.FromResult(true);
        }
    }
}
