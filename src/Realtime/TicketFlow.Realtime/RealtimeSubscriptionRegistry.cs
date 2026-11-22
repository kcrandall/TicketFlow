using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;

namespace TicketFlow.Realtime;

/// <summary>
/// The node-local <see cref="IRealtimeSubscriptions"/>: bounded connections and subscriptions, authorization of every subscription
/// and per-subscriber filtering of every delivery (inside a DI scope per publication, so scoped Access services and current data are
/// used). It receives publications from the <see cref="IRealtimeBus"/>. Connection sinks that throw are skipped; payloads are never
/// logged.
/// </summary>
public sealed class RealtimeSubscriptionRegistry : IRealtimeSubscriptions, IDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionEntry> connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<object, ConcurrentDictionary<string, SubscriptionEntry>> topics = new();
    private readonly IServiceScopeFactory scopes;
    private readonly RealtimeOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly IDisposable busSubscription;
    private readonly Lock gate = new();

    public RealtimeSubscriptionRegistry(IRealtimeBus bus, IServiceScopeFactory scopes, IOptions<RealtimeOptions> options, TimeProvider time, ILogger<RealtimeSubscriptionRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.logger = logger ?? NullLogger<RealtimeSubscriptionRegistry>.Instance;
        busSubscription = bus.Subscribe(DeliverAsync);
    }

    /// <summary>Connections currently registered on this node.</summary>
    public int ConnectionCount => connections.Count;

    public RealtimeConnectResult Connect(IRealtimeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrEmpty(connection.Id) || connection.Id.Length > 256 || connection.Subscriber.Value is null)
            return new RealtimeSubscriptionRefused(RealtimeRefusal.Invalid);
        if (connections.Count >= options.MaximumConnections)
            return new RealtimeSubscriptionRefused(RealtimeRefusal.LimitReached);
        return connections.TryAdd(connection.Id, new ConnectionEntry(connection))
            ? new RealtimeConnected(connection.Id)
            : new RealtimeSubscriptionRefused(RealtimeRefusal.Invalid);
    }

    public async Task<RealtimeSubscribeResult> SubscribeAsync(string connectionId, RealtimeTopic topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (topic.Value is null)
            return new RealtimeSubscriptionRefused(RealtimeRefusal.Invalid);
        if (!connections.TryGetValue(connectionId, out var connection))
            return new RealtimeSubscriptionRefused(RealtimeRefusal.NotConnected);
        lock (gate)
        {
            if (connection.Topics.ContainsKey(topic.Value))
                return new RealtimeSubscribed(topic);
            if (connection.Topics.Count >= options.MaximumSubscriptionsPerConnection)
                return new RealtimeSubscriptionRefused(RealtimeRefusal.LimitReached);
        }

        SubscriptionGrant? grant;
        RealtimeRefusal refusal;
        await using (var scope = scopes.CreateAsyncScope())
            (grant, refusal) = await Policy(scope.ServiceProvider).AuthorizeAsync(connection.Connection.Subscriber, topic, cancellationToken).ConfigureAwait(false);
        if (grant is null)
            return new RealtimeSubscriptionRefused(refusal);

        lock (gate)
        {
            if (connection.Closed)
                return new RealtimeSubscriptionRefused(RealtimeRefusal.NotConnected);
            if (connection.Topics.ContainsKey(topic.Value))
                return new RealtimeSubscribed(topic);
            if (connection.Topics.Count >= options.MaximumSubscriptionsPerConnection)
                return new RealtimeSubscriptionRefused(RealtimeRefusal.LimitReached);
            var entry = new SubscriptionEntry(connection, topic, grant);
            connection.Topics[topic.Value] = entry;
            topics.GetOrAdd(topic.Value, _ => new ConcurrentDictionary<string, SubscriptionEntry>(StringComparer.Ordinal))[connectionId] = entry;
        }
        return new RealtimeSubscribed(topic);
    }

    public bool Unsubscribe(string connectionId, RealtimeTopic topic)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (topic.Value is null || !connections.TryGetValue(connectionId, out var connection))
            return false;
        lock (gate)
        {
            if (!connection.Topics.Remove(topic.Value))
                return false;
            RemoveFromTopic(topic.Value, connectionId);
        }
        return true;
    }

    public void Disconnect(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (!connections.TryRemove(connectionId, out var connection))
            return;
        lock (gate)
        {
            connection.Closed = true;
            foreach (var key in connection.Topics.Keys)
                RemoveFromTopic(key, connectionId);
            connection.Topics.Clear();
        }
    }

    public IReadOnlyList<RealtimeTopic> Topics(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (!connections.TryGetValue(connectionId, out var connection))
            return [];
        lock (gate)
            return [.. connection.Topics.Values.Select(entry => entry.Topic)];
    }

    /// <summary>Delivers one publication to this node's subscribers of its topic, filtered per subscriber.</summary>
    public async Task DeliverAsync(RealtimePublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!topics.TryGetValue(publication.Topic.Value!, out var subscribers) || subscribers.IsEmpty)
            return;
        var targets = subscribers.Values.ToList();
        await using var scope = scopes.CreateAsyncScope();
        var policy = Policy(scope.ServiceProvider);
        foreach (var target in targets)
        {
            if (target.Connection.Closed)
                continue;
            RealtimeEvent? filtered;
            try
            {
                filtered = await policy.FilterAsync(target.Connection.Connection.Subscriber, target.Topic, target.Grant, publication.Event, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // Fail closed: a subscriber whose delivery cannot be authorized receives nothing.
                logger.LogWarning("Realtime delivery authorization failed ({Error}); the event was not delivered.", error.GetType().Name);
                continue;
            }
            if (filtered is not { } delivered)
                continue;
            try
            {
                await target.Connection.Connection.SendAsync(target.Topic, delivered, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("A realtime connection send failed ({Error}).", error.GetType().Name);
            }
        }
    }

    public void Dispose() => busSubscription.Dispose();

    private RealtimeDeliveryPolicy Policy(IServiceProvider services) => new(
        services.GetRequiredService<IConversationStore>(),
        services.GetRequiredService<IAccessEvaluator>(),
        services.GetRequiredService<IAccessQueryPlanner>(),
        time,
        options);

    private void RemoveFromTopic(object key, string connectionId)
    {
        if (!topics.TryGetValue(key, out var subscribers))
            return;
        subscribers.TryRemove(connectionId, out _);
        if (subscribers.IsEmpty)
            topics.TryRemove(key, out _);
    }

    private sealed class ConnectionEntry(IRealtimeConnection connection)
    {
        public IRealtimeConnection Connection { get; } = connection;
        public Dictionary<object, SubscriptionEntry> Topics { get; } = [];
        public volatile bool Closed;

        // Topics is mutated only under the registry gate.
    }

    private sealed class SubscriptionEntry(ConnectionEntry connection, RealtimeTopic topic, SubscriptionGrant grant)
    {
        public ConnectionEntry Connection { get; } = connection;
        public RealtimeTopic Topic { get; } = topic;
        public SubscriptionGrant Grant { get; } = grant;
    }
}
