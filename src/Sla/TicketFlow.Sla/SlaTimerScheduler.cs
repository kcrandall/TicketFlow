using Microsoft.Extensions.Options;

namespace TicketFlow.Sla;

/// <summary>
/// Counts from one scheduler run. <see cref="Lost"/> counts failed evaluations whose release was refused: the lease or fence was lost, or
/// a tracker write during the failed evaluation already rescheduled the timer (and cleared the lease).
/// </summary>
public sealed record SlaTimerRunResult(int Evaluated, int Retried, int Lost);

/// <summary>
/// The due-work scheduler: claims due timers from <see cref="ISlaTrackerStore"/> (leased; concurrent schedulers never share a live
/// lease) and re-synchronizes each tracker through <see cref="SlaConversationTracker.SyncAsync"/>, so warnings and breaches fire even
/// when the conversation sees no new events. The sync's tracker write reschedules the timer and clears the lease; a failed evaluation
/// releases the timer for a retry after <see cref="SlaOptions.TimerRetryDelay"/>. Firing is idempotent (thresholds are recorded per
/// clock and emissions are de-duplicated). No hosted service is included: hosts call <see cref="RunOnceAsync"/> in their own loop.
/// </summary>
public sealed class SlaTimerScheduler
{
    private readonly ISlaTrackerStore store;
    private readonly SlaConversationTracker tracker;
    private readonly SlaOptions options;
    private readonly TimeProvider time;

    public SlaTimerScheduler(ISlaTrackerStore store, SlaConversationTracker tracker, IOptions<SlaOptions> options, TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<SlaTimerRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        int evaluated = 0, retried = 0, lost = 0;
        var claims = await store.ClaimDueAsync(options.TimerBatchSize, options.TimerLease, cancellationToken).ConfigureAwait(false);
        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool succeeded;
            try
            {
                await tracker.SyncAsync(claim.Scope, claim.Conversation, cancellationToken).ConfigureAwait(false);
                succeeded = true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Exception details are not recorded: they may contain customer data.
                succeeded = false;
            }
            if (succeeded)
                evaluated++;
            else if (await store.ReleaseAsync(claim, time.GetUtcNow() + options.TimerRetryDelay, cancellationToken).ConfigureAwait(false))
                retried++;
            else
                lost++;
        }
        return new SlaTimerRunResult(evaluated, retried, lost);
    }
}
