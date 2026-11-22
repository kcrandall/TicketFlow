using TicketFlow.Sla.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Sla.Tests;

[TestClass]
public sealed class InMemorySlaStoreTests
{
    [TestMethod]
    public Task PolicyContract()
    {
        var store = new InMemorySlaPolicyStore();
        return SlaStoreContract.VerifyPoliciesAsync(store, store);
    }

    [TestMethod]
    public Task TrackerContract()
    {
        var store = new InMemorySlaTrackerStore();
        return SlaStoreContract.VerifyTrackersAsync(store, store);
    }

    [TestMethod]
    public Task TimerContract()
    {
        var clock = TestClock.Create();
        var store = new InMemorySlaTrackerStore(clock);
        return SlaStoreContract.VerifyTimersAsync(store, store, clock);
    }

    [TestMethod]
    public Task ConcurrentTimerClaims()
    {
        var clock = TestClock.Create();
        var store = new InMemorySlaTrackerStore(clock);
        return SlaStoreContract.VerifyConcurrentClaimsAsync([store, store, store], clock);
    }

    [TestMethod]
    public Task RiskListContract() => SlaStoreContract.VerifyRiskListAsync(new InMemorySlaTrackerStore());
}
