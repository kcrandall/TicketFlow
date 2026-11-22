using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketFlow.Realtime;

namespace TicketFlow.Integrations.WalFlow;

/// <summary>
/// Cross-node broadcast transport for serialized <see cref="RealtimePublicationEnvelope"/> JSON messages (for example PostgreSQL
/// <c>LISTEN</c>/<c>NOTIFY</c>, Redis pub/sub or a Kafka/NATS consumer group).
/// </summary>
public interface IRealtimeBackplane
{
    Task PublishAsync(string envelopeJson, CancellationToken cancellationToken = default);

    /// <summary>Registers a handler invoked for envelopes arriving from the backplane; dispose to detach.</summary>
    IDisposable Subscribe(Func<string, CancellationToken, Task> handler);
}

/// <summary>In-memory multi-node backplane for deterministic tests and local composition.</summary>
public sealed class InMemoryRealtimeBackplane : IRealtimeBackplane
{
    private ImmutableArray<Func<string, CancellationToken, Task>> handlers = [];

    public async Task PublishAsync(string envelopeJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        foreach (var handler in handlers)
        {
            try
            {
                await handler(envelopeJson, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    public IDisposable Subscribe(Func<string, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ImmutableInterlocked.Update(ref handlers, list => list.Add(handler));
        return new Subscription(this, handler);
    }

    private sealed class Subscription(InMemoryRealtimeBackplane backplane, Func<string, CancellationToken, Task> handler) : IDisposable
    {
        public void Dispose() => ImmutableInterlocked.Update(ref backplane.handlers, list => list.Remove(handler));
    }
}

/// <summary>
/// Multi-node <see cref="IRealtimeBus"/>: delivers publications to local subscribers immediately and broadcasts a versioned
/// <see cref="RealtimePublicationEnvelope"/> over <see cref="IRealtimeBackplane"/> to reach every other node. Incoming envelopes
/// stamped with this node's own <see cref="NodeId"/> are ignored so local subscribers never see duplicates.
/// </summary>
public sealed class CrossNodeRealtimeBus : IRealtimeBus, IDisposable
{
    private readonly IRealtimeBackplane backplane;
    private readonly WalFlowRealtimeOptions options;
    private readonly ILogger<CrossNodeRealtimeBus>? logger;
    private readonly IDisposable backplaneSubscription;
    private ImmutableArray<Func<RealtimePublication, CancellationToken, Task>> handlers = [];

    public CrossNodeRealtimeBus(IRealtimeBackplane backplane, IOptions<WalFlowRealtimeOptions> options, string? nodeId = null, ILogger<CrossNodeRealtimeBus>? logger = null)
    {
        this.backplane = backplane ?? throw new ArgumentNullException(nameof(backplane));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        NodeId = string.IsNullOrWhiteSpace(nodeId) ? Guid.NewGuid().ToString("N") : nodeId;
        this.logger = logger;
        backplaneSubscription = this.backplane.Subscribe(OnBackplaneMessageAsync);
    }

    /// <summary>Unique identifier of this node, stamped as the envelope origin to prevent self-echo.</summary>
    public string NodeId { get; }

    public async Task PublishAsync(RealtimePublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        await DeliverLocallyAsync(publication, cancellationToken).ConfigureAwait(false);
        string json;
        try
        {
            json = RealtimePublicationEnvelope.Serialize(publication, NodeId, options.MaximumEnvelopeBytes);
        }
        catch (FormatException)
        {
            // Never log payloads (VisitorMessageEvent may hold customer text); skip oversized cross-node broadcast safely.
            logger?.LogWarning("Skipped cross-node realtime broadcast because the serialized envelope exceeded {Limit} bytes.", options.MaximumEnvelopeBytes);
            return;
        }
        try
        {
            await backplane.PublishAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Cross-node realtime backplane publish failed with {Error}.", error.GetType().Name);
        }
    }

    public IDisposable Subscribe(Func<RealtimePublication, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ImmutableInterlocked.Update(ref handlers, list => list.Add(handler));
        return new Subscription(this, handler);
    }

    public void Dispose() => backplaneSubscription.Dispose();

    private async Task OnBackplaneMessageAsync(string envelopeJson, CancellationToken cancellationToken)
    {
        if (!RealtimePublicationEnvelope.TryDeserialize(envelopeJson, out var envelope, out _, options.MaximumEnvelopeBytes) || envelope is null)
            return;
        if (string.Equals(envelope.Origin, NodeId, StringComparison.Ordinal))
            return;
        await DeliverLocallyAsync(envelope.Publication, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverLocallyAsync(RealtimePublication publication, CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handler(publication, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Best-effort fan-out: delivery failures never fail the publisher.
            }
        }
    }

    private sealed class Subscription(CrossNodeRealtimeBus bus, Func<RealtimePublication, CancellationToken, Task> handler) : IDisposable
    {
        public void Dispose() => ImmutableInterlocked.Update(ref bus.handlers, list => list.Remove(handler));
    }
}
