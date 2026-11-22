using TicketFlow.Conversations.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Tests;

[TestClass]
public sealed class InMemoryConversationStoreTests
{
    [TestMethod]
    public Task WriteContract()
    {
        var store = new InMemoryConversationStore();
        return ConversationStoreContract.VerifyWritesAsync(store, store);
    }

    [TestMethod]
    public Task TimelineAndMessageContract()
    {
        var store = new InMemoryConversationStore();
        return ConversationStoreContract.VerifyTimelineAndMessagesAsync(store, store);
    }

    [TestMethod]
    public Task DeliveryContract()
    {
        var store = new InMemoryConversationStore();
        return ConversationStoreContract.VerifyDeliveryAsync(store, store);
    }

    [TestMethod]
    public Task ListAndFilterContract() => ConversationStoreContract.VerifyListAndFiltersAsync(new InMemoryConversationStore());

    [TestMethod]
    public Task DeleteContract()
    {
        var store = new InMemoryConversationStore();
        return ConversationStoreContract.VerifyDeleteAsync(store, store);
    }

    [TestMethod]
    public Task ConcurrentAppendsAreGapless()
    {
        var store = new InMemoryConversationStore();
        return ConversationStoreContract.VerifyConcurrentAppendsAsync(store, store);
    }

    [TestMethod]
    public Task ConcurrentDuplicateInboundAdmitsOne()
    {
        var store = new InMemoryConversationStore();
        return ConversationStoreContract.VerifyConcurrentDuplicateInboundAsync(store, store);
    }

    [TestMethod]
    public Task OutboxContract()
    {
        var clock = TestClock.Create();
        var store = new InMemoryConversationStore(clock);
        return ConversationStoreContract.VerifyOutboxAsync(store, store, store, clock);
    }

    [TestMethod]
    public Task DefinitionContract()
    {
        var store = new InMemoryConversationAttributeDefinitionStore();
        return ConversationStoreContract.VerifyDefinitionsAsync(store, store);
    }

    [TestMethod]
    public async Task CapacityIsBounded()
    {
        var store = new InMemoryConversationStore(maximumConversations: 1);
        var scope = TestScopes.Create();
        await ConversationStoreContract.CreateAsync(store, scope, ConversationStoreContract.NewConversation("one"));
        var result = await store.WriteAsync(scope, new ConversationWrite(ConversationStoreContract.NewConversation("two"), null));
        Assert.AreEqual("rejected:CapacityExceeded", ConversationStoreContract.Describe(result));
    }
}
