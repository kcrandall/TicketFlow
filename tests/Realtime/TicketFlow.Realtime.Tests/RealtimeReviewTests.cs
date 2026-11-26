using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Realtime.Testing;
using static TicketFlow.Realtime.Tests.RealtimeTestSupport;

namespace TicketFlow.Realtime.Tests;

[TestClass]
public sealed class RealtimeReviewTests
{
    [TestMethod]
    public async Task SlowAndThrowingConnectionsDoNotBlockOthersAndPublishersDoNotWait()
    {
        await using var host = await HostAsync(options: options =>
        {
            options.SendTimeout = TimeSpan.FromSeconds(5);
            options.ConnectionQueueCapacity = 2;
        });
        var registry = host.Services.GetRequiredService<RealtimeSubscriptionRegistry>();
        var session = new VisitorSessionId("vs_a");
        var topic = new VisitorSessionTopic(host.Scope, session);
        var subscriber = new VisitorSubscriber(host.Scope, session);
        var slow = new BlockingConnection("slow", subscriber);
        var throwing = new ThrowingConnection("throwing", subscriber);
        var fast = new RecordingConnection("fast", subscriber);
        foreach (var connection in new IRealtimeConnection[] { slow, throwing, fast })
        {
            Assert.IsInstanceOfType<RealtimeConnected>(registry.Connect(connection).Value);
            Assert.IsInstanceOfType<RealtimeSubscribed>((await registry.SubscribeAsync(connection.Id, topic)).Value);
        }

        var watch = Stopwatch.StartNew();
        for (var index = 1; index <= 5; index++)
            await host.Publisher().PublishAsync(new RealtimePublication(topic, new VisitorMessageEvent(host.Scope, session, new ConversationId("c1"), new MessageId($"m{index}"), index,
                MessageDirection.Outgoing, "hi", MessageContentType.PlainText, [], host.Clock.GetUtcNow())));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), watch.Elapsed, "Publishing only queues.");

        await WaitUntilAsync(() => fast.Events<VisitorMessageEvent>().Any(message => string.Equals(message.Message.Value, "m5", StringComparison.Ordinal)));
        Assert.IsLessThan(TimeSpan.FromSeconds(4), watch.Elapsed, "The fast client was not held up by the blocked one.");
        await WaitUntilAsync(() => Volatile.Read(ref throwing.Attempts) > 0);
        Assert.IsGreaterThan(0L, registry.DroppedCount, "The blocked client's bounded queue dropped its oldest events.");
        slow.Release();
    }

    [TestMethod]
    public async Task ConcurrentSubscribeAndDisconnectStayConsistent()
    {
        await using var host = await HostAsync();
        var conversation = await host.StartAsync();
        var registry = host.Services.GetRequiredService<RealtimeSubscriptionRegistry>();
        var topic = new ConversationTopic(host.Scope, conversation);
        var connections = Enumerable.Range(0, 40).Select(index => new RecordingConnection($"c-{index}", new AgentSubscriber(host.Agent), () => registry.IdleAsync())).ToList();
        foreach (var connection in connections)
            Assert.IsInstanceOfType<RealtimeConnected>(registry.Connect(connection).Value);

        await Task.WhenAll(connections.Select(async (connection, index) =>
        {
            await Task.Yield();
            var subscribed = registry.SubscribeAsync(connection.Id, topic);
            if (index % 2 == 0)
                registry.Disconnect(connection.Id);
            await subscribed;
            if (index % 2 == 0)
                registry.Disconnect(connection.Id);
        }));

        Assert.AreEqual(20, registry.ConnectionCount);
        foreach (var (connection, index) in connections.Select((connection, index) => (connection, index)))
            Assert.HasCount(index % 2 == 0 ? 0 : 1, registry.Topics(connection.Id));
        Applied(await host.Conversations.ReplyAsync(host.Agent, conversation, new OutgoingMessageDraft("hello")));
        await host.DispatchAsync();
        foreach (var (connection, index) in connections.Select((connection, index) => (connection, index)))
            Assert.AreEqual(index % 2 == 0 ? 0 : 1, connection.Events<ConversationChangedEvent>().Count(change => change.Kind == ConversationChangeKind.EventsAppended));
    }

    [TestMethod]
    public async Task QueueSubscriptionsEndWhenInboxReadIsRevokedAtReplanning()
    {
        await using var host = await HostAsync();
        var inboxReader = await host.RoleAsync("inbox-reader", AccessPermissions.InboxRead);
        await host.GrantAsync(new UserId("reader"), inboxReader.Role);
        await host.GrantAsync(new UserId("reader"), BuiltInRoles.Agent);
        var queue = new InboxQueueTopic(host.Scope, new InboxId("email-inbox"));
        var reader = host.Connect("reader-1", host.User("reader"));
        Assert.IsNull(await host.SubscribeAsync(reader, queue));
        var conversation = await host.StartAsync();
        await host.DispatchAsync();
        Assert.HasCount(1, reader.Events<ConversationChangedEvent>());

        var deny = new AccessGrant(new GrantId("deny-inbox"), AccessPartition.From(host.Scope), new UserId("reader"), inboxReader.Role.Id,
            new WorkspaceGrantScope(host.Scope.WorkspaceId), GrantEffect.Deny);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await host.Services.GetRequiredService<IAccessGrantStore>().CreateAsync(deny)).Value);
        // A deny overrides every allow of inbox:read (including the Agent role's), while conversation:read stays allowed.
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        Applied(await host.Conversations.ReplyAsync(host.Owner, conversation, new OutgoingMessageDraft("after revoke")));
        await host.DispatchAsync();
        Assert.HasCount(1, reader.Events<ConversationChangedEvent>(), "No delivery after inbox:read was revoked.");
        Assert.IsEmpty(host.Registry().Topics("reader-1"), "The queue subscription was removed.");
    }

    [TestMethod]
    public async Task EachChangeReadsTheConversationOncePerPublisherAndOnceForFanOut()
    {
        CountingConversationStore? counter = null;
        await using var host = await HostAsync(services => services.Replace(ServiceDescriptor.Singleton<IConversationStore>(provider =>
            counter = new CountingConversationStore(provider.GetRequiredService<InMemoryConversationStore>()))));
        var conversation = await host.StartAsync();
        Applied(await host.Conversations.AssignAsync(host.Owner, conversation, new UserId("agent")));
        foreach (var topic in new RealtimeTopic[] { new ConversationTopic(host.Scope, conversation), new InboxQueueTopic(host.Scope, new InboxId("email-inbox")),
            new AgentTopic(host.Scope, new UserId("agent")) })
        {
            var connection = host.Connect($"agent-{topic.GetType().Name}", host.Agent);
            Assert.IsNull(await host.SubscribeAsync(connection, topic));
        }
        await host.DispatchAsync();
        await host.Services.GetRequiredService<RealtimeSubscriptionRegistry>().IdleAsync();

        var handler = host.Services.GetServices<IConversationOutboxHandler>().OfType<RealtimeOutboxHandler>().Single();
        var change = new ConversationChange(Guid.NewGuid(), host.Scope, conversation, new InboxId("email-inbox"),
            (await counter!.GetAsync(host.Scope, conversation))!.Revision, ConversationChangeKind.EventsAppended,
            [new ConversationEventSummary(99, ConversationEventKind.System)], null, null, host.Clock.GetUtcNow());
        counter.Reads = 0;
        await handler.HandleAsync(new ConversationOutboxEntry(1, change));
        await host.Services.GetRequiredService<RealtimeSubscriptionRegistry>().IdleAsync();
        Assert.IsTrue(counter.Reads is >= 1 and <= 2, $"At most one read by the handler (assignee) and one by the fan-out worker for three topics; got {counter.Reads}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class BlockingConnection(string id, RealtimeSubscriber subscriber) : IRealtimeConnection
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;
        public RealtimeSubscriber Subscriber { get; } = subscriber;

        public void Release() => release.TrySetResult();

        public async ValueTask SendAsync(RealtimeTopic topic, RealtimeEvent realtimeEvent, CancellationToken cancellationToken = default) =>
            await release.Task.WaitAsync(cancellationToken);
    }

    private sealed class ThrowingConnection(string id, RealtimeSubscriber subscriber) : IRealtimeConnection
    {
        public int Attempts;

        public string Id { get; } = id;
        public RealtimeSubscriber Subscriber { get; } = subscriber;

        public ValueTask SendAsync(RealtimeTopic topic, RealtimeEvent realtimeEvent, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("socket gone");
        }
    }

    private sealed class CountingConversationStore(IConversationStore inner) : IConversationStore
    {
        public int Reads;

        public Task<StoredConversation?> GetAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return inner.GetAsync(scope, id, cancellationToken);
        }

        public Task<ConversationWriteResult> WriteAsync(TicketScope scope, ConversationWrite write, CancellationToken cancellationToken = default) => inner.WriteAsync(scope, write, cancellationToken);
        public Task<ConversationWriteResult> DeleteAsync(TicketScope scope, ConversationId id, long expectedRevision, ConversationChange change, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(scope, id, expectedRevision, change, cancellationToken);
        public Task<IReadOnlyList<TimelineEvent>> ReadTimelineAsync(TicketScope scope, ConversationId id, long afterSequence, int limit, bool includeNotes, CancellationToken cancellationToken = default) =>
            inner.ReadTimelineAsync(scope, id, afterSequence, limit, includeNotes, cancellationToken);
        public Task<StoredMessage?> GetMessageAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default) =>
            inner.GetMessageAsync(scope, conversation, message, cancellationToken);
        public Task<StoredMessage?> FindMessageByExternalIdAsync(TicketScope scope, InboxId inbox, string externalId, CancellationToken cancellationToken = default) =>
            inner.FindMessageByExternalIdAsync(scope, inbox, externalId, cancellationToken);
        public Task<MessageDeliveryWriteResult> UpdateDeliveryAsync(TicketScope scope, MessageDeliveryWrite write, CancellationToken cancellationToken = default) =>
            inner.UpdateDeliveryAsync(scope, write, cancellationToken);
        public Task<StoredConversation?> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default) =>
            inner.FindByThreadAsync(scope, inbox, thread, cancellationToken);
        public Task<ConversationPage> ListAsync(TicketScope scope, ConversationListQuery query, AccessFilter filter, CancellationToken cancellationToken = default) =>
            inner.ListAsync(scope, query, filter, cancellationToken);
    }
}
