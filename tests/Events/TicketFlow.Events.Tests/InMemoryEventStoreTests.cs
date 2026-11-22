using TicketFlow.Events.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Events.Tests;

[TestClass]
public sealed class InMemoryEventStoreTests
{
    private static InMemoryEventStore Store() => new(TestClock.Create(EventStoreContract.Now));

    [TestMethod]
    public async Task AppendContract()
    {
        var store = Store();
        await EventStoreContract.VerifyAppendAsync(store, store);
    }

    [TestMethod]
    public async Task PruneAndCheckpointContract()
    {
        var store = Store();
        await EventStoreContract.VerifyPruneAndCheckpointsAsync(store, store);
    }

    [TestMethod]
    public async Task SubscriptionContract()
    {
        var store = Store();
        await EventStoreContract.VerifySubscriptionsAsync(store, store);
    }

    [TestMethod]
    public async Task DeliveryContract()
    {
        var store = Store();
        await EventStoreContract.VerifyDeliveryAsync(store, store, store);
    }

    [TestMethod]
    public async Task ConcurrentClaimContract()
    {
        var store = Store();
        await EventStoreContract.VerifyConcurrentClaimsAsync(store, store, store);
    }
}
