using Microsoft.Extensions.Options;

namespace TicketFlow.Conversations;

/// <summary>
/// A durable outbox consumer registered by a later feature (realtime, routing, SLA, automation, reporting, channel dispatch).
/// <see cref="HandleAsync"/> must be idempotent: an entry is redelivered when the lease expires before acknowledgement.
/// </summary>
public interface IConversationOutboxHandler
{
    /// <summary>The consumer name (1-64 characters of a-z, 0-9, '-', '_' or '.', starting with a letter), unique per handler.</summary>
    string Consumer { get; }

    Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default);
}

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

    internal void Validate()
    {
        if (BatchSize is < 1 or > IConversationOutbox.MaximumClaim || Lease <= TimeSpan.Zero || Lease > IConversationOutbox.MaximumLease
            || MaximumAttempts is < 1 or > 100 || InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay)
            throw new InvalidOperationException("Conversation outbox dispatcher options are invalid.");
    }
}

/// <summary>Counts from one dispatcher run. <see cref="Lost"/> counts acknowledgements refused because the lease or fence was lost.</summary>
public sealed record ConversationOutboxDispatchResult(int Handled, int Retried, int DeadLettered, int Lost);

/// <summary>
/// Runs registered <see cref="IConversationOutboxHandler"/>s over <see cref="IConversationOutbox"/>: claim a batch, handle each entry,
/// acknowledge on success, release for an exponential-backoff retry on failure and dead-letter after
/// <see cref="ConversationOutboxDispatcherOptions.MaximumAttempts"/>. Handler exception details are not recorded (they may contain
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
        int handled = 0, retried = 0, deadLettered = 0, lost = 0;
        foreach (var handler in handlers)
        {
            var claims = await outbox.ClaimAsync(handler.Consumer, options.BatchSize, options.Lease, cancellationToken).ConfigureAwait(false);
            foreach (var claim in claims)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool succeeded;
                try
                {
                    await handler.HandleAsync(claim.Entry, cancellationToken).ConfigureAwait(false);
                    succeeded = true;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    succeeded = false;
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
                else if (await outbox.ReleaseAsync(handler.Consumer, claim.Entry.Sequence, claim.Fence, time.GetUtcNow() + Backoff(claim.Attempt), cancellationToken).ConfigureAwait(false))
                    retried++;
                else
                    lost++;
            }
        }
        return new ConversationOutboxDispatchResult(handled, retried, deadLettered, lost);
    }

    private TimeSpan Backoff(int attempt)
    {
        var factor = Math.Pow(2, Math.Min(attempt - 1, 30));
        var delay = options.InitialRetryDelay.TotalMilliseconds * factor;
        return delay >= options.MaximumRetryDelay.TotalMilliseconds ? options.MaximumRetryDelay : TimeSpan.FromMilliseconds(delay);
    }
}
