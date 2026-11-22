using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Channels.Api;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Events.Webhooks.Tests;

[TestClass]
public sealed class WebhookEventSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(100, 32).Select(index => (byte)index).ToArray();

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static readonly TicketScope Scope = TestScopes.Create();

    private static EventDelivery Delivery(int count = 1, string target = "https://hooks.example.com/tf", int attempt = 1)
    {
        var subscription = new StoredEventSubscription(Scope, new EventSubscription(new EventSubscriptionId("hook"), "Hook", new EventSinkRef("webhook", target),
            EventSubscriptionFilter.Everything, new ApiClientId("hooks")), 1, Now);
        var events = Enumerable.Range(1, count).Select(index =>
        {
            var data = new ConversationAssignedData(new ConversationId("c1"), new InboxId("email"), null, new UserId("ann"));
            var stored = new StoredIntegrationEvent(Scope, index, new IntegrationEvent(EventText.EventId("s" + index, data.Type), Now, null, data), new EventResource(), Now);
            return new RenderedEvent(stored, IntegrationEventCodec.Render(stored, EventPrivacy.IdsOnly));
        }).ToList();
        return new EventDelivery(subscription, events, attempt);
    }

    private static (WebhookEventSink Sink, Recorder Handler, InMemoryEventWebhookKeyRing Keys) Create(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new Recorder(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var keys = new InMemoryEventWebhookKeyRing();
        keys.Set(Scope, new EventSubscriptionId("hook"), "key-a", KeyA);
        var sink = new WebhookEventSink(Options.Create(new EventWebhookOptions()), TestClock.Create(Now), keys, handler);
        return (sink, handler, keys);
    }

    private static string Header(HttpRequestMessage request, string name) => request.Headers.GetValues(name).Single();

    [TestMethod]
    public async Task SignsCloudEventsWithTheApiChannelScheme()
    {
        var (sink, handler, _) = Create();
        using var _ = sink;
        Assert.IsInstanceOfType<EventSinkDelivered>((await sink.DeliverAsync(Delivery())).Value);
        var (request, body) = handler.Requests.Single();
        Assert.AreEqual("application/cloudevents+json", request.Content!.Headers.ContentType!.MediaType);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(Delivery().DeliveryId, Header(request, ApiWebhookHeaders.Id));
        Assert.AreEqual("key-a", Header(request, ApiWebhookHeaders.KeyId));
        Assert.AreEqual("1", Header(request, EventWebhookHeaders.Attempt));
        Assert.IsFalse(request.Headers.Contains(EventWebhookHeaders.PreviousSignature));
        var verified = ApiWebhookSignature.Verify(KeyA, Header(request, ApiWebhookHeaders.Id), Header(request, ApiWebhookHeaders.Timestamp),
            Header(request, ApiWebhookHeaders.Signature), body, Now);
        Assert.IsInstanceOfType<ApiWebhookVerified>(verified.Value);
        Assert.IsInstanceOfType<ApiWebhookRejected>(ApiWebhookSignature.Verify(KeyB, Header(request, ApiWebhookHeaders.Id), Header(request, ApiWebhookHeaders.Timestamp),
            Header(request, ApiWebhookHeaders.Signature), body, Now).Value);
        using var document = JsonDocument.Parse(body);
        Assert.AreEqual(IntegrationEventTypes.ConversationAssigned, document.RootElement.GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task RotationSignsWithBothKeysUntilCompleted()
    {
        var (sink, handler, keys) = Create();
        using var _ = sink;
        keys.Rotate(Scope, new EventSubscriptionId("hook"), "key-b", KeyB);
        await sink.DeliverAsync(Delivery(3, attempt: 2));
        var (request, body) = handler.Requests.Single();
        Assert.AreEqual("application/cloudevents-batch+json", request.Content!.Headers.ContentType!.MediaType);
        Assert.AreEqual(3, JsonDocument.Parse(body).RootElement.GetArrayLength());
        Assert.AreEqual("key-b", Header(request, ApiWebhookHeaders.KeyId));
        Assert.AreEqual("key-a", Header(request, EventWebhookHeaders.PreviousKeyId));
        var id = Header(request, ApiWebhookHeaders.Id);
        var timestamp = Header(request, ApiWebhookHeaders.Timestamp);
        Assert.IsInstanceOfType<ApiWebhookVerified>(ApiWebhookSignature.Verify(KeyB, id, timestamp, Header(request, ApiWebhookHeaders.Signature), body, Now).Value);
        Assert.IsInstanceOfType<ApiWebhookVerified>(ApiWebhookSignature.Verify(KeyA, id, timestamp, Header(request, EventWebhookHeaders.PreviousSignature), body, Now).Value);

        keys.CompleteRotation(Scope, new EventSubscriptionId("hook"));
        await sink.DeliverAsync(Delivery());
        Assert.IsFalse(handler.Requests[1].Request.Headers.Contains(EventWebhookHeaders.PreviousSignature));
        Assert.AreEqual(Delivery(3).DeliveryId, id, "The delivery id is stable across attempts.");
    }

    [TestMethod]
    public async Task StatusesMapToRetriesAndPermanentFailures()
    {
        foreach (var (status, permanent) in new[] { (HttpStatusCode.InternalServerError, false), (HttpStatusCode.TooManyRequests, false),
                     (HttpStatusCode.RequestTimeout, false), (HttpStatusCode.Gone, true), (HttpStatusCode.Unauthorized, true), (HttpStatusCode.Redirect, true) })
        {
            var (sink, _, _) = Create(_ =>
            {
                var response = new HttpResponseMessage(status);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
                return response;
            });
            using var disposable = sink;
            var failed = (EventSinkFailed)(await sink.DeliverAsync(Delivery())).Value!;
            Assert.AreEqual(("http-" + (int)status, permanent), (failed.Code, failed.Permanent));
            if (!permanent)
                Assert.AreEqual(TimeSpan.FromSeconds(90), failed.RetryAfter);
        }
    }

    [TestMethod]
    public async Task TargetsAndKeysAreValidated()
    {
        var (sink, handler, _) = Create();
        using var _ = sink;
        Assert.IsTrue(sink.IsValidTarget(Scope, "https://hooks.example.com/tf"));
        foreach (var invalid in new[] { "http://hooks.example.com/tf", "https://localhost/x", "https://10.0.0.5/x", "https://169.254.169.254/latest", "https://hooks.example.com:8443/x",
                     "https://user:pw@hooks.example.com/x", "not a url" })
            Assert.IsFalse(sink.IsValidTarget(Scope, invalid), invalid);
        var blocked = (EventSinkFailed)(await sink.DeliverAsync(Delivery(target: "https://127.0.0.1/x"))).Value!;
        Assert.AreEqual(("endpoint-blocked", true), (blocked.Code, blocked.Permanent));

        var unkeyed = new WebhookEventSink(Options.Create(new EventWebhookOptions()), TestClock.Create(Now), new InMemoryEventWebhookKeyRing(), handler);
        using var disposable = unkeyed;
        var missing = (EventSinkFailed)(await unkeyed.DeliverAsync(Delivery())).Value!;
        Assert.AreEqual(("key-missing", false), (missing.Code, missing.Permanent));
        Assert.AreEqual(0, handler.Requests.Count, "Nothing is sent without a valid target and key.");
        Assert.ThrowsExactly<ArgumentException>(() => new EventWebhookKey("short", new byte[16]));
    }

    [TestMethod]
    public async Task OversizedBatchesAskTheEngineToSplit()
    {
        var (sink, handler, _) = Create();
        using var _ = sink;
        var big = Delivery(2);
        var padded = new EventDelivery(big.Subscription, [.. big.Events.Select(item => item with { Json = item.Json.Replace("\"specversion\"", "\"pad\":\"" + new string('x', 600_000) + "\",\"specversion\"") })], 1);
        Assert.IsInstanceOfType<EventSinkSplit>((await sink.DeliverAsync(padded)).Value);
        var single = new EventDelivery(big.Subscription, [padded.Events[0]], 1);
        var tooLarge = new EventDelivery(big.Subscription, [padded.Events[0] with { Json = padded.Events[0].Json.Replace("\"pad\":\"", "\"pad\":\"" + new string('y', 600_000)) }], 1);
        Assert.AreEqual(("payload-too-large", true), (((EventSinkFailed)(await sink.DeliverAsync(tooLarge)).Value!).Code, ((EventSinkFailed)(await sink.DeliverAsync(tooLarge)).Value!).Permanent));
        Assert.IsInstanceOfType<EventSinkDelivered>((await sink.DeliverAsync(single)).Value);
        Assert.AreEqual(1, handler.Requests.Count, "Oversized batches are never sent.");

        var (rejecting, _, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));
        using var disposable = rejecting;
        Assert.IsInstanceOfType<EventSinkSplit>((await rejecting.DeliverAsync(Delivery(2))).Value, "413 on a batch splits it.");
    }

    [TestMethod]
    public void DeliveryIdsChangeWithReplayGenerationAndRevision()
    {
        var delivery = Delivery(2);
        Assert.AreNotEqual(delivery.DeliveryId, (delivery with { Generation = 1 }).DeliveryId);
        Assert.AreNotEqual(delivery.DeliveryId, new EventDelivery(delivery.Subscription with { Revision = 2 }, delivery.Events, 1).DeliveryId);
        Assert.AreEqual(delivery.DeliveryId, (delivery with { Attempt = 5 }).DeliveryId);
    }

    [TestMethod]
    public void RegistrationAddsTheWebhookSink()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow().AddEventWebhooks(options => options.AllowedHosts.Add("hooks.example.com"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var sink = provider.GetServices<IEventSink>().Single();
        Assert.AreEqual("webhook", sink.Kind);
        Assert.IsFalse(sink.IsValidTarget(Scope, "https://other.example.com/x"), "The host allow-list applies.");
    }
}
