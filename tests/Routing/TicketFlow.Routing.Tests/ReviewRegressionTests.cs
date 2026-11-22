using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;
using static TicketFlow.Routing.Tests.RoutingFixture;

namespace TicketFlow.Routing.Tests;

[TestClass]
public sealed class ReviewRegressionTests
{
    private sealed class CountingEngine : IRoutingEngine
    {
        public int Calls;

        public Task<RoutingOutcome> RouteAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<RoutingOutcome>(new RoutingSettled(RoutingSettledReason.Assigned));
        }
    }

    private static ConversationOutboxEntry Entry(ConversationChangeKind kind, params ConversationEventSummary[] events) =>
        new(1, new ConversationChange(Guid.NewGuid(), TestScopes.Create(), new ConversationId("c"), new InboxId("email"), 2, kind, events,
            kind == ConversationChangeKind.DeliveryUpdated ? new MessageId("m") : null, kind == ConversationChangeKind.DeliveryUpdated ? MessageDeliveryState.Sent : null,
            TestClock.DefaultStart));

    [TestMethod]
    public async Task OutboxHandlerSkipsChangesThatCannotAffectRouting()
    {
        var engine = new CountingEngine();
        var handler = new RoutingOutboxHandler(engine);
        await handler.HandleAsync(Entry(ConversationChangeKind.EventsAppended, new ConversationEventSummary(3, ConversationEventKind.Extension)));
        await handler.HandleAsync(Entry(ConversationChangeKind.EventsAppended, new ConversationEventSummary(3, ConversationEventKind.Note, new MessageId("n"))));
        await handler.HandleAsync(Entry(ConversationChangeKind.EventsAppended, new ConversationEventSummary(3, ConversationEventKind.Message, new MessageId("o"), MessageDirection.Outgoing)));
        await handler.HandleAsync(Entry(ConversationChangeKind.EventsAppended, new ConversationEventSummary(3, ConversationEventKind.Priority),
            new ConversationEventSummary(4, ConversationEventKind.Participant), new ConversationEventSummary(5, ConversationEventKind.System)));
        await handler.HandleAsync(Entry(ConversationChangeKind.DeliveryUpdated));
        Assert.AreEqual(0, engine.Calls, "irrelevant changes never reach the engine or any routing store");

        foreach (var kind in new[] { ConversationEventKind.Status, ConversationEventKind.Assignment, ConversationEventKind.Team, ConversationEventKind.Labels, ConversationEventKind.Attribute })
            await handler.HandleAsync(Entry(ConversationChangeKind.EventsAppended, new ConversationEventSummary(3, ConversationEventKind.Note, new MessageId("n")),
                new ConversationEventSummary(4, kind)));
        await handler.HandleAsync(Entry(ConversationChangeKind.EventsAppended, new ConversationEventSummary(3, ConversationEventKind.Message, new MessageId("i"), MessageDirection.Incoming)));
        await handler.HandleAsync(Entry(ConversationChangeKind.Started));
        await handler.HandleAsync(Entry(ConversationChangeKind.Deleted));
        Assert.AreEqual(8, engine.Calls);
    }

    [TestMethod]
    public async Task ExtensionAppendsDoNotTouchRoutingState()
    {
        using var fixture = await CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.DrainAsync();
        var before = Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Single();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var engine = new CountingEngine();
        var dispatcher = new ConversationOutboxDispatcher(fixture.ConversationStore, [new RoutingOutboxHandler(engine)],
            Microsoft.Extensions.Options.Options.Create(new ConversationOutboxDispatcherOptions()), fixture.Clock);
        Applied(await fixture.Conversations.AppendExtensionAsync(fixture.Owner, new ConversationId("c1"),
            new ExtensionEvent("sla.breached", 1, """{"x":1}""")));
        Applied(await fixture.Conversations.AddNoteAsync(fixture.Owner, new ConversationId("c1"), new NoteDraft("internal")));
        // Deliveries of one conversation are claimed in order, one per run.
        Assert.AreEqual(1, (await dispatcher.RunOnceAsync()).Handled);
        Assert.AreEqual(1, (await dispatcher.RunOnceAsync()).Handled);
        Assert.AreEqual(0, engine.Calls);
        Assert.AreEqual(before, Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Single());
    }

    [TestMethod]
    public async Task RevokedOrRemovedAgentsAreNeverChosenInTheNextRun()
    {
        using var fixture = await CreateAsync();
        await fixture.OnlineAsync("ann");
        await fixture.OnlineAsync("bob");
        await fixture.StartAsync("c1");
        Assert.AreEqual("assigned:ann", Describe(await RouteInNewRunAsync(fixture, "c1")));

        // Revoke ann's replies: a fresh run (new scope) re-plans her eligibility.
        await fixture.GrantAsync(new UserId("ann"), await fixture.CustomRoleAsync("no-reply", ConversationPermissions.Reply), GrantEffect.Deny);
        for (var index = 2; index <= 4; index++)
        {
            await fixture.StartAsync($"c{index}");
            Assert.AreEqual("assigned:bob", Describe(await RouteInNewRunAsync(fixture, $"c{index}")));
        }

        // Remove bob's profile: never chosen again; cat takes over.
        await fixture.OnlineAsync("cat");
        var bob = (await fixture.Agents.GetAsync(fixture.Scope, new UserId("bob")))!;
        Assert.IsInstanceOfType<RoutingWriteApplied<StoredAgentProfile>>((await fixture.Agents.DeleteAsync(fixture.Scope, bob.Agent, bob.Revision)).Value);
        for (var index = 5; index <= 7; index++)
        {
            await fixture.StartAsync($"c{index}");
            Assert.AreEqual("assigned:cat", Describe(await RouteInNewRunAsync(fixture, $"c{index}")));
        }
    }

    [TestMethod]
    public async Task PoolsLargerThanOnePageStayEligible()
    {
        using var fixture = await CreateAsync();
        var role = await fixture.CustomRoleAsync("replier", ConversationPermissions.Reply);
        for (var index = 0; index < 1100; index++)
        {
            var agent = new UserId($"a-{index:D4}");
            await fixture.GrantAsync(agent, role);
            Assert.IsInstanceOfType<RoutingWriteApplied<StoredAgentProfile>>((await fixture.Agents.SaveSettingsAsync(fixture.Scope, new AgentRoutingSettings(agent, 5), null)).Value);
            Assert.IsInstanceOfType<RoutingWriteApplied<StoredAgentProfile>>((await fixture.Agents.SetStatusAsync(fixture.Scope, agent, AgentStatus.Online, TimeSpan.FromHours(1))).Value);
        }
        // Team members whose identifiers sort after the first 1000 available agents are still candidates.
        await fixture.Provider.GetRequiredService<ITeamStore>().CreateAsync(fixture.Scope,
            new Team(new TeamId("late"), "Late", members: [new UserId("a-1050"), new UserId("a-1099")]));
        await fixture.StartAsync("team", team: new TeamId("late"));
        Assert.AreEqual("assigned:a-1050", Describe(await RouteInNewRunAsync(fixture, "team")));

        // Without a team, more than 1000 eligible agents are windowed after the round-robin cursor, so rotation continues past 1000.
        var seed = await fixture.Assignments.ReserveAsync(fixture.Scope, new RoutingReservationRequest(new ConversationId("seed"), 1, new InboxId("email"),
            new ContactId("ada"), new ChannelKind("email"), "email", RoutingSelection.RoundRobin, [new RoutingCandidate(new UserId("a-0999"), 5)], null, TimeSpan.FromSeconds(1)));
        Assert.IsInstanceOfType<RoutingReserved>(seed.Value);
        await fixture.StartAsync("wide");
        Assert.AreEqual("assigned:a-1000", Describe(await RouteInNewRunAsync(fixture, "wide")));
        await fixture.StartAsync("wrap");
        Assert.AreEqual("assigned:a-1001", Describe(await RouteInNewRunAsync(fixture, "wrap")));
    }

    [TestMethod]
    public async Task StaleQueueSnapshotUsesTheCurrentTeam()
    {
        using var fixture = await CreateAsync();
        await ConcurrencyScenarios.StaleQueueSnapshotAsync(fixture);
    }

    [TestMethod]
    public async Task TeamChangeBetweenReserveAndAssignIsRetried()
    {
        var interceptor = new ConcurrencyScenarios.ReservationInterceptor();
        using var fixture = await CreateAsync(stores: builder =>
        {
            builder.Services.AddSingleton<IRoutingAssignmentStore>(provider => interceptor.Wrap(provider.GetRequiredService<InMemoryRoutingStore>()));
            builder.AddInMemoryRoutingStores();
        });
        await ConcurrencyScenarios.TeamChangeBetweenReserveAndAssignAsync(fixture, interceptor);
    }

    private static async Task<RoutingOutcome> RouteInNewRunAsync(RoutingFixture fixture, string conversation)
    {
        using var scope = fixture.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRoutingEngine>().RouteAsync(fixture.Scope, new ConversationId(conversation));
    }
}
