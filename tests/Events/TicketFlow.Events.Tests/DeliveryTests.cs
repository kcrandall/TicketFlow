using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Events.Tests;

[TestClass]
public sealed class DeliveryTests
{
    private static JsonElement Data(RenderedEvent rendered) => JsonElement.Parse(rendered.Json).GetProperty("data");

    private static async Task<EventsFixture> WithConversationAsync(string? externalId = null)
    {
        var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1", team: "billing", externalId: externalId);
        await fixture.ReplyAsync("c1");
        await fixture.NoteAsync("c1");
        await fixture.DispatchAsync();
        return fixture;
    }

    [TestMethod]
    public async Task IdsOnlyAndMetadataNeverCarryBodies()
    {
        using var fixture = await WithConversationAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("ids"));
        await fixture.DeliverAllAsync();
        var ids = fixture.Subscriber.Events.ToList();
        Assert.AreEqual(4, ids.Count);
        Assert.IsTrue(ids.All(item => item.Json.Contains("\"privacy\":\"ids-only\"", StringComparison.Ordinal)));
        Assert.IsFalse(Data(ids[0]).TryGetProperty("status", out _), "Metadata is not part of ids-only.");
        Assert.AreEqual("c1", Data(ids[0]).GetProperty("conversation").GetString());

        fixture.Subscriber.Deliveries.Clear();
        EventsFixture.Value(await fixture.SubscribeAsync("meta", privacy: EventPrivacy.Metadata));
        await fixture.DeliverAllAsync();
        var meta = fixture.Subscriber.Events.ToList();
        Assert.AreEqual("open", Data(meta[0]).GetProperty("status").GetString());
        Assert.AreEqual("plain-text", Data(meta[1]).GetProperty("contentType").GetString());
        foreach (var item in ids.Concat(meta))
        {
            foreach (var secret in new[] { "Help please", "4111", "Refund issued", "VIP", "ada@example.com" })
                Assert.IsFalse(item.Json.Contains(secret, StringComparison.Ordinal), $"{item.Event.Event.Type} leaked content.");
            Assert.IsFalse(Data(item).TryGetProperty("content", out _));
        }
    }

    [TestMethod]
    public async Task ContentHydratesThroughFeatureServicesAndHonorsMasking()
    {
        using var fixture = await WithConversationAsync(externalId: "provider-msg-1");
        await fixture.MaskAsync(ConversationFieldPaths.SensitivePolicy(ConversationFieldPaths.Channel));
        await fixture.MaskAsync(ContactFieldPaths.SensitivePolicy(ContactFieldPaths.Email));
        await fixture.InScopeAsync(provider => provider.GetRequiredService<EventContactFollower>().CatchUpAsync(fixture.Scope));
        EventsFixture.Value(await fixture.SubscribeAsync("content", principal: "content-hooks", privacy: EventPrivacy.Content));
        await fixture.DeliverAllAsync();
        var events = fixture.Subscriber.Events.ToList();
        var received = events.Single(item => string.Equals(item.Event.Event.Type, IntegrationEventTypes.ConversationMessageReceived, StringComparison.Ordinal));
        var content = Data(received).GetProperty("content");
        Assert.AreEqual("message", content.GetProperty("kind").GetString());
        StringAssert.Contains(content.GetProperty("body").GetString(), "Help please", StringComparison.Ordinal);
        Assert.IsFalse(content.TryGetProperty("externalId", out _), "The masked channel identifier is removed.");
        var maskedFields = content.GetProperty("maskedFields").EnumerateArray().Select(field => field.GetString()).ToList();
        CollectionAssert.Contains(maskedFields, "conversation.channel");
        CollectionAssert.Contains(maskedFields, "contact.email", "The conversation's contact is attached masked.");
        Assert.AreEqual("ada", content.GetProperty("contact").GetProperty("displayName").GetString());
        Assert.AreEqual(0, content.GetProperty("contact").GetProperty("identities").GetArrayLength());
        var sent = events.Single(item => string.Equals(item.Event.Event.Type, IntegrationEventTypes.ConversationMessageSent, StringComparison.Ordinal));
        Assert.AreEqual("Refund issued", Data(sent).GetProperty("content").GetProperty("body").GetString());
        var note = events.Single(item => string.Equals(item.Event.Event.Type, IntegrationEventTypes.ConversationNoteAdded, StringComparison.Ordinal));
        Assert.IsFalse(note.Json.Contains("VIP", StringComparison.Ordinal), "Notes never carry bodies.");
        var contact = events.Single(item => string.Equals(item.Event.Event.Type, IntegrationEventTypes.ContactCreated, StringComparison.Ordinal));
        var contactContent = Data(contact).GetProperty("content");
        Assert.AreEqual("ada", contactContent.GetProperty("displayName").GetString());
        Assert.AreEqual(0, contactContent.GetProperty("identities").GetArrayLength(), "The masked email is removed.");
        Assert.AreEqual("contact.email", contactContent.GetProperty("maskedFields")[0].GetString());
        Assert.IsFalse(contact.Json.Contains("ada@example.com", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ContentRequiresEventsContentForTheManagerAndThePrincipal()
    {
        using var fixture = await WithConversationAsync();
        var refused = (TicketFailure)(await fixture.SubscribeAsync("c", principal: "content-hooks", privacy: EventPrivacy.Content, actor: fixture.Manager)).Value!;
        Assert.AreEqual(TicketFailureKind.Forbidden, refused.Kind);
        EventsFixture.Value(await fixture.SubscribeAsync("c", principal: "hooks", privacy: EventPrivacy.Content));
        var run = await fixture.DeliverAsync();
        Assert.AreEqual(1, run.Retried);
        var state = await fixture.Get<IEventSubscriptionStore>().GetStateAsync(fixture.Scope, new EventSubscriptionId("c"));
        Assert.AreEqual(("content-forbidden", 0L), (state!.LastError, state.Cursor));
        Assert.AreEqual(0, fixture.Subscriber.Deliveries.Count);
    }

    [TestMethod]
    public async Task FiltersSelectTypesInboxesAndTeams()
    {
        using var fixture = await WithConversationAsync();
        await fixture.StartAsync("c2", inbox: "chat", team: "support");
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("messages", filter: new EventSubscriptionFilter([new EventTypePattern("ticketflow.conversation.message-received.v1")])));
        await fixture.DeliverAllAsync();
        CollectionAssert.AreEqual(new[] { "c1", "c2" }, fixture.Subscriber.Events.Select(item => item.Event.Resource.Conversation!.Value.Value).ToArray());

        fixture.Subscriber.Deliveries.Clear();
        EventsFixture.Value(await fixture.SubscribeAsync("chat", filter: new EventSubscriptionFilter([EventTypePattern.All], inboxes: [new InboxId("chat")])));
        await fixture.DeliverAllAsync();
        Assert.IsTrue(fixture.Subscriber.Events.All(item => item.Event.Resource.Inbox == new InboxId("chat")));
        Assert.AreEqual(2, fixture.Subscriber.Events.Count());

        fixture.Subscriber.Deliveries.Clear();
        EventsFixture.Value(await fixture.SubscribeAsync("billing", filter: new EventSubscriptionFilter([new EventTypePattern("ticketflow.conversation.*")], teams: [new TeamId("billing")])));
        await fixture.DeliverAllAsync();
        Assert.AreEqual(4, fixture.Subscriber.Events.Count());
        Assert.IsTrue(fixture.Subscriber.Events.All(item => item.Event.Resource.Team == new TeamId("billing")));
    }

    [TestMethod]
    public async Task EventsAreAuthorizedForTheSubscriptionPrincipal()
    {
        using var fixture = await WithConversationAsync();
        await fixture.StartAsync("c2", team: "support");
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("team", principal: "billing-hooks"));
        await fixture.DeliverAllAsync();
        Assert.AreEqual(4, fixture.Subscriber.Events.Count());
        Assert.IsTrue(fixture.Subscriber.Events.All(item => item.Event.Resource.Conversation == new ConversationId("c1")));
        var state = await fixture.Get<IEventSubscriptionStore>().GetStateAsync(fixture.Scope, new EventSubscriptionId("team"));
        Assert.AreEqual((await fixture.Log.GetHeadAsync(fixture.Scope)).Last, state!.Cursor, "Unauthorized events are skipped, not retried.");

        EventsFixture.Value(await fixture.SubscribeAsync("nobody", principal: "unknown-client"));
        Assert.AreEqual(1, (await fixture.DeliverAsync()).Retried);
        Assert.AreEqual("principal-forbidden", (await fixture.Get<IEventSubscriptionStore>().GetStateAsync(fixture.Scope, new EventSubscriptionId("nobody")))!.LastError);
    }

    [TestMethod]
    public async Task BatchesRespectSizeAndPerConversationOrder()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("a");
        await fixture.StartAsync("b");
        await fixture.AssignAsync("a", "agent");
        await fixture.AssignAsync("b", "agent");
        await fixture.StatusAsync("a", ConversationStatus.Resolved);
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("small", batchSize: 2));
        await fixture.DeliverAllAsync();
        Assert.IsTrue(fixture.Subscriber.Deliveries.All(delivery => delivery.Events.Count <= 2));
        var sequences = fixture.Subscriber.Events.Select(item => item.Event.Sequence).ToList();
        CollectionAssert.AreEqual(sequences.Order().ToArray(), sequences.ToArray());
        Assert.AreEqual((await fixture.Log.GetHeadAsync(fixture.Scope)).Last, sequences.Count);
        Assert.AreEqual(sequences.Count, sequences.Distinct().Count());
    }

    [TestMethod]
    public async Task FailuresBackOffThenDeadLetterAndReplayRedelivers()
    {
        using var fixture = await WithConversationAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("flaky", batchSize: 4));
        var id = new EventSubscriptionId("flaky");
        var store = fixture.Get<IEventSubscriptionStore>();
        fixture.Subscriber.Fail = true;

        Assert.AreEqual(1, (await fixture.DeliverAsync()).Retried);
        var state = (await store.GetStateAsync(fixture.Scope, id))!;
        Assert.AreEqual((1, "subscriber-error", 0L), (state.Attempts, state.LastError, state.Cursor));
        Assert.AreEqual(fixture.Clock.GetUtcNow().AddSeconds(10), state.AvailableAt);
        Assert.AreEqual(0, (await fixture.DeliverAsync()).Retried, "Not due before the backoff.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, (await fixture.DeliverAsync()).Retried);
        Assert.AreEqual(fixture.Clock.GetUtcNow().AddSeconds(20), (await store.GetStateAsync(fixture.Scope, id))!.AvailableAt, "Exponential backoff.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.AreEqual(1, (await fixture.DeliverAsync()).DeadLettered, "The third attempt dead-letters.");
        state = (await store.GetStateAsync(fixture.Scope, id))!;
        Assert.AreEqual((0, 4L), (state.Attempts, state.Cursor));

        var letters = EventsFixture.Value(await fixture.InScopeAsync(provider => provider.GetRequiredService<IEventSubscriptionService>().ListDeadLettersAsync(fixture.Owner, id)));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, letters.Select(letter => letter.Sequence).ToArray());
        Assert.IsTrue(letters.All(letter => string.Equals(letter.Error, "subscriber-error", StringComparison.Ordinal) && letter.Attempts == 3));

        fixture.Subscriber.Fail = false;
        var refused = await fixture.InScopeAsync(provider => provider.GetRequiredService<IEventSubscriptionService>().ReplayAsync(fixture.Agent, id, 0));
        Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)refused.Value!).Kind);
        EventsFixture.Value(await fixture.InScopeAsync(provider => provider.GetRequiredService<IEventSubscriptionService>().ReplayAsync(fixture.Manager, id, 2)));
        await fixture.DeliverAllAsync();
        CollectionAssert.AreEqual(new long[] { 3, 4 }, fixture.Subscriber.Events.Select(item => item.Event.Sequence).ToArray());
        Assert.AreEqual(2, EventsFixture.Value(await fixture.InScopeAsync(provider => provider.GetRequiredService<IEventSubscriptionService>()
            .ListDeadLettersAsync(fixture.Owner, id))).Count, "Replay clears dead letters after its position only.");
        var replayed = fixture.Subscriber.Deliveries[0];
        Assert.AreEqual(1, replayed.Generation, "Replayed deliveries carry the new replay generation.");
        Assert.AreEqual(replayed.DeliveryId, new EventDelivery(replayed.Subscription, replayed.Events, 9, replayed.Generation).DeliveryId, "Delivery ids are stable across attempts.");
        Assert.AreNotEqual(replayed.DeliveryId, new EventDelivery(replayed.Subscription, replayed.Events, 1, 0).DeliveryId,
            "A replay never reuses the delivery ids receivers already deduplicated.");
    }

    [TestMethod]
    public async Task SubscriptionManagementIsAccessChecked()
    {
        using var fixture = await EventsFixture.CreateAsync();
        var service = (IServiceProvider provider) => provider.GetRequiredService<IEventSubscriptionService>();
        Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)(await fixture.SubscribeAsync("s1", actor: fixture.Agent)).Value!).Kind);
        var view = EventsFixture.Value(await fixture.SubscribeAsync("s1", actor: fixture.Manager, start: EventStartAtLatest.Instance));
        Assert.AreEqual(1, view.Subscription.Revision);
        Assert.AreEqual(0, view.Delivery!.Cursor);

        var invalid = await fixture.InScopeAsync(provider => service(provider).CreateAsync(fixture.Owner,
            new EventSubscription(new EventSubscriptionId("bad"), "bad", new EventSinkRef("in-process", "nobody"), EventSubscriptionFilter.Everything, new ApiClientId("hooks"))));
        Assert.AreEqual(TicketFailureKind.Invalid, ((TicketFailure)invalid.Value!).Kind);
        var unknownSink = await fixture.InScopeAsync(provider => service(provider).CreateAsync(fixture.Owner,
            new EventSubscription(new EventSubscriptionId("bad"), "bad", new EventSinkRef("carrier-pigeon", "x"), EventSubscriptionFilter.Everything, new ApiClientId("hooks"))));
        Assert.AreEqual(TicketFailureKind.Invalid, ((TicketFailure)unknownSink.Value!).Kind);
        Assert.AreEqual(TicketFailureKind.Rejected, ((TicketFailure)(await fixture.SubscribeAsync("s1")).Value!).Kind);

        Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)(await fixture.InScopeAsync(provider => service(provider).ListAsync(fixture.Agent))).Value!).Kind);
        Assert.AreEqual(1, EventsFixture.Value(await fixture.InScopeAsync(provider => service(provider).ListAsync(fixture.Manager))).Count);
        Assert.AreEqual(TicketFailureKind.NotFound, Assert.IsInstanceOfType<TicketFailure>((await fixture.InScopeAsync(provider => service(provider).GetAsync(fixture.Manager, new EventSubscriptionId("zz")))).Value).Kind);

        var disabled = new EventSubscription(new EventSubscriptionId("s1"), "s1", new EventSinkRef("in-process", "recorder"), EventSubscriptionFilter.Everything,
            new ApiClientId("hooks"), enabled: false);
        Assert.AreEqual(TicketFailureKind.Conflict, Assert.IsInstanceOfType<TicketFailure>((await fixture.InScopeAsync(provider => service(provider).UpdateAsync(fixture.Manager, disabled, 5))).Value).Kind);
        Assert.AreEqual(2, EventsFixture.Value(await fixture.InScopeAsync(provider => service(provider).UpdateAsync(fixture.Manager, disabled, 1))).Subscription.Revision);
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        Assert.AreEqual(0, (await fixture.DeliverAsync()).Delivered, "Disabled subscriptions are not delivered.");
        Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)(await fixture.InScopeAsync(provider => service(provider).DeleteAsync(fixture.Agent, disabled.Id, 2))).Value!).Kind);
        Assert.IsInstanceOfType<TicketValue<EventSubscriptionId>>((await fixture.InScopeAsync(provider => service(provider).DeleteAsync(fixture.Manager, disabled.Id, 2))).Value);
    }

    [TestMethod]
    public async Task RetentionPrunesEventsAndDeadLetters()
    {
        using var fixture = await WithConversationAsync();
        fixture.Clock.Advance(TimeSpan.FromDays(31));
        var result = await fixture.InScopeAsync(provider => provider.GetRequiredService<EventRetentionWorker>().RunOnceAsync());
        Assert.AreEqual(4, result.EventsPruned);
        Assert.AreEqual(new EventLogHead(null, 4), await fixture.Log.GetHeadAsync(fixture.Scope));
        EventsFixture.Value(await fixture.SubscribeAsync("late", start: new EventStartAfterSequence(0)));
        await fixture.StartAsync("c2");
        await fixture.DispatchAsync();
        await fixture.DeliverAllAsync();
        CollectionAssert.AreEqual(new long[] { 5, 6 }, fixture.Subscriber.Events.Select(item => item.Event.Sequence).ToArray());
    }
}
