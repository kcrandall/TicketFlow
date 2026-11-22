using TicketFlow.Channels.StoreContracts;

namespace TicketFlow.Channels.Tests;

[TestClass]
public sealed class InMemoryChannelStoreTests
{
    [TestMethod]
    public async Task EndpointContract()
    {
        var store = new InMemoryChannelStore();
        await ChannelStoreContract.VerifyEndpointsAsync(store, store);
    }

    [TestMethod]
    public async Task ConversationStateContract()
    {
        var store = new InMemoryChannelStore();
        await ChannelStoreContract.VerifyConversationStateAsync(store, store);
    }

    [TestMethod]
    public async Task ThreadContract()
    {
        var store = new InMemoryChannelStore();
        await ChannelStoreContract.VerifyThreadsAsync(store, store);
    }

    [TestMethod]
    public async Task DispatchContract()
    {
        var store = new InMemoryChannelStore();
        await ChannelStoreContract.VerifyDispatchesAsync(store, store);
    }
}
