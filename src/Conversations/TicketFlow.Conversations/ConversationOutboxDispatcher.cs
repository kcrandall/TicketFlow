using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

public sealed class ConversationOutboxDispatcherOptions
{
    /// <summary>Entries claimed per handler per run (1-100, default 50).</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Claim lease (default 1 minute, at most 1 hour). Handlers must finish within it or the entry is redelivered.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Attempts before an entry is dead-lettered for a consumer (1-100, default 10).</summary>
    public int MaximumAttempts { get; set; } = 10;

    /// <summary>First retry delay, doubled per attempt (default 5 seconds).</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retry delay ceiling (default 15 minutes).</summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A claim whose lease ends within this margin is not handled (it is left to expire and be redelivered), so a handler never starts
    /// work it cannot acknowledge. Default 5 seconds; must be shorter than <see cref="Lease"/>.
    /// </summary>
    public TimeSpan LeaseSafetyMargin { get; set; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (BatchSize is < 1 or > IConversationOutbox.MaximumClaim || Lease <= TimeSpan.Zero || Lease > IConversationOutbox.MaximumLease
            || MaximumAttempts is < 1 or > 100 || InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay
            || LeaseSafetyMargin < TimeSpan.Zero || LeaseSafetyMargin >= Lease)
            throw new InvalidOperationException("Conversation outbox dispatcher options are invalid.");
    }
}

/// <summary>
/// Counts from one dispatcher run. <see cref="Lost"/> counts acknowledgements refused because the lease or fence was lost;
/// <see cref="Skipped"/> counts claims left unhandled because their lease was about to expire.
/// </summary>
public sealed record ConversationOutboxDispatchResult(int Handled, int Retried, int DeadLettered, int Lost, int Skipped = 0);

/// <summary>
/// Runs registered <see cref="IConversationOutboxHandler"/>s over <see cref="IConversationOutbox"/>: claim a batch, handle each entry,
/// acknowledge on success, release for an exponential-backoff retry on failure and dead-letter after
/// <see cref="ConversationOutboxDispatcherOptions.MaximumAttempts"/>. A claim whose attempt count already exceeds the maximum (the entry
/// hung or crashed earlier handlers until its lease expired) is dead-lettered without being handled again, and a claim whose lease is
/// about to expire is skipped rather than handled. Handler exception details are not recorded (they may contain
/// customer data). No hosted service is included: hosts call <see cref="RunOnceAsync"/> in their own loop.
/// </summary>
public sealed class ConversationOutboxDispatcher
{
    private readonly IConversationOutbox outbox;
    private readonly IReadOnlyList<IConversationOutboxHandler> handlers;
    private readonly ConversationOutboxDispatcherOptions options;
    private readonly TimeProvider time;

    public ConversationOutboxDispatcher(IConversationOutbox outbox, IEnumerable<IConversationOutboxHandler> handlers, IOptions<ConversationOutboxDispatcherOptions> options, TimeProvider time)
    {
        this.outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = [.. handlers];
        foreach (var handler in this.handlers)
            IConversationOutbox.RequireConsumer(handler?.Consumer ?? throw new ArgumentException("Handlers cannot be null.", nameof(handlers)), nameof(handlers));
        if (this.handlers.Select(handler => handler.Consumer).Distinct(StringComparer.Ordinal).Count() != this.handlers.Count)
            throw new ArgumentException("Outbox handler consumer names must be unique.", nameof(handlers));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Registers every handler's consumer. Consumers receive entries committed after their registration.</summary>
    public async Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        foreach (var handler in handlers)
            await outbox.RegisterConsumerAsync(handler.Consumer, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationOutboxDispatchResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        int handled = 0, retried = 0, deadLettered = 0, lost = 0, skipped = 0;
        foreach (var handler in handlers)
        {
            var claims = await outbox.ClaimAsync(handler.Consumer, options.BatchSize, options.Lease, cancellationToken).ConfigureAwait(false);
            if (handler is IConversationOutboxBatchHandler batchHandler)
            {
                var eligible = new List<ConversationOutboxClaim>(claims.Count);
                HashSet<ConversationId>? blocked = null;
                foreach (var claim in claims)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (claim.Attempt > options.MaximumAttempts)
                    {
                        if (await outbox.DeadLetterAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, cancellationToken).ConfigureAwait(false))
                            deadLettered++;
                        else
                            lost++;
                        continue;
                    }
                    if (blocked is not null && blocked.Contains(claim.Entry.Change.Conversation))
                    {
                        skipped++;
                        continue;
                    }
                    if (claim.LeaseUntil <= time.GetUtcNow() + options.LeaseSafetyMargin)
                    {
                        (blocked ??= []).Add(claim.Entry.Change.Conversation);
                        skipped++;
                        continue;
                    }
                    eligible.Add(claim);
                }
                if (eligible.Count == 0)
                    continue;
                bool succeeded;
                TimeSpan? retryAfter = null;
                try
                {
                    await batchHandler.HandleBatchAsync([.. eligible.Select(claim => claim.Entry)], cancellationToken).ConfigureAwait(false);
                    succeeded = true;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    succeeded = false;
                    retryAfter = (error as ConversationOutboxRetryException)?.RetryAfter;
                }
                foreach (var claim in eligible)
                    if (succeeded)
                    {
                        if (await outbox.AcknowledgeAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, cancellationToken).ConfigureAwait(false))
                            handled++;
                        else
                            lost++;
                    }
                    else if (claim.Attempt >= options.MaximumAttempts)
                    {
                        if (await outbox.DeadLetterAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, cancellationToken).ConfigureAwait(false))
                            deadLettered++;
                        else
                            lost++;
                    }
                    else if (await outbox.ReleaseAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, time.GetUtcNow() + Max(Backoff(claim.Attempt), retryAfter), cancellationToken).ConfigureAwait(false))
                        retried++;
                    else
                        lost++;
                continue;
            }
            foreach (var claim in claims)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (claim.Attempt > options.MaximumAttempts)
                {
                    // Poison entry: earlier attempts never completed (hang or crash), so it is parked before running it again.
                    if (await outbox.DeadLetterAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, cancellationToken).ConfigureAwait(false))
                        deadLettered++;
                    else
                        lost++;
                    continue;
                }
                if (claim.LeaseUntil <= time.GetUtcNow() + options.LeaseSafetyMargin)
                {
                    skipped++;
                    continue;
                }
                bool succeeded;
                TimeSpan? retryAfter = null;
                try
                {
                    await handler.HandleAsync(claim.Entry, cancellationToken).ConfigureAwait(false);
                    succeeded = true;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    succeeded = false;
                    retryAfter = (error as ConversationOutboxRetryException)?.RetryAfter;
                }
                if (succeeded)
                {
                    if (await outbox.AcknowledgeAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, cancellationToken).ConfigureAwait(false))
                        handled++;
                    else
                        lost++;
                }
                else if (claim.Attempt >= options.MaximumAttempts)
                {
                    if (await outbox.DeadLetterAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, cancellationToken).ConfigureAwait(false))
                        deadLettered++;
                    else
                        lost++;
                }
                else if (await outbox.ReleaseAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, time.GetUtcNow() + Max(Backoff(claim.Attempt), retryAfter), cancellationToken).ConfigureAwait(false))
                    retried++;
                else
                    lost++;
            }
        }
        return new ConversationOutboxDispatchResult(handled, retried, deadLettered, lost, skipped);
    }

    private static TimeSpan Max(TimeSpan backoff, TimeSpan? requested) => requested is { } delay && delay > backoff ? delay : backoff;

    private TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, Math.Min(attempt - 1, 30));
        var delay = options.InitialRetryDelay.TotalMilliseconds * factor;
        return delay >= options.MaximumRetryDelay.TotalMilliseconds ? options.MaximumRetryDelay : TimeSpan.FromMilliseconds(delay);
    }
}
