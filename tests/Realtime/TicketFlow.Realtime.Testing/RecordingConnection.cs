using System.Collections.Concurrent;

namespace TicketFlow.Realtime.Testing;

/// <summary>An <see cref="IRealtimeConnection"/> that records every delivered event (thread-safe).</summary>
/// <remarks>Delivery is asynchronous: pass <paramref name="idle"/> (typically <c>RealtimeSubscriptionRegistry.IdleAsync</c>) so reads wait until the fan-out is idle.</remarks>
public sealed class RecordingConnection(string id, RealtimeSubscriber subscriber, Func<Task>? idle = null) : IRealtimeConnection, IRealtimeConnectionLifetime
{
    private readonly ConcurrentQueue<(RealtimeTopic Topic, RealtimeEvent Event)> received = new();

    public string Id { get; } = id;
    public RealtimeSubscriber Subscriber { get; } = subscriber;

    public IReadOnlyList<(RealtimeTopic Topic, RealtimeEvent Event)> Received
    {
        get
        {
            Idle();
            return [.. received];
        }
    }

    /// <summary>True once the registry closed this connection.</summary>
    public bool ClosedByServer { get; private set; }

    /// <summary>Delivered events of type <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> Events<T>() where T : class
    {
        Idle();
        return [.. received.Select(item => item.Event.Value).OfType<T>()];
    }

    public void Clear()
    {
        Idle();
        received.Clear();
    }

    public ValueTask CloseAsync()
    {
        ClosedByServer = true;
        return ValueTask.CompletedTask;
    }

    private void Idle() => idle?.Invoke().GetAwaiter().GetResult();

    public ValueTask SendAsync(RealtimeTopic topic, RealtimeEvent realtimeEvent, CancellationToken cancellationToken = default)
    {
        received.Enqueue((topic, realtimeEvent));
        return ValueTask.CompletedTask;
    }
}
