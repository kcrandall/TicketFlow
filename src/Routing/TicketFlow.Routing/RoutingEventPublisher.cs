namespace TicketFlow.Routing;

/// <summary>Best-effort fan-out to <see cref="IRoutingEventObserver"/>s: failures are swallowed (realtime must never fail routing).</summary>
internal sealed class RoutingEventPublisher(IEnumerable<IRoutingEventObserver> observers)
{
    private readonly IRoutingEventObserver[] observers = [.. observers];

    public Task PublishAsync(RoutingEvent routingEvent, CancellationToken cancellationToken) =>
        observers.Length == 0 ? Task.CompletedTask : PublishCoreAsync(routingEvent, cancellationToken);

    private async Task PublishCoreAsync(RoutingEvent routingEvent, CancellationToken cancellationToken)
    {
        for (var i = 0; i < observers.Length; i++)
        {
            try
            {
                await observers[i].OnRoutingEventAsync(routingEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Observer details may contain customer data; nothing is recorded.
            }
        }
    }
}
