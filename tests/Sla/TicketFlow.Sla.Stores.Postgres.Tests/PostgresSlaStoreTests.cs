using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Sla.Tests;
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
    public async Task ConcurrentSchedulersSyncsAndDispatcherEmitExactlyOnce()
    {
        await using var database = await Database.CreateAsync();
        using var fixture = await SlaFixture.CreateAsync(SlaScenarios.Friday, services =>
        {
            services.AddSingleton(provider => new PostgresSlaStore(database.Storage.OpenClient(), provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ISlaTrackerStore>(provider => provider.GetRequiredService<PostgresSlaStore>());
            services.AddSingleton<ISlaPolicyStore>(provider => provider.GetRequiredService<PostgresSlaStore>());
        });
        Assert.IsInstanceOfType<PostgresSlaStore>(fixture.Get<ISlaTrackerStore>());
        await SlaScenarios.VerifyExactlyOnceUnderConcurrencyAsync(fixture);
    }

    [TestMethod]
    public async Task LeasesUseTheDatabaseClockByDefault()
    {
        await using var database = await Database.CreateAsync();
        var store = new PostgresSlaStore(database.Storage.First);
        var competing = new PostgresSlaStore(database.Storage.Second);
        var scope = TestScopes.Create(workspaceId: "db-clock");
        // Due instants come from application clocks; leases and claims compare against statement_timestamp().
        await store.WriteAsync(scope, SlaStoreContract.Tracker("due", nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1)), null);
        await store.WriteAsync(scope, SlaStoreContract.Tracker("later", nextCheckAt: DateTimeOffset.UtcNow.AddHours(1)), null);
        var claims = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(5));
        CollectionAssert.AreEqual(new[] { "due" }, claims.Select(claim => claim.Conversation.Value).ToArray());
        Assert.IsTrue(claims[0].LeaseUntil > DateTimeOffset.UtcNow.AddMinutes(4), "the lease is database now plus five minutes");
        Assert.AreEqual(0, (await competing.ClaimDueAsync(10, TimeSpan.FromMinutes(5))).Count);
        Assert.IsTrue(await competing.ReleaseAsync(claims[0], DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.AreEqual(1, (await competing.ClaimDueAsync(10, TimeSpan.FromMinutes(5))).Count);
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndVersioned()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Storage.First, PostgresSlaMigrations.Catalog).InspectAsync();
        Assert.AreEqual(3, inspection.CurrentVersion);
        Assert.AreEqual("ticketflow.sla", inspection.Component);
    }
}
