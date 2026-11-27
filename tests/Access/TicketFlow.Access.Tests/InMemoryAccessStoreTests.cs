using TicketFlow.Access.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class InMemoryAccessStoreTests
{
    [TestMethod]
    public async Task RoleStoreSatisfiesTheContract()
    {
        var store = new InMemoryAccessRoleStore();
        await AccessStoreContract.VerifyRolesAsync(store, store);
        await AccessStoreContract.VerifyRoleCapacityAsync(new InMemoryAccessRoleStore());
    }

    [TestMethod]
    public async Task GrantStoreSatisfiesTheContract()
    {
        var store = new InMemoryAccessGrantStore();
        await AccessStoreContract.VerifyGrantsAsync(store, store);
        var targets = new InMemoryAccessGrantStore();
        await AccessStoreContract.VerifyGrantTargetsAsync(targets, targets);
        await AccessStoreContract.VerifyGrantCapacityAsync(new InMemoryAccessGrantStore());
        var concurrent = new InMemoryAccessGrantStore();
        await AccessStoreContract.VerifyConcurrentGrantCapacityAsync(concurrent, concurrent);
    }

    [TestMethod]
    public async Task FieldPolicyStoreSatisfiesTheContract()
    {
        var store = new InMemoryAccessFieldPolicyStore();
        await AccessStoreContract.VerifyFieldPoliciesAsync(store, store);
    }

    [TestMethod]
    public async Task StoresAreBounded()
    {
        var grants = new InMemoryAccessGrantStore(maximumEntries: 1);
        var scope = TestScopes.Create();
        var partition = AccessPartition.From(scope);
        await grants.CreateAsync(new AccessGrant(new GrantId("a"), partition, new Core.UserId("u"), BuiltInRoles.Agent.Id, TenantGrantScope.Instance));
        var full = await grants.CreateAsync(new AccessGrant(new GrantId("b"), partition, new Core.UserId("u"), BuiltInRoles.Agent.Id, TenantGrantScope.Instance));
        Assert.AreEqual("rejected:CapacityExceeded", AccessStoreContract.Describe(full, stored => stored.Revision));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => grants.ListForSubjectsAsync(partition, scope.WorkspaceId,
            Enumerable.Range(0, AccessLimits.MaximumSubjectsPerQuery + 1).Select(index => (GrantSubject)new Core.UserId($"u{index}")).ToArray()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => grants.ListAllForSubjectsAsync(partition,
            Enumerable.Range(0, AccessLimits.MaximumSubjectsPerQuery + 1).Select(index => (GrantSubject)new Core.UserId($"u{index}")).ToArray()));

        var sink = new InMemoryAccessAuditSink(maximumEvents: 1);
        var auditEvent = new AccessAuditEvent(Guid.NewGuid(), TestClock.DefaultStart, scope, AccessAuditKind.Decision, AccessAuditOutcome.Allowed, new AccessActor(AccessActorKind.User, "u"));
        await sink.RecordAsync(auditEvent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sink.RecordAsync(auditEvent));
        Assert.HasCount(1, sink.Snapshot());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new InMemoryAccessRoleStore(0));
    }
}
