using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.Events.Tests;

[TestClass]
public sealed class ProjectionTests
{
    private static readonly string[] LifecycleTypes =
    [
        IntegrationEventTypes.ConversationCreated,
        IntegrationEventTypes.ConversationMessageReceived,
        IntegrationEventTypes.ConversationAssigned,
        IntegrationEventTypes.ConversationTeamChanged,
        IntegrationEventTypes.ConversationMessageSent,
        IntegrationEventTypes.ConversationNoteAdded,
        IntegrationEventTypes.ConversationPriorityChanged,
        IntegrationEventTypes.ConversationLabelsChanged,
        IntegrationEventTypes.ConversationStatusChanged,
        IntegrationEventTypes.ConversationResolved,
        IntegrationEventTypes.ConversationMessageReceived,
        IntegrationEventTypes.ConversationStatusChanged,
        IntegrationEventTypes.ConversationReopened,
        IntegrationEventTypes.ChannelDeliveryStatus,
        IntegrationEventTypes.ConversationDeleted,
    ];

    private static async Task LifecycleAsync(EventsFixture fixture, string id = "c1")
    {
        await fixture.StartAsync(id, externalId: "provider-" + id);
        await fixture.AssignAsync(id, "agent");
        await fixture.SetTeamAsync(id, "billing");
        await fixture.ReplyAsync(id);
        await fixture.NoteAsync(id);
        await fixture.CommandAsync(service => service.SetPriorityAsync(fixture.Owner, new ConversationId(id), ConversationPriority.Urgent));
        await fixture.CommandAsync(service => service.UpdateLabelsAsync(fixture.Owner, new ConversationId(id), [new LabelId("vip")], []));
        await fixture.StatusAsync(id, ConversationStatus.Resolved);
        await fixture.IncomingAsync(id);
    }

    [TestMethod]
    public async Task ConversationLifecycleProjectsOnceInTimelineOrderWithoutBodies()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await LifecycleAsync(fixture);
        var reply = (await fixture.Get<IConversationStore>().ReadTimelineAsync(fixture.Scope, new ConversationId("c1"), 0, 50, true))
            .Select(item => item.Event.Value).OfType<MessageEvent>().Single(message => message.Direction == MessageDirection.Outgoing);
        await fixture.CommandAsync(service => service.UpdateDeliveryStatusAsync(fixture.Channel,
            DeliveryStatusUpdate.ByMessage(new ConversationId("c1"), reply.Id, MessageDeliveryState.Delivered)));
        await fixture.DispatchAsync();
        var stored = (await fixture.Get<IConversationStore>().GetAsync(fixture.Scope, new ConversationId("c1")))!;
        await fixture.CommandAsync(service => service.DeleteAsync(fixture.Owner, new ConversationId("c1"), stored.Revision));
        await fixture.DispatchAsync();

        var events = await fixture.EventsAsync();
        CollectionAssert.AreEqual(LifecycleTypes, events.Select(item => item.Event.Type).ToArray(), string.Join(",", events.Select(item => item.Event.Type)));
        CollectionAssert.AreEqual(Enumerable.Range(1, events.Count).Select(index => (long)index).ToArray(), events.Select(item => item.Sequence).ToArray());
        var created = (ConversationCreatedData)events[0].Event.Data.Value!;
        Assert.AreEqual(("open", "none", (UserId?)null, (TeamId?)null), (created.Status, created.Priority, created.Assignee, created.Team), "Initial state, not the snapshot.");
        var received = (ConversationMessageReceivedData)events[1].Event.Data.Value!;
        Assert.AreEqual(new ContactId("ada"), received.Contact);
        Assert.AreEqual("api-client", events[1].Event.Actor!.Kind);
        Assert.AreEqual("delivered", ((ChannelDeliveryStatusData)events[13].Event.Data.Value!).State);
        Assert.AreEqual(new TeamId("billing"), events[5].Resource.Team);

        foreach (var item in events)
        {
            var json = IntegrationEventCodec.EncodeData(item.Event.Data);
            foreach (var secret in new[] { "Help please", "4111", "Refund issued", "VIP", "Still broken", "provider-c1" })
                Assert.IsFalse(json.Contains(secret, StringComparison.Ordinal), $"{item.Event.Type} leaked content.");
        }

        // Redelivered changes and replays append nothing.
        var change = new ConversationChange(Guid.NewGuid(), fixture.Scope, new ConversationId("c1"), new InboxId("email"), 1, ConversationChangeKind.Deleted, [], null, null,
            EventsFixture.Start);
        await fixture.InScopeAsync(async provider =>
        {
            var projector = provider.GetRequiredService<EventProjector>();
            await projector.HandleAsync(new ConversationOutboxEntry(1, change));
            await projector.HandleAsync(new ConversationOutboxEntry(1, change));
            return true;
        });
        Assert.AreEqual(events.Count + 1, (await fixture.EventsAsync()).Count);
    }

    [TestMethod]
    public async Task RedeliveredOutboxEntriesAreIdempotent()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.AssignAsync("c1", "agent");
        var outbox = fixture.Get<IConversationOutbox>();
        for (var round = 0; round < 10; round++)
        {
            var claims = await outbox.ClaimAsync(EventsOptions.Consumer, 10, TimeSpan.FromMinutes(1));
            if (claims.Count == 0)
                break;
            await fixture.InScopeAsync(async provider =>
            {
                var projector = provider.GetRequiredService<EventProjector>();
                foreach (var claim in claims)
                {
                    await projector.HandleAsync(claim.Entry);
                    await projector.HandleAsync(claim.Entry);
                    Assert.IsTrue(await outbox.AcknowledgeAsync(EventsOptions.Consumer, claim.Entry.Sequence, claim.Fence));
                }
                return true;
            });
        }
        Assert.AreEqual(3, (await fixture.EventsAsync()).Count);
    }

    [TestMethod]
    public async Task EventsOfInterleavedConversationsKeepPerConversationOrder()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("a");
        await fixture.StartAsync("b");
        await fixture.AssignAsync("a", "agent");
        await fixture.ReplyAsync("b");
        await fixture.StatusAsync("a", ConversationStatus.Pending);
        await fixture.AssignAsync("b", "agent");
        await fixture.DispatchAsync();
        foreach (var id in new[] { "a", "b" })
        {
            var mine = (await fixture.EventsAsync()).Where(item => item.Resource.Conversation == new ConversationId(id)).ToList();
            Assert.AreEqual(4, mine.Count);
            Assert.AreEqual(IntegrationEventTypes.ConversationCreated, mine[0].Event.Type);
            CollectionAssert.AreEqual(mine.Select(item => item.Event.Time).Order().ToArray(), mine.Select(item => item.Event.Time).ToArray());
        }
    }

    [TestMethod]
    public async Task SlaEventsAreProjectedOnlyFromTheSlaWorker()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1");
        var breached = new SlaBreachedEvent("gold:1", new SlaPolicyId("gold"), SlaMetric.FirstResponse, 1, TimeSpan.FromMinutes(30), EventsFixture.Start.AddMinutes(30), []);
        var met = new SlaMetEvent("gold:1", new SlaPolicyId("gold"), SlaMetric.Resolution, 1, TimeSpan.FromHours(8), TimeSpan.FromHours(2), false, EventsFixture.Start.AddHours(2));
        await fixture.SlaEventAsync("c1", fixture.Forger, breached);
        await fixture.SlaEventAsync("c1", fixture.SlaWorker, breached);
        await fixture.SlaEventAsync("c1", fixture.Forger, met);
        await fixture.SlaEventAsync("c1", fixture.SlaWorker, met);
        await fixture.DispatchAsync();
        var sla = (await fixture.EventsAsync()).Where(item => item.Event.Type.StartsWith("ticketflow.sla.", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEqual(new[] { IntegrationEventTypes.SlaBreached, IntegrationEventTypes.SlaMet }, sla.Select(item => item.Event.Type).ToArray());
        Assert.IsTrue(sla.All(item => item.Event.Actor == new EventActor("api-client", "sla-worker")));
        var data = (SlaBreachedData)sla[0].Event.Data.Value!;
        Assert.AreEqual(("gold", "first-response", TimeSpan.FromMinutes(30)), (data.Policy, data.Metric, data.Target));
    }

    [TestMethod]
    public async Task ContactFollowerProjectsTheChangeLogWithACheckpoint()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.CreateContactAsync("dup", "dup@example.com");
        var merge = await fixture.InScopeAsync(async provider =>
        {
            var contacts = provider.GetRequiredService<IContactService>();
            var target = ((TicketValue<ContactView>)(await contacts.GetAsync(fixture.Owner, new ContactId("ada"))).Value!).Value;
            var source = ((TicketValue<ContactView>)(await contacts.GetAsync(fixture.Owner, new ContactId("dup"))).Value!).Value;
            var merged = (TicketValue<ContactMergeOutcome>)(await contacts.MergeAsync(fixture.Owner,
                new ContactMergeRequest(target.Id, target.Revision, source.Id, source.Revision, justification: "duplicate"))).Value!;
            await contacts.UnmergeAsync(fixture.Owner, merged.Value.MergeId);
            return merged.Value.MergeId;
        });
        var consumed = await fixture.InScopeAsync(provider => provider.GetRequiredService<EventContactFollower>().CatchUpAsync(fixture.Scope, 2));
        Assert.AreEqual(2, consumed);
        while (await fixture.InScopeAsync(provider => provider.GetRequiredService<EventContactFollower>().CatchUpAsync(fixture.Scope)) > 0)
        {
        }
        var events = await fixture.EventsAsync();
        var types = events.Select(item => item.Event.Type).ToList();
        Assert.AreEqual(2, types.Count(type => string.Equals(type, IntegrationEventTypes.ContactCreated, StringComparison.Ordinal)));
        var merged = events.Select(item => item.Event.Data.Value).OfType<ContactMergedData>().Single();
        Assert.AreEqual((new ContactId("dup"), new ContactId("ada"), merge), (merged.Contact, merged.Target, merged.Merge));
        Assert.AreEqual(merge, events.Select(item => item.Event.Data.Value).OfType<ContactUnmergedData>().Single().Merge);
        Assert.IsTrue(events.All(item => item.Resource.Contact is not null && item.Resource.Conversation is null));

        // A lost checkpoint replays idempotently.
        Assert.IsTrue(await fixture.Log.SetCheckpointAsync(fixture.Scope, EventContactFollower.CheckpointName, 0,
            await fixture.Log.GetCheckpointAsync(fixture.Scope, EventContactFollower.CheckpointName)));
        await fixture.InScopeAsync(provider => provider.GetRequiredService<EventContactFollower>().CatchUpAsync(fixture.Scope, 500));
        Assert.AreEqual(events.Count, (await fixture.EventsAsync()).Count);
    }

    [TestMethod]
    public async Task SurveyFollowerProjectsResponsesOnceAcrossOverlappingWindows()
    {
        using var fixture = await EventsFixture.CreateAsync();
        Assert.AreEqual(0, await fixture.InScopeAsync(provider => provider.GetRequiredService<EventSurveyFollower>().CatchUpAsync(fixture.Scope)), "The first run sets the start.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Surveys.Add(fixture.Scope, "c1", 5, "Great help from Ann", fixture.Clock.GetUtcNow().AddMinutes(-1), team: "billing");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, await fixture.InScopeAsync(provider => provider.GetRequiredService<EventSurveyFollower>().CatchUpAsync(fixture.Scope)));
        // A late-committed response inside the overlap is still found; the first is not duplicated.
        fixture.Surveys.Add(fixture.Scope, "c2", 2, null, fixture.Clock.GetUtcNow().AddMinutes(-3));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, await fixture.InScopeAsync(provider => provider.GetRequiredService<EventSurveyFollower>().CatchUpAsync(fixture.Scope)));
        var events = await fixture.EventsAsync();
        Assert.AreEqual(2, events.Count);
        var first = (SurveyRespondedData)events[0].Event.Data.Value!;
        Assert.AreEqual(("csat", 5, new TeamId("billing")), (first.Kind, first.Score, first.Team!.Value));
        Assert.IsFalse(IntegrationEventCodec.EncodeData(events[0].Event.Data).Contains("Great help", StringComparison.Ordinal));
        Assert.AreEqual(new TeamId("billing"), events[0].Resource.Team);
    }

    [TestMethod]
    public async Task RoutingObserverMapsAssignmentsOverflowsAndWaits()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1", team: "billing");
        var at = fixture.Clock.GetUtcNow();
        await fixture.InScopeAsync(async provider =>
        {
            var observer = provider.GetServices<IRoutingEventObserver>().OfType<EventRoutingObserver>().Single();
            await observer.OnRoutingEventAsync(new ConversationRouted(fixture.Scope, new ConversationId("c1"), new UserId("agent"), 2, at));
            await observer.OnRoutingEventAsync(new ConversationRouted(fixture.Scope, new ConversationId("c1"), new UserId("agent"), 2, at));
            await observer.OnRoutingEventAsync(new ConversationQueued(fixture.Scope, new ConversationId("c1"), new InboxId("email"), RoutingQueueReason.NoCapacity, at));
            await observer.OnRoutingEventAsync(new ConversationOverflowed(fixture.Scope, new ConversationId("c1"), new OverflowToTeam(new TeamId("support")), at));
            await observer.OnRoutingEventAsync(new QueueWaitExceeded(fixture.Scope, new ConversationId("c1"), new InboxId("email"), at));
            await observer.OnRoutingEventAsync(new AgentStatusChanged(fixture.Scope, new UserId("agent"), AgentStatus.Online, at));
            await observer.OnRoutingEventAsync(new ConversationRouted(fixture.Scope, new ConversationId("missing"), new UserId("agent"), 2, at));
            return true;
        });
        var events = await fixture.EventsAsync();
        CollectionAssert.AreEqual(new[] { IntegrationEventTypes.RoutingAssigned, IntegrationEventTypes.RoutingQueued, IntegrationEventTypes.RoutingOverflowed, IntegrationEventTypes.RoutingQueueWaitExceeded },
            events.Select(item => item.Event.Type).ToArray());
        Assert.AreEqual(new TeamId("billing"), events[0].Resource.Team);
        Assert.AreEqual("no-capacity", ((RoutingQueuedData)events[1].Event.Data.Value!).Reason);
        Assert.AreEqual(new TeamId("support"), ((RoutingOverflowedData)events[2].Event.Data.Value!).TargetTeam);
    }

    [TestMethod]
    public async Task BatchOutboxProjectionAppendsMultipleChangesInOneRun()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("b1");
        await fixture.StartAsync("b2");
        await fixture.StartAsync("b3");
        var result = await fixture.InScopeAsync(provider => provider.GetRequiredService<ConversationOutboxDispatcher>().RunOnceAsync());
        Assert.AreEqual(3, result.Handled);
        var events = await fixture.EventsAsync();
        Assert.AreEqual(3, events.Count(item => string.Equals(item.Event.Type, IntegrationEventTypes.ConversationCreated, StringComparison.Ordinal)));
    }
}
