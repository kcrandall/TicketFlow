using System.Collections.Immutable;

namespace TicketFlow.Realtime;

/// <summary>
/// The default <see cref="IRealtimeBus"/>: delivers publications to handlers in this process only (awaited in registration order;
/// handler failures are swallowed so one node component cannot fail a publisher). Multi-node deployments replace it.
/// </summary>
public sealed class InProcessRealtimeBus : IRealtimeBus
{
    private ImmutableArray<Func<RealtimePublication, CancellationToken, Task>> handlers = [];

    public Task PublishAsync(RealtimePublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var current = handlers;
        return current.IsEmpty ? Task.CompletedTask : PublishCoreAsync(current, publication, cancellationToken);
    }

    private static async Task PublishCoreAsync(ImmutableArray<Func<RealtimePublication, CancellationToken, Task>> current, RealtimePublication publication, CancellationToken cancellationToken)
    {
        for (var i = 0; i < current.Length; i++)
        {
            try
            {
                await current[i](publication, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Best-effort fan-out: delivery failures never fail the publisher.
            }
        }
    }

    public IDisposable Subscribe(Func<RealtimePublication, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ImmutableInterlocked.Update(ref handlers, list => list.Add(handler));
        return new Subscription(this, handler);
    }

    private sealed class Subscription(InProcessRealtimeBus bus, Func<RealtimePublication, CancellationToken, Task> handler) : IDisposable
    {
        public void Dispose() => ImmutableInterlocked.Update(ref bus.handlers, list => list.Remove(handler));
    }
}

/// <summary>The default <see cref="IRealtimePublisher"/>: forwards to the registered <see cref="IRealtimeBus"/>.</summary>
public sealed class BusRealtimePublisher(IRealtimeBus bus) : IRealtimePublisher
{
    private readonly IRealtimeBus bus = bus ?? throw new ArgumentNullException(nameof(bus));

    public Task PublishAsync(RealtimePublication publication, CancellationToken cancellationToken = default) => bus.PublishAsync(publication, cancellationToken);
}
