using System.Collections.Concurrent;

namespace TicketFlow.Realtime.Testing;

/// <summary>An <see cref="IRealtimeConnection"/> that records every delivered event (thread-safe).</summary>
public sealed class RecordingConnection(string id, RealtimeSubscriber subscriber) : IRealtimeConnection
{
    private readonly ConcurrentQueue<(RealtimeTopic Topic, RealtimeEvent Event)> received = new();

    public string Id { get; } = id;
    public RealtimeSubscriber Subscriber { get; } = subscriber;

    public IReadOnlyList<(RealtimeTopic Topic, RealtimeEvent Event)> Received => [.. received];

    /// <summary>Delivered events of type <typeparamref name="T"/>.</summary>
    public IReadOnlyList<T> Events<T>() where T : class => [.. received.Select(item => item.Event.Value).OfType<T>()];

    public void Clear() => received.Clear();

    public ValueTask SendAsync(RealtimeTopic topic, RealtimeEvent realtimeEvent, CancellationToken cancellationToken = default)
    {
        received.Enqueue((topic, realtimeEvent));
        return ValueTask.CompletedTask;
    }
}
