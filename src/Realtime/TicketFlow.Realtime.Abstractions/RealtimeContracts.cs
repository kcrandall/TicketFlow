using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>
/// Publishes realtime events. Delivery is best-effort and at-most-once per connection: realtime is a hint layer and clients recover
/// through refetch (conversation timelines, visitor message cursors). Publishing never throws for absent subscribers.
/// </summary>
public interface IRealtimePublisher
{
    Task PublishAsync(RealtimePublication publication, CancellationToken cancellationToken = default);
}

/// <summary>
/// Moves publications to every node that holds connections. The default in-process bus delivers to this process only; a multi-node
/// deployment supplies a bus over a shared transport (Redis pub/sub, a message broker, change capture) that serializes publications
/// with a versioned envelope. Each node authorizes and filters deliveries for its own connections.
/// </summary>
public interface IRealtimeBus
{
    Task PublishAsync(RealtimePublication publication, CancellationToken cancellationToken = default);

    /// <summary>Registers a node-local handler; dispose to stop receiving.</summary>
    IDisposable Subscribe(Func<RealtimePublication, CancellationToken, Task> handler);
}

/// <summary>
/// A transport connection (a SignalR connection, a WebSocket, a test sink). <see cref="SendAsync"/> receives events already
/// authorized and filtered for <see cref="Subscriber"/>; it should not block (queue or fire-and-forget) and must never log payloads.
/// </summary>
public interface IRealtimeConnection
{
    /// <summary>Unique per node (1-256 characters).</summary>
    string Id { get; }

    RealtimeSubscriber Subscriber { get; }

    ValueTask SendAsync(RealtimeTopic topic, RealtimeEvent realtimeEvent, CancellationToken cancellationToken = default);
}

public enum RealtimeRefusal
{
    /// <summary>The connection is not registered (or was disconnected).</summary>
    NotConnected = 1,

    /// <summary>Access (or the visitor session rule) denied the subscription or action.</summary>
    Forbidden = 2,

    /// <summary>The conversation does not exist in the subscriber's scope.</summary>
    NotFound = 3,

    /// <summary>The request is invalid (for example a topic in another scope).</summary>
    Invalid = 4,

    /// <summary>A connection or subscription limit was reached.</summary>
    LimitReached = 5,
}

public sealed record RealtimeSubscribed(RealtimeTopic Topic);

public sealed record RealtimeSubscriptionRefused(RealtimeRefusal Reason);

/// <summary>Outcome of a subscription request. Switch over every case without a discard arm.</summary>
public union RealtimeSubscribeResult(RealtimeSubscribed, RealtimeSubscriptionRefused);

/// <summary>Outcome of connecting a transport connection. Switch over every case without a discard arm.</summary>
public union RealtimeConnectResult(RealtimeConnected, RealtimeSubscriptionRefused);

public sealed record RealtimeConnected(string ConnectionId);

/// <summary>
/// The node-local subscription registry. Subscriptions are authorized when made and every delivery is re-authorized and filtered per
/// subscriber against current data, so revoked grants, reassignments and masking apply to live connections.
/// </summary>
public interface IRealtimeSubscriptions
{
    RealtimeConnectResult Connect(IRealtimeConnection connection);

    Task<RealtimeSubscribeResult> SubscribeAsync(string connectionId, RealtimeTopic topic, CancellationToken cancellationToken = default);

    bool Unsubscribe(string connectionId, RealtimeTopic topic);

    void Disconnect(string connectionId);

    /// <summary>The connection's current topics (empty when not connected).</summary>
    IReadOnlyList<RealtimeTopic> Topics(string connectionId);
}

public sealed record RealtimeActionApplied
{
    private RealtimeActionApplied()
    {
    }

    public static RealtimeActionApplied Instance { get; } = new();
}

/// <summary>Outcome of an agent realtime action. Switch over every case without a discard arm.</summary>
public union RealtimeActionResult(RealtimeActionApplied, RealtimeSubscriptionRefused);

/// <summary>
/// Ephemeral agent actions: typing (<c>conversation:reply</c>) and read receipts (<c>conversation:read</c>). Published to the
/// conversation topic and, through <see cref="IVisitorSessionDirectory"/>, to linked visitor sessions without the agent's identity.
/// </summary>
public interface IRealtimeAgentActions
{
    Task<RealtimeActionResult> SetTypingAsync(PrincipalRef actor, ConversationId conversation, bool typing, CancellationToken cancellationToken = default);

    Task<RealtimeActionResult> MarkReadAsync(PrincipalRef actor, ConversationId conversation, long upToSequence, CancellationToken cancellationToken = default);
}

/// <summary>Finds the live-chat visitor sessions linked to a conversation (implemented by the live-chat package; trusted lookup).</summary>
public interface IVisitorSessionDirectory
{
    public const int MaximumSessions = 32;

    Task<IReadOnlyList<VisitorSessionId>> ListSessionsAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optionally implemented by an <see cref="IRealtimeConnection"/> whose transport can be closed by the server (for example when the
/// visitor session it belongs to ends).
/// </summary>
public interface IRealtimeConnectionLifetime
{
    ValueTask CloseAsync();
}
