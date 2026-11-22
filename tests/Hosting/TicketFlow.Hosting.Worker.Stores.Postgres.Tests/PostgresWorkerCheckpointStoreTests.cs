using TicketFlow.Hosting.Worker.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Hosting.Worker.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresWorkerCheckpointStoreTests
{
    [TestMethod]
    public async Task CheckpointContractUsesIndependentClients()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_worker_test_");
        var store = new PostgresWorkerCheckpointStore(database.First);
        await store.InitializeAsync();
        await store.InitializeAsync();
        await WorkerCheckpointStoreContract.VerifyAsync(store, new PostgresWorkerCheckpointStore(database.Second));
    }

    [TestMethod]
    public async Task StoresAreUnusableBeforeExplicitInitialization()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_worker_test_");
        var store = new PostgresWorkerCheckpointStore(database.First);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.GetAsync(TestScopes.Create(), "contact-merges"));
    }
}
