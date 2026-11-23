using System.Text.Json;
using Microsoft.Extensions.Options;
using TicketFlow.Events;
using WalFlow.Abstractions.Models;
using WalFlow.Abstractions.Sinks;
using WalFlow.Abstractions.Sinks.Transport;

namespace TicketFlow.Integrations.WalFlow.Sinks;

/// <summary>A named WalFlow sink and the scopes allowed to target it (at least one rule).</summary>
public sealed record WalFlowEventSinkRegistration
{
    public WalFlowEventSinkRegistration(SinkRegistration sink, IEnumerable<EventScopeRule> allowedScopes)
    {
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
        ArgumentNullException.ThrowIfNull(allowedScopes);
        AllowedScopes = [.. allowedScopes];
        if (AllowedScopes.Count == 0 || AllowedScopes.Any(rule => rule is null))
            throw new ArgumentException("A WalFlow event sink needs at least one allowed scope rule.", nameof(allowedScopes));
    }

    public SinkRegistration Sink { get; }
    public IReadOnlyList<EventScopeRule> AllowedScopes { get; }
}

public sealed class WalFlowEventSinkOptions
{
    /// <summary>
    /// Named WalFlow sinks (for example a <c>KafkaSink</c> registered as <c>kafka</c>) with their allowed scopes; a subscription's sink target
    /// names one, and only subscriptions of an allowed scope may use it.
    /// </summary>
    public IList<WalFlowEventSinkRegistration> Sinks { get; } = [];

    /// <summary>Adds <paramref name="sink"/> for the scopes matching <paramref name="allowedScopes"/>.</summary>
    public WalFlowEventSinkOptions Add(SinkRegistration sink, params EventScopeRule[] allowedScopes)
    {
        Sinks.Add(new WalFlowEventSinkRegistration(sink, allowedScopes));
        return this;
    }

    /// <summary>
    /// Refuse targets whose sink does not declare a provider durability boundary (<see cref="ISinkDurability"/> other than
    /// <see cref="SinkDurabilityGuarantee.RequestResponsePerSend"/> or <see cref="SinkDurabilityGuarantee.FlushAcknowledged"/>). Default
    /// <see langword="false"/>: delivery is then only as durable as the sink.
    /// </summary>
    public bool RequireDurableSinks { get; set; }

    /// <summary>The Debezium <c>source.name</c> of produced records (default <c>ticketflow</c>).</summary>
    public string SourceName { get; set; } = "ticketflow";
}

/// <summary>
/// The <c>walflow</c> sink: adapts integration events to WalFlow's <see cref="ISink"/> contract so any WalFlow sink package can carry them.
/// Each event becomes a Debezium-compatible record: <c>op = "c"</c>, <c>key = {"id", "subject", "partitionKey"}</c> (the partition key is the
/// conversation or contact identifier, so key-partitioned brokers keep a conversation's events on one partition), <c>after</c> = the
/// CloudEvent (attributes and <c>data</c>) and <c>source = {connector: "ticketflow", name, schema: "ticketflow", table: &lt;area&gt;}</c> where
/// the area is <c>conversation</c>, <c>contact</c>, <c>sla</c>, <c>routing</c>, <c>survey</c> or <c>channel</c> (table-based WalFlow routing
/// and partition resolvers therefore route by area). Events are sent in order, then the sink is flushed once; any exception fails the
/// delivery (<c>broker-error</c>). Use <see cref="CloudEventSinkPayloadTransformer"/> where a sink accepts a payload transformer to publish the
/// structured CloudEvent as the message body instead of the Debezium envelope.
/// </summary>
public sealed class WalFlowEventSink : IEventSink
{
    public const string SinkKind = "walflow";

    private readonly IReadOnlyDictionary<string, WalFlowEventSinkRegistration> sinks;
    private readonly WalFlowEventSinkOptions options;

    public WalFlowEventSink(IOptions<WalFlowEventSinkOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        if (string.IsNullOrWhiteSpace(this.options.SourceName))
            throw new InvalidOperationException("WalFlow event sink options are invalid.");
        var index = new Dictionary<string, WalFlowEventSinkRegistration>(StringComparer.Ordinal);
        foreach (var registration in this.options.Sinks)
            if (registration is null || !EventText.IsToken(registration.Sink.Name, 64) || !index.TryAdd(registration.Sink.Name, registration))
                throw new InvalidOperationException("WalFlow sink registrations need unique token names (a-z, 0-9, '-', '_', '.').");
        sinks = index;
    }

    public string Kind => SinkKind;

    public int MaximumBatchSize => EventSubscription.MaximumBatchSize;

    public bool IsValidTarget(Core.TicketScope scope, string target) =>
        scope is not null && target is not null && sinks.TryGetValue(target, out var registration) && EventScopeRule.Allows(registration.AllowedScopes, scope)
        && (!options.RequireDurableSinks
            || registration.Sink.Sink is ISinkDurability { DurabilityGuarantee: SinkDurabilityGuarantee.RequestResponsePerSend or SinkDurabilityGuarantee.FlushAcknowledged });

    public async Task<EventSinkResult> DeliverAsync(EventDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var target = delivery.Subscription.Subscription.Sink.Target;
        if (!IsValidTarget(delivery.Scope, target))
            return new EventSinkFailed("sink-missing", permanent: true);
        var sink = sinks[target].Sink.Sink;
        try
        {
            foreach (var rendered in delivery.Events)
                await sink.SendAsync(Payload(rendered, options.SourceName), cancellationToken).ConfigureAwait(false);
            await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            return EventSinkDelivered.Instance;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Broker exception details may echo payloads; only a code is recorded.
            return new EventSinkFailed("broker-error");
        }
    }

    /// <summary>The Debezium-compatible record for one rendered event.</summary>
    public static DebeziumPayload Payload(RenderedEvent rendered, string sourceName = "ticketflow")
    {
        ArgumentNullException.ThrowIfNull(rendered);
        using var document = JsonDocument.Parse(rendered.Json);
        var after = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            after[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.Clone();
        var stored = rendered.Event;
        var type = stored.Event.Type;
        var firstDot = type.IndexOf('.');
        var secondDot = type.IndexOf('.', firstDot + 1);
        var area = type[(firstDot + 1)..secondDot];
        var partitionKey = stored.Resource.Conversation?.Value ?? stored.Resource.Contact?.Value ?? stored.Scope.WorkspaceId.Value;
        var milliseconds = stored.RecordedAt.ToUnixTimeMilliseconds();
        return new DebeziumPayload
        {
            Key = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = stored.Event.Id.ToString("D"),
                ["subject"] = stored.Event.Subject,
                ["partitionKey"] = partitionKey,
            },
            After = after,
            Operation = "c",
            TimestampMs = milliseconds,
            Source = new SourceInfo
            {
                Version = "ticketflow-events-v1",
                Connector = "ticketflow",
                Name = sourceName,
                TimestampMs = stored.Event.Time.ToUnixTimeMilliseconds(),
                Schema = "ticketflow",
                Table = area,
            },
        };
    }
}

/// <summary>
/// A WalFlow <see cref="ISinkPayloadTransformer"/> that publishes the structured CloudEvent (the record's <c>after</c>) as the message body
/// with content type <c>application/cloudevents+json</c>, for sinks constructed with a payload transformer.
/// </summary>
public sealed class CloudEventSinkPayloadTransformer : ISinkPayloadTransformer
{
    public ValueTask<SinkTransformedMessage> TransformAsync(DebeziumPayload payload, SinkPayloadTransformContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.After is null)
            throw new ArgumentException("TicketFlow event records carry the CloudEvent in 'after'.", nameof(payload));
        return ValueTask.FromResult(SinkTransformedMessage.FromBytes(JsonSerializer.SerializeToUtf8Bytes(payload.After), "application/cloudevents+json"));
    }
}
