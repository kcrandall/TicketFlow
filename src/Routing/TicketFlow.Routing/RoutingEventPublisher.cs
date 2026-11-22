namespace TicketFlow.Routing;

/// <summary>Best-effort fan-out to <see cref="IRoutingEventObserver"/>s: failures are swallowed (realtime must never fail routing).</summary>
internal sealed class RoutingEventPublisher(IEnumerable<IRoutingEventObserver> observers)
{
    private readonly IReadOnlyList<IRoutingEventObserver> observers = [.. observers];

    public async Task PublishAsync(RoutingEvent routingEvent, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.OnRoutingEventAsync(routingEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Observer details may contain customer data; nothing is recorded.
            }
        }
    }
}
