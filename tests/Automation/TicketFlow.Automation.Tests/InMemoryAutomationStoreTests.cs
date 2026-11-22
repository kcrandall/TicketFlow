using TicketFlow.Automation.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Automation.Tests;

[TestClass]
public sealed class InMemoryAutomationStoreTests
{
    [TestMethod]
    public Task RuleContract()
    {
        var store = new InMemoryAutomationRuleStore();
        return AutomationStoreContract.VerifyRulesAsync(store, store);
    }

    [TestMethod]
    public Task ScheduleContract()
    {
        var clock = TestClock.Create();
        var store = new InMemoryAutomationRuleStore(clock);
        return AutomationStoreContract.VerifySchedulesAsync(store, store, clock);
    }

    [TestMethod]
    public Task ConcurrentScheduleClaims()
    {
        var clock = TestClock.Create();
        var store = new InMemoryAutomationRuleStore(clock);
        return AutomationStoreContract.VerifyConcurrentScheduleClaimsAsync([store, store, store], clock);
    }

    [TestMethod]
    public Task ExecutionContract()
    {
        var store = new InMemoryAutomationExecutionStore();
        return AutomationStoreContract.VerifyExecutionsAsync(store, store);
    }

    [TestMethod]
    public Task PurgeContract()
    {
        var store = new InMemoryAutomationExecutionStore();
        return AutomationStoreContract.VerifyPurgeAsync(store, store);
    }

    [TestMethod]
    public Task ConcurrentBegin()
    {
        var store = new InMemoryAutomationExecutionStore();
        return AutomationStoreContract.VerifyConcurrentBeginAsync([store, store]);
    }

    [TestMethod]
    public Task MacroContract()
    {
        var store = new InMemoryMacroStore();
        return AutomationStoreContract.VerifyMacrosAsync(store, store);
    }

    [TestMethod]
    public Task CannedResponseContract()
    {
        var store = new InMemoryCannedResponseStore();
        return AutomationStoreContract.VerifyCannedResponsesAsync(store, store);
    }
}
