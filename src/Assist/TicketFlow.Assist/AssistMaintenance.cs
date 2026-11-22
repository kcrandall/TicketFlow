using Microsoft.Extensions.Options;

namespace TicketFlow.Assist;

/// <summary>
/// Periodic assistant maintenance for a host worker (for example the E11 worker): <see cref="RunOnceAsync"/> settles reservations left
/// unsettled longer than <see cref="AssistOptions.ReservationTimeout"/> (a crashed process) as Failed at their estimate, so they stay
/// charged but no longer look in-flight. Idempotent and safe to run concurrently on several nodes.
/// </summary>
public sealed class AssistMaintenance
{
    public const int BatchSize = 500;
    public const int MaximumBatchesPerRun = 20;

    private readonly IAssistStore store;
    private readonly AssistOptions options;
    private readonly TimeProvider time;

    public AssistMaintenance(IAssistStore store, IOptions<AssistOptions> options, TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Sweeps abandoned reservations in bounded batches; returns how many were settled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var total = 0;
        for (var batch = 0; batch < MaximumBatchesPerRun; batch++)
        {
            var swept = await store.SweepReservationsAsync(now - options.ReservationTimeout, now, BatchSize, cancellationToken).ConfigureAwait(false);
            total += swept;
            if (swept < BatchSize)
                break;
        }
        return total;
    }
}
