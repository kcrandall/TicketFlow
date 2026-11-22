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
        options.Projects["default"] = new PostHogProject(new Uri("https://eu.i.posthog.com/"), "phc_test_key");
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
        Assert.AreEqual("ada", first.GetProperty("distinct_id").GetString());
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
        Assert.AreEqual("user-42", batch[0].GetProperty("distinct_id").GetString());
        Assert.AreEqual("ada", batch[1].GetProperty("distinct_id").GetString(), "Falls back to the contact id when the external id is masked.");
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
        Assert.IsTrue(sink.IsValidTarget("default"));
        Assert.IsFalse(sink.IsValidTarget("other"));
        Assert.ThrowsExactly<ArgumentException>(() => new PostHogProject(new Uri("http://insecure.example.com/"), "phc"));

        var services = new ServiceCollection();
        services.AddTicketFlow().AddPostHogEvents(options => options.Projects["default"] = new PostHogProject(new Uri("https://us.i.posthog.com/"), "phc"));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.AreEqual("posthog", provider.GetServices<IEventSink>().Single().Kind);
    }
}
