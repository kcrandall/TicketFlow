using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;
using static TicketFlow.Routing.Tests.RoutingFixture;

namespace TicketFlow.Routing.Tests;

[TestClass]
public sealed class EngineTests
{
    private static readonly RoutingPolicyId Main = new("main");

    [TestMethod]
    public async Task RoundRobinAssignsFromTheOutboxInRotation()
    {
        using var fixture = await CreateAsync();
        foreach (var agent in new[] { "ann", "bob", "cat" })
            await fixture.OnlineAsync(agent);
        for (var index = 0; index < 6; index++)
            await fixture.StartAsync($"c{index}");
        await fixture.DrainAsync();
        var assignees = new List<string>();
        for (var index = 0; index < 6; index++)
            assignees.Add((await fixture.AssigneeAsync($"c{index}"))!.Value.Value);
        CollectionAssert.AreEqual(new[] { "ann", "bob", "cat", "ann", "bob", "cat" }, assignees);
        Assert.AreEqual(1, (await fixture.AssignmentsAsync("c0")).Count);
        Assert.AreEqual(6, fixture.Events.Count(item => item.Value is ConversationRouted));
        Assert.AreEqual(2, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId("ann")))[new ChannelKind("email")]);
        Assert.AreEqual(0, Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Count);
        // Replaying the changes is idempotent.
        Assert.AreEqual("settled:Assigned", Describe(await fixture.Engine.RouteAsync(fixture.Scope, new ConversationId("c0"))));
        Assert.AreEqual(1, (await fixture.AssignmentsAsync("c0")).Count);
    }

    [TestMethod]
    public async Task CapacityIsRespectedAndFreedCapacityDrainsTheQueue()
    {
        using var fixture = await CreateAsync();
        await fixture.OnlineAsync("ann", capacity: 1);
        await fixture.OnlineAsync("bob", capacity: 1);
        await fixture.StartAsync("c1");
        await fixture.StartAsync("c2");
        await fixture.StartAsync("c3");
        await fixture.DrainAsync();
        Assert.IsNull(await fixture.AssigneeAsync("c3"));
        Assert.AreEqual("queued:NoCapacity", Describe(await fixture.Engine.RouteAsync(fixture.Scope, new ConversationId("c3"))));
        var waiting = Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery()));
        CollectionAssert.AreEqual(new[] { "c3" }, waiting.Items.Select(item => item.Conversation.Value).ToArray());

        var owner = (await fixture.AssigneeAsync("c1"))!.Value;
        Applied(await fixture.Conversations.ChangeStatusAsync(fixture.Owner, new ConversationId("c1"), ConversationStatus.Resolved));
        await fixture.DrainAsync();
        var result = await fixture.Maintenance.RunOnceAsync();
        Assert.AreEqual(1, result.Routed);
        Assert.AreEqual(owner, await fixture.AssigneeAsync("c3"));
    }

    [TestMethod]
    public async Task LeastBusyIsCapacityAwarePerChannel()
    {
        using var fixture = await CreateAsync();
        var chat = new ChannelKind("live-chat");
        await fixture.OnlineAsync("ann", capacity: 10, capacities: [new(chat, 2)]);
        await fixture.OnlineAsync("bob", capacity: 10, capacities: [new(chat, 6)]);
        for (var index = 0; index < 6; index++)
            await fixture.StartAsync($"chat-{index}", inbox: "chat");
        await fixture.DrainAsync();
        Assert.AreEqual(2, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId("ann")))[chat]);
        Assert.AreEqual(4, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId("bob")))[chat]);
    }

    [TestMethod]
    public async Task SkillsAreDerivedFromLabelsAndAttributes()
    {
        using var fixture = await CreateAsync();
        await fixture.SetPolicyAsync(new RoutingPolicy(Main, "Skills", new SkillsBasedStrategy([
            new SkillRule(new LabelSkillMatch(new LabelId("vip")), "billing", 3),
            new SkillRule(new LabelSkillMatch(new LabelId("refund")), "billing", 5)])));
        await fixture.OnlineAsync("ann", skills: [new AgentSkill("billing", 3)]);
        await fixture.OnlineAsync("bob", skills: [new AgentSkill("billing", 5)]);
        await fixture.OnlineAsync("cat");
        await fixture.StartAsync("vip", labels: [new LabelId("vip")]);
        await fixture.StartAsync("refund", labels: [new LabelId("vip"), new LabelId("refund")]);
        await fixture.StartAsync("plain");
        await fixture.DrainAsync();
        Assert.AreNotEqual(new UserId("cat"), await fixture.AssigneeAsync("vip"));
        Assert.AreEqual(new UserId("bob"), await fixture.AssigneeAsync("refund"));
        Assert.IsNotNull(await fixture.AssigneeAsync("plain"));

        await fixture.StartAsync("expert", labels: [new LabelId("refund")]);
        Value(await fixture.Availability.SetStatusAsync(fixture.User("bob"), new UserId("bob"), AgentStatus.Away));
        await fixture.DrainAsync();
        Assert.IsNull(await fixture.AssigneeAsync("expert"));
        Assert.AreEqual("queued:NoEligibleAgent", Describe(await fixture.Engine.RouteAsync(fixture.Scope, new ConversationId("expert"))));
    }

    [TestMethod]
    public async Task StickyOwnerPrefersThePreviousAssigneeWithinTheWindow()
    {
        using var fixture = await CreateAsync();
        await fixture.SetPolicyAsync(new RoutingPolicy(Main, "Sticky", new StickyOwnerStrategy(TimeSpan.FromHours(1), RoutingStrategy.RoundRobin)));
        foreach (var agent in new[] { "ann", "bob", "cat" })
            await fixture.OnlineAsync(agent);
        await fixture.StartAsync("first", contact: "grace");
        await fixture.DrainAsync();
        var owner = (await fixture.AssigneeAsync("first"))!.Value;
        await fixture.StartAsync("other", contact: "ada");
        await fixture.StartAsync("second", contact: "grace");
        await fixture.DrainAsync();
        Assert.AreEqual(owner, await fixture.AssigneeAsync("second"));
        Assert.AreNotEqual(owner, await fixture.AssigneeAsync("other"));

        // Outside the window the fallback rotation applies; a manual fallback only offers the sticky owner.
        foreach (var agent in new[] { "ann", "bob", "cat" })
            Value(await fixture.Availability.HeartbeatAsync(fixture.User(agent)));
        fixture.Clock.Advance(TimeSpan.FromMinutes(61));
        foreach (var agent in new[] { "ann", "bob", "cat" })
            Value(await fixture.Availability.SetStatusAsync(fixture.User(agent), new UserId(agent), AgentStatus.Online));
        await fixture.SetPolicyAsync(new RoutingPolicy(Main, "Sticky", new StickyOwnerStrategy(TimeSpan.FromHours(1), RoutingStrategy.Manual)));
        await fixture.StartAsync("late", contact: "grace");
        await fixture.DrainAsync();
        Assert.IsNull(await fixture.AssigneeAsync("late"));
        Assert.AreEqual("queued:Manual", Describe(await fixture.Engine.RouteAsync(fixture.Scope, new ConversationId("late"))));
    }

    [TestMethod]
    public async Task ManualAndMissingPoliciesQueue()
    {
        using var fixture = await CreateAsync();
        await fixture.OnlineAsync("ann");
        await fixture.StartAsync("plain", inbox: "plain");
        await fixture.DrainAsync();
        Assert.AreEqual("queued:Manual", Describe(await fixture.Engine.RouteAsync(fixture.Scope, new ConversationId("plain"))));
        await fixture.SetPolicyAsync(new RoutingPolicy(Main, "Manual", RoutingStrategy.Manual));
        await fixture.StartAsync("manual");
        await fixture.DrainAsync();
        Assert.IsNull(await fixture.AssigneeAsync("manual"));
        // A manual assignment leaves the queue and counts toward load (even beyond capacity).
        Applied(await fixture.Conversations.AssignAsync(fixture.Owner, new ConversationId("manual"), new UserId("ann")));
        await fixture.DrainAsync();
        Assert.AreEqual(1, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId("ann")))[new ChannelKind("email")]);
        CollectionAssert.AreEqual(new[] { "plain" }, Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Select(i => i.Conversation.Value).ToArray());
        // Unassigning returns it to the queue; deleting removes everything.
        Applied(await fixture.Conversations.AssignAsync(fixture.Owner, new ConversationId("manual"), null));
        await fixture.DrainAsync();
        Assert.AreEqual(2, Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Count);
        Assert.AreEqual(0, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId("ann")))!.GetValueOrDefault(new ChannelKind("email")));
        var stored = await fixture.ConversationStore.GetAsync(fixture.Scope, new ConversationId("manual"));
        Applied(await fixture.Conversations.DeleteAsync(fixture.Owner, new ConversationId("manual"), stored!.Revision));
        await fixture.DrainAsync();
        Assert.AreEqual(1, Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Count);
    }

    [TestMethod]
    public async Task EligibilityUsesAccessAndTeams()
    {
        using var fixture = await CreateAsync();
        // cat is denied replies on vip conversations; "inboxer" may only work the chat inbox (an inbox-scoped grant cannot set its own
        // workspace-level status, so the supervisor does).
        var noReply = await fixture.CustomRoleAsync("no-reply", ConversationPermissions.Reply);
        await fixture.GrantAsync(new UserId("cat"), noReply, GrantEffect.Deny, new HasLabelCondition(new LabelId("vip")));
        await fixture.GrantAsync(new UserId("inboxer"), BuiltInRoles.Agent, scope: new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("chat")));
        await fixture.OnlineAsync("cat");
        await fixture.OnlineAsync("inboxer", bySupervisor: true);
        await fixture.StartAsync("vip", labels: [new LabelId("vip")]);
        await fixture.DrainAsync();
        Assert.AreEqual("queued:NoEligibleAgent", Describe(await fixture.Engine.RouteAsync(fixture.Scope, new ConversationId("vip"))));
        await fixture.StartAsync("plain");
        await fixture.DrainAsync();
        Assert.AreEqual(new UserId("cat"), await fixture.AssigneeAsync("plain"));

        // A team on the conversation restricts the pool to members.
        await fixture.OnlineAsync("bob");
        await fixture.StartAsync("team", team: new TeamId("billing"));
        await fixture.DrainAsync();
        Assert.AreEqual(new UserId("bob"), await fixture.AssigneeAsync("team"));
    }

    [TestMethod]
    public async Task EngineWithoutGrantsLeavesConversationsQueued()
    {
        using var fixture = await CreateAsync(engineGrant: false);
        await fixture.OnlineAsync("ann");
        await fixture.StartAsync("c1");
        await fixture.DrainAsync();
        Assert.IsNull(await fixture.AssigneeAsync("c1"));
        Assert.AreEqual("queued:Refused", Describe(await fixture.Engine.RouteAsync(fixture.Scope, new ConversationId("c1"))));
        Assert.AreEqual(0, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId("ann"))).Count, "the reservation is released");
    }

    [TestMethod]
    public async Task OverflowMovesWaitingConversationsAndRaisesMaximumWait()
    {
        using var fixture = await CreateAsync();
        await fixture.SetPolicyAsync(new RoutingPolicy(Main, "Overflow", RoutingStrategy.RoundRobin, new TeamId("billing"),
            [new OverflowRule(TimeSpan.FromMinutes(2), new OverflowToTeam(new TeamId("tier2"))), new OverflowRule(TimeSpan.FromMinutes(4), new OverflowToInbox(new InboxId("chat")))],
            TimeSpan.FromMinutes(3)));
        await fixture.SetPolicyAsync(new RoutingPolicy(new RoutingPolicyId("chat"), "Chat", RoutingStrategy.Manual));
        await fixture.StartAsync("wait");
        await fixture.DrainAsync();
        Assert.AreEqual(1, (await fixture.Maintenance.RunOnceAsync()).StillQueued);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var first = await fixture.Maintenance.RunOnceAsync();
        Assert.AreEqual(1, first.Overflowed);
        Assert.AreEqual(new TeamId("tier2"), (await fixture.ConversationStore.GetAsync(fixture.Scope, new ConversationId("wait")))!.Conversation.Team);
        await fixture.DrainAsync();

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await fixture.Maintenance.RunOnceAsync();
        Assert.AreEqual(1, second.WaitExceeded);
        Assert.AreEqual(1, fixture.Events.Count(item => item.Value is QueueWaitExceeded));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, (await fixture.Maintenance.RunOnceAsync()).Overflowed);
        var entry = Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Single();
        Assert.AreEqual(new InboxId("chat"), entry.TargetInbox);
        Assert.AreEqual(new InboxId("email"), entry.Inbox);
        Assert.AreEqual(2, entry.OverflowStage);
        Assert.IsTrue(entry.WaitExceeded);
        Assert.AreEqual(2, fixture.Events.Count(item => item.Value is ConversationOverflowed));

        // A tier2 agent now gets it once the overflow target routes automatically.
        await fixture.SetPolicyAsync(new RoutingPolicy(new RoutingPolicyId("chat"), "Chat", RoutingStrategy.LeastBusy));
        await fixture.OnlineAsync("ann");
        await fixture.OnlineAsync("dee");
        Assert.AreEqual(1, (await fixture.Maintenance.RunOnceAsync()).Routed);
        Assert.AreEqual(new UserId("dee"), await fixture.AssigneeAsync("wait"));
    }

    [TestMethod]
    public async Task OfflineAgentsHaveConversationsReturnedPerPolicy()
    {
        using var fixture = await CreateAsync();
        await fixture.SetPolicyAsync(new RoutingPolicy(Main, "Main", RoutingStrategy.RoundRobin, onAgentOffline: OfflineReassignment.ReturnToQueue));
        await fixture.OnlineAsync("ann");
        await fixture.StartAsync("e1");
        await fixture.StartAsync("chat-1", inbox: "chat");
        await fixture.DrainAsync();
        Assert.AreEqual(new UserId("ann"), await fixture.AssigneeAsync("e1"));
        Assert.AreEqual(new UserId("ann"), await fixture.AssigneeAsync("chat-1"));

        await fixture.OnlineAsync("bob");
        Value(await fixture.Availability.SetStatusAsync(fixture.User("ann"), new UserId("ann"), AgentStatus.Offline));
        var result = await fixture.Maintenance.RunOnceAsync();
        Assert.AreEqual(1, result.Reassigned);
        Assert.AreEqual(new UserId("bob"), await fixture.AssigneeAsync("e1"));
        Assert.AreEqual(new UserId("ann"), await fixture.AssigneeAsync("chat-1"), "the chat policy keeps assignments");
        Assert.IsFalse((await fixture.Agents.GetAsync(fixture.Scope, new UserId("ann")))!.ReassignmentPending);

        // Presence expiry is detected by maintenance and handled the same way.
        await fixture.StartAsync("e2");
        await fixture.DrainAsync();
        await fixture.OnlineAsync("cat");
        Assert.AreEqual(new UserId("bob"), await fixture.AssigneeAsync("e2"));
        Value(await fixture.Availability.HeartbeatAsync(fixture.User("cat")));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Value(await fixture.Availability.HeartbeatAsync(fixture.User("cat")));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1.5));
        var expired = await fixture.Maintenance.RunOnceAsync();
        Assert.AreEqual(1, expired.PresenceExpired);
        Assert.AreEqual(2, expired.Reassigned);
        Assert.AreEqual(new UserId("cat"), await fixture.AssigneeAsync("e1"));
        Assert.AreEqual(new UserId("cat"), await fixture.AssigneeAsync("e2"));
    }
}
