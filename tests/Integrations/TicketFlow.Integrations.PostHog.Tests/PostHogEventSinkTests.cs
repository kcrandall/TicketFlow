using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Events;
using TicketFlow.Testing;

namespace TicketFlow.Integrations.PostHog.Tests;

[TestClass]
public sealed class PostHogEventSinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TicketScope Scope = TestScopes.Create(workspaceId: "acme");

    private sealed class Recorder(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(Uri Uri, string? MediaType, JsonDocument Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Content!.Headers.ContentType?.MediaType, JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken))));
            return new HttpResponseMessage(status);
        }
    }

    private static PostHogEventSink Sink(Recorder handler, PostHogDistinctIdStrategy strategy = PostHogDistinctIdStrategy.ContactId)
    {
        var options = new PostHogOptions { DistinctId = strategy };
        options.Projects["default"] = new PostHogProject(new Uri("https://eu.i.posthog.com/"), "phc_test_key", [new EventScopeRule(Scope.TenantId, workspaceId: Scope.WorkspaceId)]);
        return new PostHogEventSink(Options.Create(options), handler);
    }

    private static RenderedEvent Rendered(IntegrationEventData data, long sequence, EventPrivacy privacy = EventPrivacy.Metadata, EventContent? content = null, ContactId? contact = null)
    {
        var stored = new StoredIntegrationEvent(Scope, sequence, new IntegrationEvent(EventText.EventId("s" + sequence, data.Type), Now, null, data),
            new EventResource(contact: contact), Now);
        return new RenderedEvent(stored, IntegrationEventCodec.Render(stored, privacy, content));
    }

    private static EventDelivery Delivery(params RenderedEvent[] events) =>
        new(new StoredEventSubscription(Scope, new EventSubscription(new EventSubscriptionId("ph"), "PostHog", new EventSinkRef("posthog", "default"),
            EventSubscriptionFilter.Everything, new ApiClientId("analytics"), EventPrivacy.Metadata), 1, Now), events, 1);

    [TestMethod]
    public async Task BatchesEventsInThePostHogCaptureShape()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var sink = Sink(handler);
        var created = Rendered(new ConversationCreatedData(new ConversationId("c1"), new InboxId("email"), new ContactId("ada"), null, null, "open", "high", [], "email"), 1);
        var routed = Rendered(new RoutingAssignedData(new ConversationId("c1"), new InboxId("email"), new UserId("ann"), 3), 2, EventPrivacy.IdsOnly);
        Assert.IsInstanceOfType<EventSinkDelivered>((await sink.DeliverAsync(Delivery(created, routed))).Value);

        var (uri, mediaType, body) = handler.Requests.Single();
        Assert.AreEqual(new Uri("https://eu.i.posthog.com/batch/"), uri);
        Assert.AreEqual("application/json", mediaType);
        var root = body.RootElement;
        Assert.AreEqual("phc_test_key", root.GetProperty("api_key").GetString());
        Assert.IsFalse(root.GetProperty("historical_migration").GetBoolean());
        var batch = root.GetProperty("batch");
        Assert.AreEqual(2, batch.GetArrayLength());

        var first = batch[0];
        Assert.AreEqual(IntegrationEventTypes.ConversationCreated, first.GetProperty("event").GetString());
        Assert.AreEqual("contact:ada", first.GetProperty("distinct_id").GetString());
        Assert.AreEqual(created.Event.Event.Id.ToString("D"), first.GetProperty("uuid").GetString());
        Assert.AreEqual("2026-03-02T09:00:00.000000Z", first.GetProperty("timestamp").GetString());
        var properties = first.GetProperty("properties");
        Assert.AreEqual("high", properties.GetProperty("priority").GetString());
        Assert.AreEqual("acme", properties.GetProperty("$groups").GetProperty("workspace").GetString());
        Assert.AreEqual("1", properties.GetProperty("ticketflow_sequence").GetString());
        Assert.AreEqual("metadata", properties.GetProperty("ticketflow_privacy").GetString());
        Assert.IsFalse(properties.TryGetProperty("$process_person_profile", out _));

        var second = batch[1];
        Assert.AreEqual("workspace:acme", second.GetProperty("distinct_id").GetString());
        Assert.IsFalse(second.GetProperty("properties").GetProperty("$process_person_profile").GetBoolean());
        Assert.AreEqual("ann", second.GetProperty("properties").GetProperty("agent").GetString());
        Assert.IsFalse(second.GetProperty("properties").TryGetProperty("revision", out _), "IdsOnly properties carry no metadata.");
    }

    [TestMethod]
    public async Task ExternalIdStrategyUsesOnlyUnmaskedContentIdentities()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var sink = Sink(handler, PostHogDistinctIdStrategy.ContactExternalId);
        var contact = new ContactCreatedData(new ContactId("ada"));
        var withExternal = Rendered(contact, 1, EventPrivacy.Content,
            new EventContent(new ContactEventContent("Ada", [new EventIdentity("email", "", "ada@example.com", true), new EventIdentity("external-user", "idp", "user-42", true)]), []));
        var masked = Rendered(contact, 2, EventPrivacy.Content, new EventContent(new ContactEventContent("Ada", []), ["contact.external-id"]));
        await sink.DeliverAsync(Delivery(withExternal, masked));
        var batch = handler.Requests.Single().Body.RootElement.GetProperty("batch");
        Assert.AreEqual("external:idp:user-42", batch[0].GetProperty("distinct_id").GetString());
        Assert.AreEqual("contact:ada", batch[1].GetProperty("distinct_id").GetString(), "Falls back to the contact id when the external id is masked.");
    }

    [TestMethod]
    public async Task ConversationEventsUseTheConversationContactConsistently()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var sink = Sink(handler, PostHogDistinctIdStrategy.ContactExternalId);
        var sla = Rendered(new SlaBreachedData(new ConversationId("c1"), new InboxId("email"), "gold", "gold:1", "first-response", 1, TimeSpan.FromMinutes(30), Now), 1,
            contact: new ContactId("ada"));
        var related = new EventContent(null, [], new ContactEventContent("Ada", [new EventIdentity("external-user", "idp", "user-42", true)]));
        var message = Rendered(new ConversationMessageSentData(new ConversationId("c1"), new InboxId("email"), new MessageId("m1"), new EventActor("user", "ann"), 2, "plain-text", 0),
            2, EventPrivacy.Content, related, new ContactId("ada"));
        var other = Rendered(new ContactCreatedData(new ContactId("user-42")), 3);
        await sink.DeliverAsync(Delivery(sla, message, other));
        var batch = handler.Requests.Single().Body.RootElement.GetProperty("batch");
        Assert.AreEqual("contact:ada", batch[0].GetProperty("distinct_id").GetString(), "Metadata SLA events use the conversation's contact.");
        Assert.AreEqual("external:idp:user-42", batch[1].GetProperty("distinct_id").GetString(), "Content events use the related contact's external id.");
        Assert.AreEqual("contact:user-42", batch[2].GetProperty("distinct_id").GetString(), "Namespaces keep contact ids and external ids apart.");
    }

    [TestMethod]
    public async Task OtherScopesCannotDeliverToAProject()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        using var sink = Sink(handler);
        var rendered = Rendered(new ContactCreatedData(new ContactId("ada")), 1);
        var foreign = new EventDelivery(Delivery(rendered).Subscription with { Scope = TestScopes.Create(tenantId: "other-tenant", workspaceId: "acme") }, [rendered], 1);
        Assert.AreEqual("project-missing", ((EventSinkFailed)(await sink.DeliverAsync(foreign)).Value!).Code);
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task StatusesMapToRetriesAndPermanentFailures()
    {
        var created = Rendered(new ContactCreatedData(new ContactId("ada")), 1);
        foreach (var (status, permanent) in new[] { (HttpStatusCode.ServiceUnavailable, false), (HttpStatusCode.TooManyRequests, false), (HttpStatusCode.BadRequest, true),
                     (HttpStatusCode.Unauthorized, true) })
        {
            using var sink = Sink(new Recorder(status));
            var failed = (EventSinkFailed)(await sink.DeliverAsync(Delivery(created))).Value!;
            Assert.AreEqual(permanent, failed.Permanent, status.ToString());
        }
    }

    [TestMethod]
    public void TargetsNameConfiguredProjectsAndRegistrationWorks()
    {
        using var sink = Sink(new Recorder(HttpStatusCode.OK));
        Assert.IsTrue(sink.IsValidTarget(Scope, "default"));
        Assert.IsFalse(sink.IsValidTarget(Scope, "other"));
        Assert.IsFalse(sink.IsValidTarget(TestScopes.Create(workspaceId: "other-workspace"), "default"), "Projects are allow-listed per workspace.");
        Assert.IsFalse(sink.IsValidTarget(TestScopes.Create(tenantId: "other-tenant", workspaceId: "acme"), "default"), "Projects are allow-listed per tenant.");
        Assert.ThrowsExactly<ArgumentException>(() => new PostHogProject(new Uri("http://insecure.example.com/"), "phc", [new EventScopeRule("tenant")]));
        Assert.ThrowsExactly<ArgumentException>(() => new PostHogProject(new Uri("https://us.i.posthog.com/"), "phc", []));

        var services = new ServiceCollection();
        services.AddTicketFlow().AddPostHogEvents(options => options.Projects["default"] = new PostHogProject(new Uri("https://us.i.posthog.com/"), "phc", [new EventScopeRule("tenant")]));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.AreEqual("posthog", provider.GetServices<IEventSink>().Single().Kind);
    }

    [TestMethod]
    public async Task RetryAfterSupportsDeltaAndHttpDateClampedAndDoesNotBufferResponseBody()
    {
        var created = Rendered(new ContactCreatedData(new ContactId("ada")), 1);
        var clock = TestClock.Create(Now);

        // Response body must not be buffered (HttpCompletionOption.ResponseHeadersRead).
        var bodyRead = false;
        using (var okSink = SinkWithResponder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingReadContent(() => bodyRead = true) }, clock))
        {
            Assert.IsInstanceOfType<EventSinkDelivered>((await okSink.DeliverAsync(Delivery(created))).Value);
            Assert.IsFalse(bodyRead, "Response body must not be buffered.");
        }

        // Huge Retry-After delta is clamped to MaximumRetryAfter (default 1 hour).
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

        // HTTP-date in the future is honored (and clamped when huge); past HTTP-date clamps to TimeSpan.Zero.
        using (var futureDate = SinkWithResponder(_ =>
               {
                   var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                   response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(5));
                   return response;
               }, clock))
            Assert.AreEqual(TimeSpan.FromMinutes(5), ((EventSinkFailed)(await futureDate.DeliverAsync(Delivery(created))).Value!).RetryAfter);

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
                   response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(-10));
                   return response;
               }, clock))
            Assert.IsNull(((EventSinkFailed)(await pastDate.DeliverAsync(Delivery(created))).Value!).RetryAfter);
    }

    private static PostHogEventSink SinkWithResponder(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeProvider time)
    {
        var options = new PostHogOptions();
        options.Projects["default"] = new PostHogProject(new Uri("https://eu.i.posthog.com/"), "phc_test_key", [new EventScopeRule(Scope.TenantId, workspaceId: Scope.WorkspaceId)]);
        return new PostHogEventSink(Options.Create(options), new FunctionalHandler(respond), time);
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
