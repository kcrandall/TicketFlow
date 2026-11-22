namespace TicketFlow.Realtime;

public sealed class RealtimeOptions
{
    /// <summary>The outbox consumer name of <see cref="RealtimeOutboxHandler"/>.</summary>
    public const string OutboxConsumer = "realtime";

    /// <summary>Connections per node (1-1 000 000, default 100 000).</summary>
    public int MaximumConnections { get; set; } = 100_000;

    /// <summary>Subscriptions per connection (1-4096, default 256).</summary>
    public int MaximumSubscriptionsPerConnection { get; set; } = 256;

    /// <summary>How long a queue subscription's planned <c>conversation:read</c> filter is reused before it is planned again (1 second to 10 minutes, default 30 seconds).</summary>
    public TimeSpan QueueFilterLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a principal's loaded Access decision context (<c>conversation:read</c>, <c>note:read</c>) is reused for deliveries before
    /// it is loaded again (1 second to 10 minutes, default 30 seconds). This bounds how long a revoked grant can still receive
    /// identifier-only notifications on an existing subscription.
    /// </summary>
    public TimeSpan DecisionContextLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Publications buffered for the node's fan-out worker (100-1 000 000, default 10 000); the oldest are dropped when full.</summary>
    public int MaximumPendingPublications { get; set; } = 10_000;

    /// <summary>Events buffered per connection (1-65 536, default 256); the oldest are dropped when a client is slow.</summary>
    public int ConnectionQueueCapacity { get; set; } = 256;

    /// <summary>Longest time one send to a connection may take before it is abandoned (100 ms to 5 minutes, default 10 seconds).</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (MaximumConnections is < 1 or > 1_000_000 || MaximumSubscriptionsPerConnection is < 1 or > 4096
            || QueueFilterLifetime < TimeSpan.FromSeconds(1) || QueueFilterLifetime > TimeSpan.FromMinutes(10)
            || DecisionContextLifetime < TimeSpan.FromSeconds(1) || DecisionContextLifetime > TimeSpan.FromMinutes(10)
            || MaximumPendingPublications is < 100 or > 1_000_000 || ConnectionQueueCapacity is < 1 or > 65_536
            || SendTimeout < TimeSpan.FromMilliseconds(100) || SendTimeout > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Realtime options are invalid.");
    }
}
