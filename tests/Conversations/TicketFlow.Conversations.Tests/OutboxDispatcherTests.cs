using Microsoft.Extensions.Options;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Tests;

[TestClass]
public sealed class OutboxDispatcherTests
{
    private sealed class Handler(string consumer, Func<ConversationOutboxEntry, bool> succeed) : IConversationOutboxHandler
    {
        public List<long> Seen { get; } = [];

        public string Consumer => consumer;

        public Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
        {
            Seen.Add(entry.Sequence);
            return succeed(entry) ? Task.CompletedTask : throw new InvalidOperationException("handler failed");
        }
    }

    [TestMethod]
    public async Task HandlersAreAcknowledgedRetriedAndDeadLettered()
    {
        var clock = TestClock.Create();
        var store = new InMemoryConversationStore(clock);
        var good = new Handler("realtime", _ => true);
        var bad = new Handler("reporting", _ => false);
        var dispatcher = new ConversationOutboxDispatcher(store, [good, bad],
            Options.Create(new ConversationOutboxDispatcherOptions { MaximumAttempts = 2, InitialRetryDelay = TimeSpan.FromSeconds(10) }), clock);
        await dispatcher.RegisterAsync();
        var scope = TestScopes.Create();
        await StoreContracts.ConversationStoreContract.CreateAsync(store, scope, StoreContracts.ConversationStoreContract.NewConversation("a"));
        await StoreContracts.ConversationStoreContract.CreateAsync(store, scope, StoreContracts.ConversationStoreContract.NewConversation("b"));

        Assert.AreEqual(new ConversationOutboxDispatchResult(2, 2, 0, 0), await dispatcher.RunOnceAsync());
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 0, 0), await dispatcher.RunOnceAsync(), "retries wait for their backoff");
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 2, 0), await dispatcher.RunOnceAsync());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 0, 0), await dispatcher.RunOnceAsync());
        Assert.AreEqual(2, good.Seen.Count);
        Assert.AreEqual(4, bad.Seen.Count);
        Assert.AreEqual(2, await store.PruneAsync(clock.GetUtcNow(), 10));
    }

    [TestMethod]
    public void HandlerNamesMustBeValidAndUnique()
    {
        var store = new InMemoryConversationStore();
        var options = Options.Create(new ConversationOutboxDispatcherOptions());
        Assert.ThrowsExactly<ArgumentException>(() => new ConversationOutboxDispatcher(store, [new Handler("a", _ => true), new Handler("a", _ => true)], options, TimeProvider.System));
        Assert.ThrowsExactly<ArgumentException>(() => new ConversationOutboxDispatcher(store, [new Handler("Bad Name", _ => true)], options, TimeProvider.System));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ConversationOutboxDispatcher(store, [],
            Options.Create(new ConversationOutboxDispatcherOptions { BatchSize = 0 }), TimeProvider.System));
    }
}
