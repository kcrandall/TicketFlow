using Microsoft.Extensions.Options;

namespace TicketFlow.Reporting;

/// <summary>Counts from one retention run.</summary>
public sealed record ReportingRetentionResult(int FactsPruned, int RollupsPruned);

/// <summary>
/// Applies <see cref="ReportingOptions.FactRetention"/> and <see cref="ReportingOptions.RollupRetention"/>: each run deletes at most
/// <see cref="ReportingOptions.MaximumPruneBatches"/> batches of <see cref="ReportingOptions.PruneBatchSize"/> rows per table, store-wide.
/// No hosted service is included; hosts call <see cref="RunOnceAsync"/> in their loop (a short run with full batches means more is
/// due).
/// </summary>
public sealed class ReportingRetentionWorker
{
    private readonly IReportingStore store;
    private readonly ReportingOptions options;
    private readonly TimeProvider time;

    public ReportingRetentionWorker(IReportingStore store, IOptions<ReportingOptions> options, TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<ReportingRetentionResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var facts = options.FactRetention is { } factAge ? await PruneAsync(store.PruneFactsAsync, now - factAge, cancellationToken).ConfigureAwait(false) : 0;
        var rollups = options.RollupRetention is { } rollupAge ? await PruneAsync(store.PruneRollupsAsync, now - rollupAge, cancellationToken).ConfigureAwait(false) : 0;
        return new ReportingRetentionResult(facts, rollups);
    }

    private async Task<int> PruneAsync(Func<DateTimeOffset, int, CancellationToken, Task<int>> prune, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < options.MaximumPruneBatches; batch++)
        {
            var deleted = await prune(olderThan, options.PruneBatchSize, cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < options.PruneBatchSize)
                break;
        }
        return total;
    }
}
