using TicketFlow.Workspaces.StoreContracts;

namespace TicketFlow.Workspaces.Tests;

[TestClass]
public sealed class InMemoryWorkspaceStoreTests
{
    [TestMethod]
    public async Task SettingsStoreSatisfiesTheContract()
    {
        var store = new InMemoryWorkspaceSettingsStore();
        await WorkspaceStoreContract.VerifySettingsAsync(store, store);
    }

    [TestMethod]
    public async Task InboxStoreSatisfiesTheContract()
    {
        var store = new InMemoryInboxStore();
        await WorkspaceStoreContract.VerifyInboxesAsync(store, store);
        await WorkspaceStoreContract.VerifyInboxListsAsync(store);
        var bindings = new InMemoryInboxStore();
        await WorkspaceStoreContract.VerifyInboxChannelBindingsAsync(bindings, bindings);
        await WorkspaceStoreContract.VerifyInboxCapacityAsync(new InMemoryInboxStore());
    }

    [TestMethod]
    public async Task TeamStoreSatisfiesTheContract()
    {
        var store = new InMemoryTeamStore();
        await WorkspaceStoreContract.VerifyTeamsAsync(store, store);
        await WorkspaceStoreContract.VerifyTeamListsAsync(store);
        await WorkspaceStoreContract.VerifyTeamMembershipLimitAsync(store);
        await WorkspaceStoreContract.VerifyConcurrentTeamMembershipLimitAsync(store, store);
        await WorkspaceStoreContract.VerifyTeamCapacityAsync(new InMemoryTeamStore());
    }

    [TestMethod]
    public async Task CalendarStoreSatisfiesTheContract()
    {
        var store = new InMemoryBusinessCalendarStore();
        await WorkspaceStoreContract.VerifyCalendarsAsync(store, store);
        await WorkspaceStoreContract.VerifyCalendarCapacityAsync(new InMemoryBusinessCalendarStore());
    }

    [TestMethod]
    public async Task StoresAreBoundedGlobally()
    {
        var store = new InMemoryInboxStore(maximumEntries: 1);
        var scope = Testing.TestScopes.Create();
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await store.CreateAsync(scope, WorkspaceStoreContract.SampleInbox("a"))).Value);
        Assert.IsInstanceOfType<WorkspaceWriteRejected>((await store.CreateAsync(scope, WorkspaceStoreContract.SampleInbox("b"))).Value);
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await store.DeleteAsync(scope, new Core.InboxId("a"), 1)).Value);
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await store.CreateAsync(scope, WorkspaceStoreContract.SampleInbox("b"))).Value);
    }
}

[TestClass]
public sealed class InMemoryListParityTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(42)]
    [DataRow(2026)]
    [DataRow(31337)]
    public Task ListsEqualPointChecksOverRandomizedGrants(int seed) =>
        AccessFilteredListParity.VerifyAsync(new InMemoryInboxStore(), new InMemoryTeamStore(), seed);
}
