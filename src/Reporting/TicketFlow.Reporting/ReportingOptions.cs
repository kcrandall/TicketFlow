using TicketFlow.Core;

namespace TicketFlow.Reporting;

public sealed class ReportingOptions
{
    /// <summary>The outbox consumer name of <see cref="ReportingProjector"/>.</summary>
    public const string Consumer = "reporting";

    /// <summary>
    /// The SLA worker API client (<c>SlaOptions.WorkerClientId</c>). Only <c>sla.*</c> timeline events appended by this actor are counted;
    /// events by any other actor are ignored. Default <c>sla-worker</c>.
    /// </summary>
    public ApiClientId SlaWorkerClientId { get; set; } = new("sla-worker");

    /// <summary>Attempts when concurrent projector writers win (1-64, default 16).</summary>
    public int MaximumWriteAttempts { get; set; } = 16;

    /// <summary>
    /// Conversations first seen with a longer timeline are adopted from their snapshot (counted as created, current dimensions, no
    /// historical facts) instead of replayed (1-100000, default 10000).
    /// </summary>
    public int MaximumReplayEvents { get; set; } = 10_000;

    /// <summary>Facts older than this are pruned by <see cref="ReportingRetentionWorker"/> (at least one day; <see langword="null"/> keeps them, the default).</summary>
    public TimeSpan? FactRetention { get; set; }

    /// <summary>Rollup cells older than this are pruned by <see cref="ReportingRetentionWorker"/> (at least one day; <see langword="null"/> keeps them, the default).</summary>
    public TimeSpan? RollupRetention { get; set; }

    /// <summary>Rows deleted per prune statement (1-10000, default 1000).</summary>
    public int PruneBatchSize { get; set; } = 1000;

    /// <summary>Prune statements per table per retention run (1-1000, default 10).</summary>
    public int MaximumPruneBatches { get; set; } = 10;

    /// <summary>Time zone for report buckets when the workspace has no settings or its zone does not resolve on this host (default UTC).</summary>
    public string DefaultTimeZoneId { get; set; } = "UTC";

    internal void Validate()
    {
        if (SlaWorkerClientId == default || MaximumWriteAttempts is < 1 or > 64 || MaximumReplayEvents is < 1 or > 100_000
            || FactRetention is { } facts && facts < TimeSpan.FromDays(1) || RollupRetention is { } rollups && rollups < TimeSpan.FromDays(1)
            || PruneBatchSize is < 1 or > ReportStoreLimits.MaximumPrune || MaximumPruneBatches is < 1 or > 1000 || string.IsNullOrWhiteSpace(DefaultTimeZoneId))
            throw new InvalidOperationException("Reporting options are invalid.");
    }
}
