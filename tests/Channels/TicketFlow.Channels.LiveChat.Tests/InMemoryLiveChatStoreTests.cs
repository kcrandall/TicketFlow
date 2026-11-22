using TicketFlow.Channels.LiveChat.StoreContracts;

namespace TicketFlow.Channels.LiveChat.Tests;

[TestClass]
public sealed class InMemoryLiveChatStoreTests
{
    [TestMethod]
    public async Task WidgetContract()
    {
        var store = new InMemoryLiveChatStore();
        await LiveChatStoreContract.VerifyWidgetsAsync(store, store);
    }

    [TestMethod]
    public async Task SessionContract()
    {
        var store = new InMemoryLiveChatStore();
        await LiveChatStoreContract.VerifySessionsAsync(store, store);
    }

    [TestMethod]
    public async Task ConcurrentLinksAndUpdates()
    {
        var store = new InMemoryLiveChatStore();
        await LiveChatStoreContract.VerifyConcurrentLinksAsync(store, store);
    }
}
