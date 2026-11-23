using Microsoft.Extensions.Options;

namespace TicketFlow.Sla;

/// <summary>
/// Counts from one scheduler run. <see cref="Retried"/> counts failed evaluations rescheduled after <see cref="SlaOptions.TimerRetryDelay"/>
/// (including deferred flushes); <see cref="Lost"/> counts failed evaluations whose release was refused because the lease or fence was lost.
/// </summary>
public sealed record SlaTimerRunResult(int Evaluated, int Retried, int Lost);

/// <summary>
/// The due-work scheduler: claims due timers from <see cref="ISlaTrackerStore"/> (leased; concurrent schedulers never share a live
/// lease) and re-synchronizes each tracker through <see cref="SlaConversationTracker.SyncAsync(TicketFlow.Core.TicketScope, TicketFlow.Core.ConversationId, SlaTimerClaim?, CancellationToken)"/>, so warnings and breaches fire even
/// when the conversation sees no new events. The sync's final tracker write reschedules the timer and releases this claim's lease (writes
/// from the outbox path never release another worker's lease); a failed evaluation releases the timer for a retry after
/// <see cref="SlaOptions.TimerRetryDelay"/>, and a failed flush defers the timer by the same delay instead of spinning. Firing is idempotent (thresholds are recorded per
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
        var cache = claims.Count > 1 ? new SlaRunCache() : null;
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await tracker.SyncInternalAsync(claim.Scope, claim.Conversation, claim, cache, cancellationToken).ConfigureAwait(false);
                evaluated++;
                continue;
            }
            catch (SlaFlushDeferredException)
            {
                // The tracker write already rescheduled the timer (FlushRetryAt) and released this claim's lease.
                retried++;
                continue;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Exception details are not recorded: they may contain customer data.
            }
            if (await store.ReleaseAsync(claim, time.GetUtcNow() + options.TimerRetryDelay, cancellationToken).ConfigureAwait(false))
                retried++;
            else
                lost++;
        }
        return new SlaTimerRunResult(evaluated, retried, lost);
    }
}
