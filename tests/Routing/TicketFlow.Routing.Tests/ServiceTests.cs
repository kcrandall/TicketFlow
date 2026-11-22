using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;
using static TicketFlow.Routing.Tests.RoutingFixture;

namespace TicketFlow.Routing.Tests;

[TestClass]
public sealed class ServiceTests
{
    [TestMethod]
    public async Task PolicyManagementIsAuthorizedAndValidated()
    {
        using var fixture = await CreateAsync();
        var policy = new RoutingPolicy(new RoutingPolicyId("vip"), "VIP", RoutingStrategy.LeastBusy, new TeamId("billing"),
            [new OverflowRule(TimeSpan.FromMinutes(5), new OverflowToInbox(new InboxId("chat")))]);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Policies.CreateAsync(fixture.User("ann"), policy)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Policies.GetAsync(fixture.Channel, policy.Id)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Policies.CreateAsync(fixture.Lead,
            new RoutingPolicy(policy.Id, "VIP", RoutingStrategy.LeastBusy, new TeamId("missing")))));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Policies.CreateAsync(fixture.Lead, new RoutingPolicy(policy.Id, "VIP", RoutingStrategy.LeastBusy,
            overflow: [new OverflowRule(TimeSpan.FromMinutes(5), new OverflowToInbox(new InboxId("missing")))]))));
        var created = Value(await fixture.Policies.CreateAsync(fixture.Lead, policy));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual("not-found", Describe(await fixture.Policies.GetAsync(fixture.User("ann"), new RoutingPolicyId("none"))));
        Assert.AreEqual(policy, Value(await fixture.Policies.GetAsync(fixture.User("ann"), policy.Id)).Policy);
        Assert.AreEqual(3, Value(await fixture.Policies.ListAsync(fixture.User("viewer"))).Count);
        Assert.AreEqual("conflict:1", Describe(await fixture.Policies.UpdateAsync(fixture.Owner, policy, 7)));
        Value(await fixture.Policies.UpdateAsync(fixture.Owner, new RoutingPolicy(policy.Id, "VIP 2", RoutingStrategy.Manual), 1));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Policies.DeleteAsync(fixture.User("bob"), policy.Id, 2)));
        Value(await fixture.Policies.DeleteAsync(fixture.Lead, policy.Id, 2));
    }

    [TestMethod]
    public async Task AvailabilityDistinguishesSelfFromOthers()
    {
        using var fixture = await CreateAsync();
        var ann = new UserId("ann");
        var settings = new AgentRoutingSettings(ann, 2, skills: [new AgentSkill("billing", 3)]);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Availability.ConfigureAsync(fixture.User("ann"), settings)));
        Assert.AreEqual(1, Value(await fixture.Availability.ConfigureAsync(fixture.Lead, settings)).Revision);
        Assert.AreEqual("conflict:1", Describe(await fixture.Availability.ConfigureAsync(fixture.Lead, settings)));
        Assert.AreEqual("not-found", Describe(await fixture.Availability.SetStatusAsync(fixture.User("bob"), new UserId("bob"), AgentStatus.Online)));

        var online = Value(await fixture.Availability.SetStatusAsync(fixture.User("ann"), ann, AgentStatus.Online));
        Assert.AreEqual(AgentStatus.Online, online.Presence.Status);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Availability.SetStatusAsync(fixture.User("bob"), ann, AgentStatus.Away)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Availability.SetStatusAsync(fixture.User("light"), new UserId("light"), AgentStatus.Online)));
        Assert.AreEqual(AgentStatus.Away, Value(await fixture.Availability.SetStatusAsync(fixture.Lead, ann, AgentStatus.Away)).Presence.Status);
        Assert.IsInstanceOfType<AgentStatusChanged>(fixture.Events[^1].Value);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var beat = Value(await fixture.Availability.HeartbeatAsync(fixture.User("ann")));
        Assert.AreEqual(fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(2), beat.Presence.ExpiresAt);
        Assert.AreEqual("refused:Rejected", Describe(await fixture.Availability.HeartbeatAsync(fixture.Channel)));

        Assert.AreEqual(settings, Value(await fixture.Availability.GetAsync(fixture.User("viewer"), ann)).Settings);
        Assert.AreEqual(1, Value(await fixture.Availability.ListAsync(fixture.User("bob"))).Count);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Availability.ListAsync(fixture.Channel)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Availability.ListAsync(fixture.Lead, limit: 0)));
    }

    [TestMethod]
    public async Task CollisionPresenceAndReplyLocks()
    {
        using var fixture = await CreateAsync();
        await fixture.StartAsync("c1");
        var conversation = new ConversationId("c1");
        var viewing = Value(await fixture.Collision.ReportActivityAsync(fixture.User("ann"), conversation, ConversationActivity.Viewing));
        Assert.AreEqual(ConversationActivity.Viewing, viewing.Activity);
        Value(await fixture.Collision.ReportActivityAsync(fixture.User("ann"), conversation, ConversationActivity.Viewing));
        Value(await fixture.Collision.ReportActivityAsync(fixture.User("light"), conversation, ConversationActivity.Composing));
        Assert.AreEqual(2, fixture.Events.Count(item => item.Value is ConversationPresenceChanged), "refreshes do not publish");
        CollectionAssert.AreEqual(new[] { "ann", "light" }, Value(await fixture.Collision.ListActivityAsync(fixture.User("bob"), conversation)).Select(p => p.User.Value).ToArray());
        Assert.AreEqual("not-found", Describe(await fixture.Collision.ReportActivityAsync(fixture.User("ann"), new ConversationId("nope"), ConversationActivity.Viewing)));
        Assert.AreEqual("refused:Rejected", Describe(await fixture.Collision.ListActivityAsync(fixture.Channel, conversation)));
        Assert.IsTrue(Value(await fixture.Collision.LeaveAsync(fixture.User("light"), conversation)));
        Assert.IsNull(((ConversationPresenceChanged)fixture.Events[^1].Value!).Activity);

        // Light agents may view but not lock (no conversation:reply).
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Collision.AcquireReplyLockAsync(fixture.User("light"), conversation)));
        var locked = Assert.IsInstanceOfType<ReplyLockAcquired>(Value(await fixture.Collision.AcquireReplyLockAsync(fixture.User("ann"), conversation)).Value);
        Assert.IsInstanceOfType<ReplyLockHeld>(Value(await fixture.Collision.AcquireReplyLockAsync(fixture.User("bob"), conversation)).Value);
        Assert.AreEqual(new UserId("ann"), Value(await fixture.Collision.GetReplyLockAsync(fixture.User("light"), conversation))!.Holder);
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Collision.AcquireReplyLockAsync(fixture.User("ann"), conversation, TimeSpan.FromHours(1))));
        Assert.IsFalse(Value(await fixture.Collision.ReleaseReplyLockAsync(fixture.User("bob"), conversation, locked.Lock.Fence)));
        Assert.IsTrue(Value(await fixture.Collision.ReleaseReplyLockAsync(fixture.User("ann"), conversation, locked.Lock.Fence)));
        Assert.IsInstanceOfType<ReplyLockAcquired>(Value(await fixture.Collision.AcquireReplyLockAsync(fixture.User("bob"), conversation)).Value);
        Assert.IsTrue(fixture.Events.Count(item => item.Value is ReplyLockChanged) >= 3);

        // Presence lapses.
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.AreEqual(0, Value(await fixture.Collision.ListActivityAsync(fixture.User("bob"), conversation)).Count);
    }

    [TestMethod]
    public async Task CollisionRespectsConversationAccess()
    {
        using var fixture = await CreateAsync();
        await fixture.GrantAsync(new UserId("scoped"), BuiltInRoles.Agent, scope: new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        await fixture.StartAsync("other");
        await fixture.StartAsync("team", team: new TeamId("billing"));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Collision.ReportActivityAsync(fixture.User("scoped"), new ConversationId("other"), ConversationActivity.Viewing)));
        Value(await fixture.Collision.ReportActivityAsync(fixture.User("scoped"), new ConversationId("team"), ConversationActivity.Viewing));
    }

    [TestMethod]
    public async Task QueueListsAreOrderedAndAccessFiltered()
    {
        using var fixture = await CreateAsync();
        await fixture.SetPolicyAsync(new RoutingPolicy(new RoutingPolicyId("main"), "Main", RoutingStrategy.Manual));
        await fixture.StartAsync("q1");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.StartAsync("q2", team: new TeamId("billing"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.StartAsync("q3", inbox: "plain");
        await fixture.DrainAsync();

        var all = Value(await fixture.Queue.ListAsync(fixture.User("ann"), new RoutingQueueQuery()));
        CollectionAssert.AreEqual(new[] { "q1", "q2", "q3" }, all.Items.Select(item => item.Conversation.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "q1", "q2" }, Value(await fixture.Queue.ListAsync(fixture.User("ann"), new RoutingQueueQuery(inbox: new InboxId("email"))))
            .Items.Select(item => item.Conversation.Value).ToArray());
        var paged = Value(await fixture.Queue.ListAsync(fixture.User("ann"), new RoutingQueueQuery(limit: 2)));
        Assert.IsNotNull(paged.Next);
        CollectionAssert.AreEqual(new[] { "q3" }, Value(await fixture.Queue.ListAsync(fixture.User("ann"), new RoutingQueueQuery(limit: 2, after: paged.Next)))
            .Items.Select(item => item.Conversation.Value).ToArray());

        await fixture.GrantAsync(new UserId("scoped"), BuiltInRoles.Agent, scope: new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        CollectionAssert.AreEqual(new[] { "q2" }, Value(await fixture.Queue.ListAsync(fixture.User("scoped"), new RoutingQueueQuery()))
            .Items.Select(item => item.Conversation.Value).ToArray());
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Queue.ListAsync(fixture.Channel, new RoutingQueueQuery())));
    }
}
