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

    internal void Validate()
    {
        if (MaximumConnections is < 1 or > 1_000_000 || MaximumSubscriptionsPerConnection is < 1 or > 4096
            || QueueFilterLifetime < TimeSpan.FromSeconds(1) || QueueFilterLifetime > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Realtime options are invalid.");
    }
}
