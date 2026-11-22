using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.Reporting.Tests;

[TestClass]
public sealed class ProjectorTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static string Describe(ReportFact fact) =>
        $"{fact.Metric}:{fact.Dimensions.Agent?.Value ?? "-"}:{fact.Dimensions.Team?.Value ?? "-"}{(fact.Duration is { } duration ? ":" + duration.TotalMinutes : "")}";

    private static async Task LifecycleAsync(ReportingFixture fixture)
    {
        await fixture.StartAsync("c1", team: "billing");                       // 09:00 NY, initial incoming message
        await fixture.AssignAsync("c1", "ann", 5 * Minute);
        await fixture.ReplyAsync("c1", fixture.Ann, 25 * Minute);              // 09:30: first response after 30 minutes
        await fixture.IncomingAsync("c1", 30 * Minute);                        // 10:00
        await fixture.ReplyAsync("c1", fixture.Bob, 60 * Minute);              // 11:00: response by bob (not the assignee)
        await fixture.AssignAsync("c1", "bob", Minute);                        // handoff ann -> bob
        await fixture.SetTeamAsync("c1", "support", Minute);                   // team transfer
        await fixture.StatusAsync("c1", ConversationStatus.Resolved, 118 * Minute);   // 13:00: resolved after 4 hours
        await fixture.IncomingAsync("c1", TimeSpan.FromDays(1));               // reopens
        await fixture.StatusAsync("c1", ConversationStatus.Resolved, 30 * Minute);
    }

    private static readonly string[] LifecycleFacts =
    [
        "ConversationCreated:-:billing",
        "MessageReceived:-:billing",
        "Assignment:ann:billing",
        "MessageSent:ann:billing",
        "FirstResponseTime:ann:billing:30",
        "FirstResponseBusinessTime:ann:billing:30",
        "ResponseTime:ann:billing:30",
        "ResponseBusinessTime:ann:billing:30",
        "MessageReceived:ann:billing",
        "MessageSent:bob:billing",
        "ResponseTime:bob:billing:60",
        "ResponseBusinessTime:bob:billing:60",
        "Assignment:bob:billing",
        "Handoff:bob:billing",
        "TeamTransfer:bob:support",
        "ConversationResolved:bob:support",
        "ResolutionTime:bob:support:240",
        "ResolutionBusinessTime:bob:support:240",
        "MessageReceived:bob:support",
        "ConversationReopened:bob:support",
        "ConversationResolved:bob:support",
    ];

    [TestMethod]
    public async Task ConversationLifecycleProducesFactsOncePerSourceEvent()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await LifecycleAsync(fixture);
        var dispatched = await fixture.DispatchAsync();
        Assert.AreEqual(0, dispatched.Retried + dispatched.DeadLettered);
        var facts = await fixture.FactsAsync();
        CollectionAssert.AreEquivalent(LifecycleFacts, facts.Select(Describe).ToArray());
        Assert.IsTrue(facts.All(fact => fact.Dimensions.Inbox == new InboxId("email") && fact.Dimensions.Channel == "email"));
        Assert.AreEqual(ReportingFixture.Monday, facts.Single(fact => fact.Metric == ReportMetric.ConversationCreated).OccurredAt);
        var state = (await fixture.Store.GetConversationAsync(fixture.Scope, new ConversationId("c1")))!.State;
        Assert.AreEqual((ConversationStatusKind.Resolved, 2, (DateTimeOffset?)null), (state.Status, state.Resolutions, state.WaitingSince));

        // Replays, redeliveries and direct syncs add nothing.
        await fixture.SyncAsync("c1");
        await fixture.SyncAsync("c1");
        var change = new ConversationChange(Guid.NewGuid(), fixture.Scope, new ConversationId("c1"), new InboxId("email"), 1, ConversationChangeKind.Started, [], null, null,
            ReportingFixture.Monday);
        await fixture.InScopeAsync(async services =>
        {
            await services.GetRequiredService<ReportingProjector>().HandleAsync(new ConversationOutboxEntry(1, change));
            return true;
        });
        Assert.HasCount(LifecycleFacts.Length, await fixture.FactsAsync());
        var totals = await fixture.Store.AggregateAsync(fixture.Scope, StoreContracts.ReportingStoreContract.Whole(ReportingFixture.Monday.AddDays(-1), ReportingFixture.Monday.AddDays(3)),
            AllowAllFilter.Instance);
        Assert.AreEqual(LifecycleFacts.Length, totals.Rows.Sum(row => row.Count));
    }

    [TestMethod]
    public async Task ALateConsumerBackfillsTheSameFactsIntoPastCells()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await LifecycleAsync(fixture);
        fixture.Clock.Advance(TimeSpan.FromDays(30));
        await fixture.SyncAsync("c1");   // first sight of the conversation, a month later
        var facts = await fixture.FactsAsync();
        CollectionAssert.AreEquivalent(LifecycleFacts, facts.Select(Describe).ToArray());
        Assert.IsTrue(facts.All(fact => fact.OccurredAt < ReportingFixture.Monday.AddDays(2)), "facts keep their event instants");
    }

    [TestMethod]
    public async Task LongTimelinesAreAdoptedFromTheSnapshot()
    {
        using var fixture = await ReportingFixture.CreateAsync(options: options => options.MaximumReplayEvents = 3);
        await LifecycleAsync(fixture);
        await fixture.SyncAsync("c1");
        var facts = await fixture.FactsAsync();
        CollectionAssert.AreEqual(new[] { "ConversationCreated:bob:support" }, facts.Select(Describe).ToArray());
        await fixture.IncomingAsync("c1", Minute);
        await fixture.SyncAsync("c1");
        Assert.AreEqual(1, (await fixture.FactsAsync()).Count(fact => fact.Metric == ReportMetric.ConversationReopened));
    }

    [TestMethod]
    public async Task BusinessTimeUsesTheInboxCalendarAndCalendarTimeWithoutOne()
    {
        var friday = new DateTimeOffset(2026, 3, 6, 21, 0, 0, TimeSpan.Zero);   // 16:00 New York
        using var fixture = await ReportingFixture.CreateAsync(friday);
        await fixture.StartAsync("email-1");
        await fixture.StartAsync("chat-1", inbox: "chat");
        await fixture.ReplyAsync("email-1", fixture.Ann, TimeSpan.FromHours(65));  // Monday 10:00 New York (after the DST switch: 14:00 UTC)
        await fixture.ReplyAsync("chat-1", fixture.Ann, TimeSpan.Zero);
        await fixture.DispatchAsync();
        var facts = await fixture.FactsAsync();
        TimeSpan Value(string conversation, ReportMetric metric) => facts.Single(fact => fact.Conversation.Value == conversation && fact.Metric == metric).Duration!.Value;
        Assert.AreEqual(TimeSpan.FromHours(65), Value("email-1", ReportMetric.FirstResponseTime));
        Assert.AreEqual(TimeSpan.FromHours(2), Value("email-1", ReportMetric.FirstResponseBusinessTime), "Friday 16-17 plus Monday 09-10");
        Assert.AreEqual(TimeSpan.FromHours(65), Value("chat-1", ReportMetric.FirstResponseBusinessTime), "no calendar: calendar time");
        Assert.AreEqual("live-chat", facts.First(fact => fact.Conversation.Value == "chat-1").Dimensions.Channel);
    }

    [TestMethod]
    public async Task AutomatedRepliesDoNotAnswerTheContact()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await fixture.StartAsync("c1", assignee: "ann");
        await fixture.ReplyAsync("c1", fixture.Bot, Minute);
        await fixture.ReplyAsync("c1", fixture.Ann, 9 * Minute);
        await fixture.DispatchAsync();
        var facts = await fixture.FactsAsync();
        CollectionAssert.AreEquivalent(new[]
        {
            "ConversationCreated:ann:-", "MessageReceived:ann:-", "MessageSent:-:-", "MessageSent:ann:-", "FirstResponseTime:ann:-:10",
            "FirstResponseBusinessTime:ann:-:10", "ResponseTime:ann:-:10", "ResponseBusinessTime:ann:-:10",
        }, facts.Select(Describe).ToArray());
    }

    [TestMethod]
    public async Task OnlyAuthenticSlaEventsCountAtTheirOwnInstants()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await fixture.StartAsync("c1", assignee: "ann");
        var policy = new SlaPolicyId("standard");
        var breachedAt = ReportingFixture.Monday.AddHours(4);
        await fixture.SlaEventAsync("c1", fixture.SlaWorker, new SlaBreachedEvent("k1", policy, SlaMetric.FirstResponse, 1, TimeSpan.FromHours(4), breachedAt, []), TimeSpan.FromDays(2));
        await fixture.SlaEventAsync("c1", fixture.Forger, new SlaMetEvent("k2", policy, SlaMetric.Resolution, 1, TimeSpan.FromDays(1), TimeSpan.FromHours(1), false, breachedAt), Minute);
        await fixture.SlaEventAsync("c1", fixture.SlaWorker, new SlaMetEvent("k3", policy, SlaMetric.Resolution, 1, TimeSpan.FromDays(1), TimeSpan.FromHours(1), false, breachedAt.AddHours(1)), Minute);
        await fixture.SlaEventAsync("c1", fixture.SlaWorker, new SlaMetEvent("k4", policy, SlaMetric.FirstResponse, 1, TimeSpan.FromHours(4), TimeSpan.FromHours(5), true, breachedAt.AddHours(1)), Minute);
        await fixture.DispatchAsync();
        var sla = (await fixture.FactsAsync()).Where(fact => fact.Metric is ReportMetric.SlaAchieved or ReportMetric.SlaBreached).ToList();
        CollectionAssert.AreEqual(new[] { $"SlaBreached:{breachedAt:O}", $"SlaAchieved:{breachedAt.AddHours(1):O}" },
            sla.Select(fact => $"{fact.Metric}:{fact.OccurredAt:O}").ToArray());
        Assert.IsTrue(sla.All(fact => fact.Dimensions.Agent == new UserId("ann")));
    }

    [TestMethod]
    public async Task DeletedConversationsDropTheirStateButKeepFacts()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        var stored = await fixture.InScopeAsync(services => services.GetRequiredService<IConversationStore>().GetAsync(fixture.Scope, new ConversationId("c1")));
        await fixture.CommandAsync(service => service.DeleteAsync(fixture.Owner, new ConversationId("c1"), stored!.Revision));
        await fixture.DispatchAsync();
        Assert.IsNull(await fixture.Store.GetConversationAsync(fixture.Scope, new ConversationId("c1")));
        Assert.HasCount(2, await fixture.FactsAsync());
    }

    [TestMethod]
    public async Task InitialDimensionsUndoLaterChanges()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await fixture.StartAsync("c1", team: "billing", assignee: "ann", labels: [new LabelId("vip")]);
        await fixture.CommandAsync(service => service.UpdateLabelsAsync(fixture.Owner, new ConversationId("c1"), [new LabelId("refund")], [new LabelId("vip")]), Minute);
        await fixture.CommandAsync(service => service.SetPriorityAsync(fixture.Owner, new ConversationId("c1"), ConversationPriority.Urgent), Minute);
        await fixture.AssignAsync("c1", null, Minute);
        await fixture.IncomingAsync("c1", Minute);
        await fixture.SyncAsync("c1");
        var facts = await fixture.FactsAsync();
        var created = facts.Single(fact => fact.Metric == ReportMetric.ConversationCreated).Dimensions;
        Assert.AreEqual((new UserId("ann"), ConversationPriority.None), (created.Agent!.Value, created.Priority));
        CollectionAssert.AreEqual(new[] { new LabelId("vip") }, created.Labels.ToArray());
        var last = facts.Where(fact => fact.Metric == ReportMetric.MessageReceived).MaxBy(fact => fact.OccurredAt)!.Dimensions;
        Assert.AreEqual((null as UserId?, ConversationPriority.Urgent), (last.Agent, last.Priority));
        CollectionAssert.AreEqual(new[] { new LabelId("refund") }, last.Labels.ToArray());
        Assert.AreEqual(0, facts.Count(fact => fact.Metric == ReportMetric.Assignment), "neither the initial assignment nor an unassignment is an assignment event");
    }
}
