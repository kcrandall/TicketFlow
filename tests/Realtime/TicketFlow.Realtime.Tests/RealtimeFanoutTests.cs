using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Channels.Testing;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Routing;
using TicketFlow.Testing;
using static TicketFlow.Realtime.Tests.RealtimeTestSupport;

namespace TicketFlow.Realtime.Tests;

[TestClass]
public sealed class RealtimeFanoutTests
{
    [TestMethod]
    public async Task ConversationSubscriptionsNeedReadAndDeliverIdentifierOnlyChanges()
    {
        await using var host = await HostAsync();
        var conversation = await host.StartAsync();
        var topic = new ConversationTopic(host.Scope, conversation);

        var agent = host.Connect("agent-1", host.Agent);
        Assert.IsNull(await host.SubscribeAsync(agent, topic));
        Assert.IsNull(await host.SubscribeAsync(agent, topic), "Subscribing twice is idempotent.");
        var stranger = host.Connect("stranger-1", host.User("stranger"));
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(stranger, topic));
        Assert.AreEqual(RealtimeRefusal.NotFound, await host.SubscribeAsync(agent, new ConversationTopic(host.Scope, new ConversationId("missing"))));
        Assert.AreEqual(RealtimeRefusal.Invalid, await host.SubscribeAsync(agent, new ConversationTopic(TestScopes.Create(workspaceId: "other"), conversation)));
        Assert.AreEqual(RealtimeRefusal.NotConnected, ChannelHost.Is<RealtimeSubscriptionRefused>((await host.Registry().SubscribeAsync("nobody", topic)).Value).Reason);

        await host.DispatchAsync();
        agent.Clear();
        Applied(await host.Conversations.ReplyAsync(host.Agent, conversation, new OutgoingMessageDraft("Secret reply body")));
        await host.DispatchAsync();

        var change = agent.Events<ConversationChangedEvent>().Single();
        Assert.AreEqual(conversation, change.Conversation);
        Assert.AreEqual(ConversationChangeKind.EventsAppended, change.Kind);
        Assert.AreEqual(MessageDirection.Outgoing, change.Events.Single().Direction);
        Assert.IsEmpty(stranger.Received);
    }

    [TestMethod]
    public async Task NoteSummariesNeedNoteReadAndDeliveriesAreReauthorized()
    {
        await using var host = await HostAsync();
        var reader = await host.RoleAsync("reader", ConversationPermissions.Read);
        await host.GrantAsync(new UserId("reader"), reader.Role);
        var conversation = await host.StartAsync();
        var topic = new ConversationTopic(host.Scope, conversation);
        var readerConnection = host.Connect("reader-1", host.User("reader"));
        var agentConnection = host.Connect("agent-1", host.Agent);
        Assert.IsNull(await host.SubscribeAsync(readerConnection, topic));
        Assert.IsNull(await host.SubscribeAsync(agentConnection, topic));
        await host.DispatchAsync();
        readerConnection.Clear();
        agentConnection.Clear();

        Applied(await host.Conversations.AddNoteAsync(host.Agent, conversation, new NoteDraft("internal")));
        await host.DispatchAsync();
        Assert.AreEqual(ConversationEventKind.Note, agentConnection.Events<ConversationChangedEvent>().Single().Events.Single().Kind);
        Assert.IsEmpty(readerConnection.Received, "A note-only change is dropped for subscribers without note:read.");

        // A deny grant takes effect on the live subscription: the next change is not delivered.
        var deny = new AccessGrant(new GrantId("deny-reader"), AccessPartition.From(host.Scope), new UserId("reader"), reader.Role.Id,
            new WorkspaceGrantScope(host.Scope.WorkspaceId), GrantEffect.Deny);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await host.Services.GetRequiredService<IAccessGrantStore>().CreateAsync(deny)).Value);
        host.Clock.Advance(TimeSpan.FromSeconds(31));   // past DecisionContextLifetime: the cached decision context is reloaded
        Applied(await host.Conversations.ReplyAsync(host.Agent, conversation, new OutgoingMessageDraft("hello")));
        await host.DispatchAsync();
        Assert.IsEmpty(readerConnection.Received);
        Assert.HasCount(2, agentConnection.Events<ConversationChangedEvent>());
    }

    [TestMethod]
    public async Task QueueTopicsNeedInboxReadAndApplyThePlannedReadFilter()
    {
        await using var host = await HostAsync();
        var inboxReader = await host.RoleAsync("inbox-reader", AccessPermissions.InboxRead);
        await host.GrantAsync(new UserId("team-agent"), inboxReader.Role);
        await host.GrantAsync(new UserId("team-agent"), BuiltInRoles.Agent, new TeamGrantScope(host.Scope.WorkspaceId, new TeamId("team-1")));
        var queue = new InboxQueueTopic(host.Scope, new InboxId("email-inbox"));

        var teamAgent = host.Connect("team-agent-1", host.User("team-agent", "team-1"));
        Assert.IsNull(await host.SubscribeAsync(teamAgent, queue));
        var stranger = host.Connect("stranger-1", host.User("stranger"));
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(stranger, queue));

        await host.Services.GetRequiredService<TicketFlow.Workspaces.ITeamStore>().CreateAsync(host.Scope,
            new TicketFlow.Workspaces.Team(new TeamId("team-1"), "Team 1", members: [new UserId("team-agent")]));
        var conversation = await host.StartAsync();
        await host.DispatchAsync();
        Assert.IsEmpty(teamAgent.Received, "The conversation has no team, so the team-scoped grant does not cover it.");

        Applied(await host.Conversations.SetTeamAsync(host.Owner, conversation, new TeamId("team-1")));
        await host.DispatchAsync();
        Assert.AreEqual(conversation, teamAgent.Events<ConversationChangedEvent>().Single().Conversation);
    }

    [TestMethod]
    public async Task AgentTopicsAreSelfOnlyAndCarryAssignmentsAndStatus()
    {
        await using var host = await HostAsync();
        var conversation = await host.StartAsync();
        var agent = host.Connect("agent-1", host.Agent);
        Assert.IsNull(await host.SubscribeAsync(agent, new AgentTopic(host.Scope, new UserId("agent"))));
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(agent, new AgentTopic(host.Scope, new UserId("owner"))));
        var client = host.Connect("client-1", host.Worker);
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(client, new AgentTopic(host.Scope, new UserId("agent"))));
        await host.DispatchAsync();

        Applied(await host.Conversations.AssignAsync(host.Owner, conversation, new UserId("agent")));
        await host.DispatchAsync();
        Assert.AreEqual(conversation, agent.Events<ConversationChangedEvent>().Single().Conversation);

        var observer = host.Services.GetServices<IRoutingEventObserver>().OfType<RealtimeRoutingEventObserver>().Single();
        await observer.OnRoutingEventAsync(new AgentStatusChanged(host.Scope, new UserId("agent"), AgentStatus.Online, host.Clock.GetUtcNow()));
        await observer.OnRoutingEventAsync(new AgentStatusChanged(host.Scope, new UserId("owner"), AgentStatus.Away, host.Clock.GetUtcNow()));
        await observer.OnRoutingEventAsync(new ConversationRouted(host.Scope, conversation, new UserId("agent"), 3, host.Clock.GetUtcNow()));
        Assert.AreEqual(RealtimeAgentStatus.Online, agent.Events<AgentStatusChangedEvent>().Single().Status);
        Assert.AreEqual(conversation, agent.Events<ConversationAssignedEvent>().Single().Conversation);
    }

    [TestMethod]
    public async Task PresenceAndLocksReachOnlyReaders()
    {
        await using var host = await HostAsync();
        var conversation = await host.StartAsync();
        var topic = new ConversationTopic(host.Scope, conversation);
        var viewer = host.Connect("viewer-1", host.Viewer);
        Assert.IsNull(await host.SubscribeAsync(viewer, topic));
        var observer = host.Services.GetServices<IRoutingEventObserver>().OfType<RealtimeRoutingEventObserver>().Single();
        await observer.OnRoutingEventAsync(new ConversationPresenceChanged(host.Scope, conversation, new UserId("agent"), ConversationActivity.Composing, host.Clock.GetUtcNow()));
        await observer.OnRoutingEventAsync(new ReplyLockChanged(host.Scope, conversation, new UserId("agent"), host.Clock.GetUtcNow().AddMinutes(1), host.Clock.GetUtcNow()));
        await observer.OnRoutingEventAsync(new QueueWaitExceeded(host.Scope, conversation, new InboxId("email-inbox"), host.Clock.GetUtcNow()));
        Assert.AreEqual(RealtimeActivity.Composing, viewer.Events<PresenceChangedEvent>().Single().Activity);
        Assert.AreEqual(new UserId("agent"), viewer.Events<ReplyLockChangedEvent>().Single().Holder);
        Assert.AreEqual(QueueChange.WaitExceeded, viewer.Events<QueueChangedEvent>().Single().Change);
    }

    [TestMethod]
    public async Task VisitorsSeeOnlyTheirSessionAndAgentsNeverSeeVisitorContent()
    {
        await using var host = await HostAsync();
        var conversation = await host.StartAsync();
        var sessionA = new VisitorSessionId("vs_a");
        var sessionB = new VisitorSessionId("vs_b");
        var visitor = host.Connect("visitor-a", new VisitorSubscriber(host.Scope, sessionA));
        Assert.IsNull(await host.SubscribeAsync(visitor, new VisitorSessionTopic(host.Scope, sessionA)));
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(visitor, new VisitorSessionTopic(host.Scope, sessionB)));
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(visitor, new ConversationTopic(host.Scope, conversation)));
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(visitor, new InboxQueueTopic(host.Scope, new InboxId("email-inbox"))));
        var agent = host.Connect("agent-1", host.Agent);
        Assert.IsNull(await host.SubscribeAsync(agent, new ConversationTopic(host.Scope, conversation)));
        Assert.AreEqual(RealtimeRefusal.Forbidden, await host.SubscribeAsync(agent, new VisitorSessionTopic(host.Scope, sessionA)));

        var now = host.Clock.GetUtcNow();
        VisitorMessageEvent Message(VisitorSessionId session) => new(host.Scope, session, conversation, new MessageId("m-out"), 2, MessageDirection.Outgoing, "Hi there",
            MessageContentType.PlainText, [], now);
        await host.Publisher().PublishAsync(new RealtimePublication(new VisitorSessionTopic(host.Scope, sessionA), Message(sessionA)));
        await host.Publisher().PublishAsync(new RealtimePublication(new VisitorSessionTopic(host.Scope, sessionA), Message(sessionB)));
        await host.Publisher().PublishAsync(new RealtimePublication(new ConversationTopic(host.Scope, conversation), Message(sessionA)));
        await host.Publisher().PublishAsync(new RealtimePublication(new VisitorSessionTopic(host.Scope, sessionA),
            new TypingEvent(host.Scope, conversation, new AgentActor(new UserId("agent")), true, now)));
        await host.Publisher().PublishAsync(new RealtimePublication(new VisitorSessionTopic(host.Scope, sessionA),
            new ConversationChangedEvent(host.Scope, conversation, new InboxId("email-inbox"), 1, ConversationChangeKind.Started, [], null, null, now)));

        Assert.AreEqual(sessionA, visitor.Events<VisitorMessageEvent>().Single().Session);
        Assert.IsInstanceOfType<SupportActor>(visitor.Events<TypingEvent>().Single().Actor.Value, "Agent identities are hidden from visitors.");
        Assert.IsEmpty(visitor.Events<ConversationChangedEvent>());
        Assert.IsEmpty(agent.Received, "Visitor-only content never reaches agents.");
    }

    [TestMethod]
    public async Task AgentActionsAuthorizeAndReachLinkedVisitorsAnonymously()
    {
        var directory = new FixedDirectory();
        await using var host = await HostAsync(services => services.AddSingleton<IVisitorSessionDirectory>(directory));
        var conversation = await host.StartAsync();
        directory.Sessions.Add(new VisitorSessionId("vs_a"));
        var visitor = host.Connect("visitor-a", new VisitorSubscriber(host.Scope, new VisitorSessionId("vs_a")));
        Assert.IsNull(await host.SubscribeAsync(visitor, new VisitorSessionTopic(host.Scope, new VisitorSessionId("vs_a"))));
        var viewer = host.Connect("viewer-1", host.Viewer);
        Assert.IsNull(await host.SubscribeAsync(viewer, new ConversationTopic(host.Scope, conversation)));
        var actions = host.Services.GetRequiredService<IRealtimeAgentActions>();

        Assert.AreEqual(RealtimeRefusal.Forbidden, Refusal(await actions.SetTypingAsync(host.Viewer, conversation, true)), "Typing needs conversation:reply.");
        Assert.AreEqual(RealtimeRefusal.NotFound, Refusal(await actions.SetTypingAsync(host.Agent, new ConversationId("missing"), true)));
        Assert.IsInstanceOfType<RealtimeActionApplied>((await actions.SetTypingAsync(host.Agent, conversation, true)).Value);
        Assert.AreEqual(new UserId("agent"), ((AgentActor)viewer.Events<TypingEvent>().Single().Actor.Value!).Agent);
        Assert.IsInstanceOfType<SupportActor>(visitor.Events<TypingEvent>().Single().Actor.Value);

        Assert.AreEqual(RealtimeRefusal.Invalid, Refusal(await actions.MarkReadAsync(host.Agent, conversation, 99)));
        Assert.IsInstanceOfType<RealtimeActionApplied>((await actions.MarkReadAsync(host.Agent, conversation, 1)).Value);
        Assert.AreEqual(1, visitor.Events<ReadReceiptEvent>().Single().UpToSequence);
    }

    [TestMethod]
    public async Task ConnectionAndSubscriptionLimitsAndDisconnect()
    {
        await using var host = await HostAsync(options: options => options.MaximumSubscriptionsPerConnection = 1);
        var conversation = await host.StartAsync();
        var agent = host.Connect("agent-1", host.Agent);
        Assert.IsNull(await host.SubscribeAsync(agent, new ConversationTopic(host.Scope, conversation)));
        Assert.AreEqual(RealtimeRefusal.LimitReached, await host.SubscribeAsync(agent, new AgentTopic(host.Scope, new UserId("agent"))));
        Assert.IsInstanceOfType<RealtimeSubscriptionRefused>(host.Registry().Connect(new TicketFlow.Realtime.Testing.RecordingConnection("agent-1", new AgentSubscriber(host.Agent))).Value,
            "Connection ids are unique per node.");
        Assert.IsTrue(host.Registry().Unsubscribe("agent-1", new ConversationTopic(host.Scope, conversation)));
        Assert.IsFalse(host.Registry().Unsubscribe("agent-1", new ConversationTopic(host.Scope, conversation)));
        Assert.IsNull(await host.SubscribeAsync(agent, new AgentTopic(host.Scope, new UserId("agent"))));
        host.Registry().Disconnect("agent-1");
        Assert.IsEmpty(host.Registry().Topics("agent-1"));
        Assert.AreEqual(RealtimeRefusal.NotConnected, await host.SubscribeAsync(agent, new AgentTopic(host.Scope, new UserId("agent"))));
        Applied(await host.Conversations.AssignAsync(host.Owner, conversation, new UserId("agent")));
        await host.DispatchAsync();
        Assert.IsEmpty(agent.Received);
    }

    private static RealtimeRefusal? Refusal(RealtimeActionResult result) => result.Value is RealtimeSubscriptionRefused refused ? refused.Reason : null;

    private sealed class FixedDirectory : IVisitorSessionDirectory
    {
        public List<VisitorSessionId> Sessions { get; } = [];

        public Task<IReadOnlyList<VisitorSessionId>> ListSessionsAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VisitorSessionId>>([.. Sessions]);
    }
}
