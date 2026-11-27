using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Events;
using TicketFlow.Testing;

namespace TicketFlow.Integrations.Segment.Tests;

[TestClass]
public sealed class SegmentEventSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TicketScope Scope = TestScopes.Create(workspaceId: "acme");

    private sealed class Recorder(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(Uri Uri, string? AuthScheme, string? AuthParam, string? MediaType, JsonDocument Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Content!.Headers.ContentType?.MediaType, JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken))));
            return new HttpResponseMessage(status);
        }
    }

    private static SegmentEventSink Sink(Recorder handler, SegmentUserIdStrategy strategy = SegmentUserIdStrategy.ContactId, int maxPayloadBytes = SegmentOptions.DefaultMaximumPayloadBytes)
    {
        var options = new SegmentOptions { UserId = strategy, MaximumPayloadBytes = maxPayloadBytes };
        options.Sources["default"] = new SegmentSource(new Uri("https://api.segment.io/"), "seg_write_key", [new EventScopeRule(Scope.TenantId, workspaceId: Scope.WorkspaceId)]);
        return new SegmentEventSink(Options.Create(options), handler);
    }

    private static RenderedEvent Rendered(IntegrationEventData data, long sequence, EventPrivacy privacy = EventPrivacy.Metadata, EventContent? content = null, ContactId? contact = null)
    {
        var stored = new StoredIntegrationEvent(Scope, sequence, new IntegrationEvent(EventText.EventId("s" + sequence, data.Type), Now, null, data),
            new EventResource(contact: contact), Now);
        return new RenderedEvent(stored, IntegrationEventCodec.Render(stored, privacy, content));
    }

    private static EventDelivery Delivery(params RenderedEvent[] events) =>
        new(new StoredEventSubscription(Scope, new EventSubscription(new EventSubscriptionId("seg"), "Segment", new EventSinkRef("segment", "default"),
            EventSubscriptionFilter.Everything, new ApiClientId("analytics"), EventPrivacy.Metadata), 1, Now), events, 1);

    [TestMethod]
    public async Task BatchesEventsInSegmentTrackShapeWithBasicAuth()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var sink = Sink(handler);
        var created = Rendered(new ConversationCreatedData(new ConversationId("c1"), new InboxId("email"), new ContactId("ada"), null, null, "open", "high", [], "email"), 1);
        var routed = Rendered(new RoutingAssignedData(new ConversationId("c1"), new InboxId("email"), new UserId("ann"), 3), 2, EventPrivacy.IdsOnly);
        Assert.IsInstanceOfType<EventSinkDelivered>((await sink.DeliverAsync(Delivery(created, routed))).Value);

        var (uri, scheme, param, mediaType, body) = handler.Requests.Single();
        Assert.AreEqual(new Uri("https://api.segment.io/v1/batch"), uri);
        Assert.AreEqual("Basic", scheme);
        Assert.AreEqual("seg_write_key:", Encoding.ASCII.GetString(Convert.FromBase64String(param!)));
        Assert.AreEqual("application/json", mediaType);
        var batch = body.RootElement.GetProperty("batch");
        Assert.AreEqual(2, batch.GetArrayLength());

        var first = batch[0];
        Assert.AreEqual("track", first.GetProperty("type").GetString());
        Assert.AreEqual(IntegrationEventTypes.ConversationCreated, first.GetProperty("event").GetString());
        Assert.AreEqual("contact:ada", first.GetProperty("userId").GetString());
        Assert.IsFalse(first.TryGetProperty("anonymousId", out _));
        Assert.AreEqual(created.Event.Event.Id.ToString("D"), first.GetProperty("messageId").GetString());
        Assert.AreEqual("2026-03-02T09:00:00.000000Z", first.GetProperty("timestamp").GetString());
        Assert.AreEqual("high", first.GetProperty("properties").GetProperty("priority").GetString());
        Assert.AreEqual("acme", first.GetProperty("context").GetProperty("groupId").GetString());

        var second = batch[1];
        Assert.IsFalse(second.TryGetProperty("userId", out _));
        Assert.AreEqual("workspace:acme", second.GetProperty("anonymousId").GetString());
        Assert.AreEqual("ann", second.GetProperty("properties").GetProperty("agent").GetString());
        Assert.IsFalse(second.GetProperty("properties").TryGetProperty("revision", out _));
    }

    [TestMethod]
    public async Task ExternalIdStrategyUsesUnmaskedExternalUserOrFallsBack()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var sink = Sink(handler, SegmentUserIdStrategy.ContactExternalId);
        var contact = new ContactCreatedData(new ContactId("ada"));
        var withExternal = Rendered(contact, 1, EventPrivacy.Content,
            new EventContent(new ContactEventContent("Ada", [new EventIdentity("external-user", "idp", "user-42", true)]), []));
        var masked = Rendered(contact, 2, EventPrivacy.Content, new EventContent(new ContactEventContent("Ada", []), ["contact.external-id"]));
        await sink.DeliverAsync(Delivery(withExternal, masked));
        var batch = handler.Requests.Single().Body.RootElement.GetProperty("batch");
        Assert.AreEqual("external:idp:user-42", batch[0].GetProperty("userId").GetString());
        Assert.AreEqual("contact:ada", batch[1].GetProperty("userId").GetString());
    }

    [TestMethod]
    public async Task OversizedBatchOrHttp413RequestsSplit()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var smallLimit = Sink(handler, maxPayloadBytes: 1024);
        var many = Enumerable.Range(1, 5).Select(i => Rendered(new ContactCreatedData(new ContactId("c" + i)), i)).ToArray();
        Assert.IsInstanceOfType<EventSinkSplit>((await smallLimit.DeliverAsync(Delivery(many))).Value);
        Assert.AreEqual(0, handler.Requests.Count);

        using var tooLarge = Sink(new Recorder(HttpStatusCode.RequestEntityTooLarge));
        Assert.IsInstanceOfType<EventSinkSplit>((await tooLarge.DeliverAsync(Delivery(many[0], many[1]))).Value);
        Assert.IsTrue(((EventSinkFailed)(await tooLarge.DeliverAsync(Delivery(many[0]))).Value!).Permanent);
    }

    [TestMethod]
    public async Task ScopeAllowListAndStatusCodesAreEnforced()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var sink = Sink(handler);
        var created = Rendered(new ContactCreatedData(new ContactId("ada")), 1);
        var foreign = new EventDelivery(Delivery(created).Subscription with { Scope = TestScopes.Create(tenantId: "other", workspaceId: "acme") }, [created], 1);
        Assert.AreEqual("source-missing", ((EventSinkFailed)(await sink.DeliverAsync(foreign)).Value!).Code);
        Assert.AreEqual(0, handler.Requests.Count);

        foreach (var (status, permanent) in new[] { (HttpStatusCode.TooManyRequests, false), (HttpStatusCode.BadGateway, false), (HttpStatusCode.BadRequest, true), (HttpStatusCode.Unauthorized, true) })
        {
            using var statusSink = Sink(new Recorder(status));
            Assert.AreEqual(permanent, ((EventSinkFailed)(await statusSink.DeliverAsync(Delivery(created))).Value!).Permanent, status.ToString());
        }

        Assert.IsFalse(new SegmentSource("secret-key", [new EventScopeRule("t")]).ToString().Contains("secret-key", StringComparison.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => new SegmentSource(new Uri("http://api.segment.io/"), "key", [new EventScopeRule("t")]));
        Assert.ThrowsExactly<ArgumentException>(() => new SegmentSource("key", []));

        var services = new ServiceCollection();
        services.AddTicketFlow().AddSegmentEvents(o => o.Sources["default"] = new SegmentSource("key", [new EventScopeRule("tenant")]));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.AreEqual("segment", provider.GetServices<IEventSink>().Single().Kind);
    }

    [TestMethod]
    public async Task RetryAfterSupportsDeltaAndHttpDateClampedAndDoesNotBufferResponseBody()
    {
        var created = Rendered(new ContactCreatedData(new ContactId("ada")), 1);
        var clock = TestClock.Create(Now);

        var bodyRead = false;
        using (var okSink = SinkWithResponder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingReadContent(() => bodyRead = true) }, clock))
        {
            Assert.IsInstanceOfType<EventSinkDelivered>((await okSink.DeliverAsync(Delivery(created))).Value);
            Assert.IsFalse(bodyRead, "Response body must not be buffered.");
        }

        using (var hugeDelta = SinkWithResponder(_ =>
               {
                   var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new ThrowingReadContent(() => bodyRead = true) };
                   response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(365));
                   return response;
               }, clock))
        {
            var failed = (EventSinkFailed)(await hugeDelta.DeliverAsync(Delivery(created))).Value!;
            Assert.AreEqual(TimeSpan.FromHours(1), failed.RetryAfter);
            Assert.IsFalse(bodyRead);
        }

        using (var futureDate = SinkWithResponder(_ =>
               {
                   var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                   response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(3));
                   return response;
               }, clock))
            Assert.AreEqual(TimeSpan.FromMinutes(3), ((EventSinkFailed)(await futureDate.DeliverAsync(Delivery(created))).Value!).RetryAfter);

        using (var hugeDate = SinkWithResponder(_ =>
               {
                   var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                   response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddDays(30));
                   return response;
               }, clock))
            Assert.AreEqual(TimeSpan.FromHours(1), ((EventSinkFailed)(await hugeDate.DeliverAsync(Delivery(created))).Value!).RetryAfter);

        using (var pastDate = SinkWithResponder(_ =>
               {
                   var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                   response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(-5));
                   return response;
               }, clock))
            Assert.IsNull(((EventSinkFailed)(await pastDate.DeliverAsync(Delivery(created))).Value!).RetryAfter);
    }

    private static SegmentEventSink SinkWithResponder(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeProvider time)
    {
        var options = new SegmentOptions();
        options.Sources["default"] = new SegmentSource(new Uri("https://api.segment.io/"), "seg_write_key", [new EventScopeRule(Scope.TenantId, workspaceId: Scope.WorkspaceId)]);
        return new SegmentEventSink(Options.Create(options), new FunctionalHandler(respond), time);
    }

    private sealed class FunctionalHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ThrowingReadContent(Action onRead) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            onRead();
            throw new InvalidOperationException("Response body should not be read or buffered.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
