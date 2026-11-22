using Microsoft.Extensions.Time.Testing;
using TicketFlow.Sla.StoreContracts;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Sla.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresSlaStoreTests
{
    private sealed class Database : IAsyncDisposable
    {
        private readonly PostgresTestDatabase storage;

        private Database(PostgresTestDatabase storage, FakeTimeProvider clock)
        {
            this.storage = storage;
            Clock = clock;
            Store = new(storage.First, clock);
            Competing = new(storage.Second, clock);
        }

        public FakeTimeProvider Clock { get; }
        public PostgresSlaStore Store { get; }
        public PostgresSlaStore Competing { get; }
        public PostgresTestDatabase Storage => storage;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_sla_test_", async storage =>
        {
            var database = new Database(storage, TestClock.Create());
            await database.Store.InitializeAsync();
            return database;
        }, maximumPoolSize: 16);

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task PolicyContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await SlaStoreContract.VerifyPoliciesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task TrackerContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await SlaStoreContract.VerifyTrackersAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task TimerContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await SlaStoreContract.VerifyTimersAsync(database.Store, database.Competing, database.Clock);
    }

    [TestMethod]
    public async Task ConcurrentTimerClaimsNeverShareALease()
    {
        await using var database = await Database.CreateAsync();
        var clients = Enumerable.Range(0, 4).Select(_ => new PostgresSlaStore(database.Storage.OpenClient(), database.Clock)).ToList();
        await SlaStoreContract.VerifyConcurrentClaimsAsync(clients, database.Clock);
    }

    [TestMethod]
    public async Task RiskListContractTranslatesAccessFilters()
    {
        await using var database = await Database.CreateAsync();
        await SlaStoreContract.VerifyRiskListAsync(database.Store);
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndVersioned()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Storage.First, PostgresSlaMigrations.Catalog).InspectAsync();
        Assert.AreEqual(1, inspection.CurrentVersion);
        Assert.AreEqual("ticketflow.sla", inspection.Component);
    }
}
