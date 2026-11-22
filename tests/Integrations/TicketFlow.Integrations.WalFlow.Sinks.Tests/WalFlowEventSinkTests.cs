using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Events;
using TicketFlow.Testing;
using WalFlow.Abstractions.Models;
using WalFlow.Abstractions.Sinks;
using WalFlow.Abstractions.Sinks.Transport;

namespace TicketFlow.Integrations.WalFlow.Sinks.Tests;

[TestClass]
public sealed class WalFlowEventSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TicketScope Scope = TestScopes.Create();

    private class RecordingSink : ISink
    {
        public List<DebeziumPayload> Sent { get; } = [];
        public int Flushes { get; private set; }
        public bool Fail { get; set; }

        public Task SendAsync(DebeziumPayload payload, CancellationToken ct = default)
        {
            if (Fail)
                throw new InvalidOperationException("broker down: payload echo");
            Sent.Add(payload);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct = default)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }

    private sealed class DurableSink : RecordingSink, ISinkDurability
    {
        public SinkDurabilityGuarantee DurabilityGuarantee => SinkDurabilityGuarantee.FlushAcknowledged;
    }

    private static RenderedEvent Rendered(IntegrationEventData data, long sequence, EventResource resource)
    {
        var stored = new StoredIntegrationEvent(Scope, sequence, new IntegrationEvent(EventText.EventId("s" + sequence, data.Type), Now, null, data), resource, Now);
        return new RenderedEvent(stored, IntegrationEventCodec.Render(stored, EventPrivacy.Metadata));
    }

    private static EventDelivery Delivery(string target, params RenderedEvent[] events) =>
        new(new StoredEventSubscription(Scope, new EventSubscription(new EventSubscriptionId("broker"), "Broker", new EventSinkRef("walflow", target),
            EventSubscriptionFilter.Everything, new ApiClientId("broker")), 1, Now), events, 1);

    private static WalFlowEventSink Sink(Action<WalFlowEventSinkOptions> configure)
    {
        var options = new WalFlowEventSinkOptions();
        configure(options);
        return new WalFlowEventSink(Options.Create(options));
    }

    [TestMethod]
    public async Task EventsBecomeOrderedDebeziumRecordsKeyedByConversation()
    {
        var broker = new RecordingSink();
        var sink = Sink(options => options.Add(new SinkRegistration("kafka", broker), new EventScopeRule(Scope.TenantId)));
        var conversation = new EventResource(new ConversationId("c1"), new InboxId("email"));
        var first = Rendered(new ConversationStatusChangedData(new ConversationId("c1"), new InboxId("email"), "open", "pending"), 7, conversation);
        var second = Rendered(new ContactUpdatedData(new ContactId("ada")), 8, new EventResource(contact: new ContactId("ada")));
        Assert.IsInstanceOfType<EventSinkDelivered>((await sink.DeliverAsync(Delivery("kafka", first, second))).Value);
        Assert.AreEqual(1, broker.Flushes);
        Assert.AreEqual(2, broker.Sent.Count);

        var record = broker.Sent[0];
        Assert.AreEqual("c", record.Operation);
        Assert.AreEqual("c1", record.Key!["partitionKey"]);
        Assert.AreEqual(first.Event.Event.Id.ToString("D"), record.Key["id"]);
        Assert.AreEqual(("ticketflow", "ticketflow", "conversation"), (record.Source.Connector, record.Source.Schema, record.Source.Table));
        Assert.AreEqual(IntegrationEventTypes.ConversationStatusChanged, record.After!["type"]);
        Assert.AreEqual("7", record.After["sequence"]);
        Assert.AreEqual("pending", ((JsonElement)record.After["data"]!).GetProperty("current").GetString());
        Assert.AreEqual(("ada", "contact"), ((string?)broker.Sent[1].Key!["partitionKey"], broker.Sent[1].Source.Table));

        var body = await new CloudEventSinkPayloadTransformer().TransformAsync(record, new SinkPayloadTransformContext());
        Assert.AreEqual("application/cloudevents+json", body.ContentType);
        using var json = JsonDocument.Parse(body.GetBytes());
        Assert.AreEqual(IntegrationEventTypes.ConversationStatusChanged, json.RootElement.GetProperty("type").GetString());
        Assert.AreEqual("pending", json.RootElement.GetProperty("data").GetProperty("current").GetString());
    }

    [TestMethod]
    public async Task BrokerFailuresAndUnknownTargetsFail()
    {
        var broker = new RecordingSink { Fail = true };
        var sink = Sink(options => options.Add(new SinkRegistration("rabbit", broker), new EventScopeRule(Scope.TenantId)));
        var rendered = Rendered(new ContactCreatedData(new ContactId("ada")), 1, new EventResource(contact: new ContactId("ada")));
        var failed = (EventSinkFailed)(await sink.DeliverAsync(Delivery("rabbit", rendered))).Value!;
        Assert.AreEqual(("broker-error", false), (failed.Code, failed.Permanent));
        Assert.AreEqual(("sink-missing", true), (((EventSinkFailed)(await sink.DeliverAsync(Delivery("other", rendered))).Value!).Code,
            ((EventSinkFailed)(await sink.DeliverAsync(Delivery("other", rendered))).Value!).Permanent));
    }

    [TestMethod]
    public void DurableSinksCanBeRequired()
    {
        var sink = Sink(options =>
        {
            options.RequireDurableSinks = true;
            options.Add(new SinkRegistration("buffered", new RecordingSink()), new EventScopeRule(Scope.TenantId));
            options.Add(new SinkRegistration("durable", new DurableSink()), new EventScopeRule(Scope.TenantId, workspaceId: Scope.WorkspaceId));
        });
        Assert.IsFalse(sink.IsValidTarget(Scope, "buffered"));
        Assert.IsTrue(sink.IsValidTarget(Scope, "durable"));
        Assert.IsFalse(sink.IsValidTarget(TestScopes.Create(workspaceId: "other"), "durable"), "Sinks are allow-listed per workspace.");
        Assert.IsFalse(sink.IsValidTarget(TestScopes.Create(tenantId: "other"), "durable"), "Sinks are allow-listed per tenant.");
        Assert.ThrowsExactly<ArgumentException>(() => new WalFlowEventSinkRegistration(new SinkRegistration("x", new RecordingSink()), []));
        Assert.ThrowsExactly<InvalidOperationException>(() => Sink(options =>
        {
            options.Add(new SinkRegistration("a", new RecordingSink()), new EventScopeRule("tenant"));
            options.Add(new SinkRegistration("a", new RecordingSink()), new EventScopeRule("tenant"));
        }));

        var services = new ServiceCollection();
        services.AddTicketFlow().AddWalFlowEventSinks(options => options.Add(new SinkRegistration("kafka", new RecordingSink()), new EventScopeRule(Scope.TenantId)));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsTrue(provider.GetServices<IEventSink>().Single().IsValidTarget(Scope, "kafka"));
    }
}
