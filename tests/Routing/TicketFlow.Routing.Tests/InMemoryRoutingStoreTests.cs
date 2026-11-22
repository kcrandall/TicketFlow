using TicketFlow.Routing.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Routing.Tests;

[TestClass]
public sealed class InMemoryRoutingStoreTests
{
    [TestMethod]
    public Task PolicyContract()
    {
        var store = new InMemoryRoutingStore(TestClock.Create());
        return RoutingStoreContract.VerifyPoliciesAsync(store, store);
    }

    [TestMethod]
    public Task AgentContract()
    {
        var clock = TestClock.Create();
        var store = new InMemoryRoutingStore(clock);
        return RoutingStoreContract.VerifyAgentsAsync(store, store, clock);
    }

    [TestMethod]
    public Task AssignmentContract()
    {
        var clock = TestClock.Create();
        var store = new InMemoryRoutingStore(clock);
        return RoutingStoreContract.VerifyAssignmentsAsync(store, store, clock);
    }

    [TestMethod]
    public Task ReservationRaceContract()
    {
        var store = new InMemoryRoutingStore(TestClock.Create());
        return RoutingStoreContract.VerifyReservationRacesAsync(store, store);
    }

    [TestMethod]
    public Task QueueContract()
    {
        var clock = TestClock.Create();
        var store = new InMemoryRoutingStore(clock);
        return RoutingStoreContract.VerifyQueueAsync(store, store, clock);
    }

    [TestMethod]
    public Task PresenceContract()
    {
        var clock = TestClock.Create();
        var store = new InMemoryRoutingStore(clock);
        return RoutingStoreContract.VerifyPresenceAsync(store, store, clock);
    }
}
