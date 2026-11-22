using Microsoft.AspNetCore.SignalR;

namespace TicketFlow.Realtime.SignalR;

/// <summary>
/// An <see cref="IRealtimeConnection"/> over one SignalR connection: events (already authorized and filtered by the registry) are sent
/// to that connection only, as <see cref="RealtimeWireFormat.EventMethod"/> with a <see cref="RealtimeWireEvent"/>.
/// </summary>
public sealed class SignalRRealtimeConnection(string id, RealtimeSubscriber subscriber, IClientProxy client) : IRealtimeConnection
{
    private readonly IClientProxy client = client ?? throw new ArgumentNullException(nameof(client));

    public string Id { get; } = id ?? throw new ArgumentNullException(nameof(id));
    public RealtimeSubscriber Subscriber { get; } = subscriber;

    public async ValueTask SendAsync(RealtimeTopic topic, RealtimeEvent realtimeEvent, CancellationToken cancellationToken = default) =>
        await client.SendAsync(RealtimeWireFormat.EventMethod, RealtimeWireFormat.ToWire(topic, realtimeEvent), cancellationToken).ConfigureAwait(false);
}
