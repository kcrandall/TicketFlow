using TicketFlow.Contacts.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.Tests;

[TestClass]
public sealed class InMemoryContactStoreTests
{
    [TestMethod]
    public async Task ContactContract()
    {
        var store = new InMemoryContactStore();
        await ContactStoreContract.VerifyContactsAsync(store, store);
    }

    [TestMethod]
    public async Task MergeRecordContract()
    {
        var store = new InMemoryContactStore();
        await ContactStoreContract.VerifyMergeRecordsAsync(store, store);
    }

    [TestMethod]
    public async Task SearchContract() => await ContactStoreContract.VerifySearchAsync(new InMemoryContactStore());

    [TestMethod]
    public async Task ChangeOutboxContract()
    {
        var store = new InMemoryContactStore();
        await ContactStoreContract.VerifyChangesAsync(store, store);
    }

    [TestMethod]
    public async Task DeleteCascadeContract()
    {
        var store = new InMemoryContactStore();
        await ContactStoreContract.VerifyDeleteCascadeAsync(store, store);
    }

    [TestMethod]
    public async Task BoundedScanContract() => await ContactStoreContract.VerifyBoundedScanAsync(new InMemoryContactStore(maximumRowsExamined: 3));

    [TestMethod]
    public async Task ConcurrentVerifiedIdentityContract()
    {
        var store = new InMemoryContactStore();
        await ContactStoreContract.VerifyConcurrentVerifiedIdentityAsync(store, store);
    }

    [TestMethod]
    public async Task DefinitionContract()
    {
        var store = new InMemoryContactAttributeDefinitionStore();
        await ContactStoreContract.VerifyDefinitionsAsync(store, store);
    }

    [TestMethod]
    public async Task CapacityIsBoundedAndFailsClosed()
    {
        var store = new InMemoryContactStore(maximumContacts: 1, maximumAuditEvents: 1);
        var scope = TestScopes.Create();
        Assert.AreEqual("applied:1", ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(store, scope, ContactStoreContract.NewContact("a"))));
        Assert.AreEqual("rejected:CapacityExceeded", ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(store, scope, ContactStoreContract.NewContact("b"))));
        var auditEvent = new ContactAuditEvent(Guid.NewGuid(), ContactStoreContract.At, scope, ContactAuditKind.Exported, new Access.AccessActor(Access.AccessActorKind.User, "u"));
        await store.RecordAsync(auditEvent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RecordAsync(auditEvent));
    }
}
