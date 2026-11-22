using Npgsql;
using TicketFlow.Core;
using TicketFlow.Events.StoreContracts;
using TicketFlow.Events.Tests;
using TicketFlow.Testing;

namespace TicketFlow.Events.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresEventStoreTests
{
    [AssemblyInitialize]
    public static void UsePostgres(TestContext context) => EventsFixture.StoreFactory = async builder =>
    {
        var storage = await PostgresTestDatabase.CreateAsync("tf_events_feature_", maximumPoolSize: 16);
        var store = new PostgresEventStore(storage.First);
        await store.InitializeAsync();
        builder.AddPostgresEventStores(storage.First);
        return storage;
    };

    internal sealed class Database : IAsyncDisposable
    {
        private readonly PostgresTestDatabase storage;

        private Database(PostgresTestDatabase storage)
        {
            this.storage = storage;
            var clock = TestClock.Create(EventStoreContract.Now);
            Store = new(storage.First, clock);
            Competing = new(storage.Second, clock);
        }

        public PostgresEventStore Store { get; }
        public PostgresEventStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_events_test_", async storage =>
        {
            var database = new Database(storage);
            await database.Store.InitializeAsync();
            await database.Competing.InitializeAsync();
            return database;
        }, maximumPoolSize: 32);

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var command = Primary.CreateCommand(sql);
            return await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task FeatureScenariosRunAgainstPostgres()
    {
        using var fixture = await EventsFixture.CreateAsync();
        Assert.IsInstanceOfType<PostgresEventStore>(fixture.Log);
        Assert.IsInstanceOfType<PostgresEventStore>(fixture.Get<IEventSubscriptionStore>());
    }

    [TestMethod]
    public async Task AppendContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await EventStoreContract.VerifyAppendAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task PruneAndCheckpointContract()
    {
        await using var database = await Database.CreateAsync();
        await EventStoreContract.VerifyPruneAndCheckpointsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task SubscriptionContract()
    {
        await using var database = await Database.CreateAsync();
        await EventStoreContract.VerifySubscriptionsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task DeliveryContract()
    {
        await using var database = await Database.CreateAsync();
        await EventStoreContract.VerifyDeliveryAsync(database.Store, database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentClaimsNeverShareALease()
    {
        await using var database = await Database.CreateAsync();
        for (var round = 0; round < 3; round++)
            await EventStoreContract.VerifyConcurrentClaimsAsync(database.Store, database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ManyConcurrentAppendersKeepTheSequenceGapless()
    {
        await using var database = await Database.CreateAsync();
        var scope = EventStoreContract.NewScope();
        var stores = Enumerable.Range(0, 6).Select(index => index % 2 == 0 ? database.Store : database.Competing).ToList();
        await Task.WhenAll(stores.Select((store, index) => Task.Run(async () =>
        {
            for (var batch = 0; batch < 10; batch++)
                await store.AppendAsync(scope, [EventStoreContract.Draft(source: $"w{index}-{batch}-a"), EventStoreContract.Draft(source: $"w{index}-{batch}-b"),
                    EventStoreContract.Draft(source: $"shared-{batch}")], EventStoreContract.Now);
        })));
        var events = await database.Store.ReadAsync(scope, 0, 500);
        Assert.AreEqual(6 * 10 * 2 + 10, events.Count);
        CollectionAssert.AreEqual(Enumerable.Range(1, events.Count).Select(index => (long)index).ToArray(), events.Select(item => item.Sequence).ToArray());
        Assert.AreEqual((long)events.Count, (await database.Store.GetHeadAsync(scope)).Last);
    }

    [TestMethod]
    public async Task SchemaUsesBoundedKeysAndVersionedRows()
    {
        await using var database = await Database.CreateAsync();
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM tf_store_migrations WHERE component = 'ticketflow.events'"));
        Assert.AreEqual("bytea", await database.ScalarAsync(
            "SELECT data_type FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'tf_events' AND column_name = 'scope_key'"));
        var scope = new TicketScope(TestScopes.LongIdentifier(1), TestScopes.LongIdentifier(2), TestScopes.LongIdentifier(3), TestScopes.LongIdentifier(4),
            new WorkspaceId(TestScopes.LongIdentifier(5)));
        await database.Store.AppendAsync(scope, [EventStoreContract.Draft(conversation: TestScopes.LongIdentifier(6))], EventStoreContract.Now);
        Assert.AreEqual(1, (await database.Competing.ReadAsync(scope, 0, 10)).Count);
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM tf_events WHERE format_version = 1"));
        Assert.IsFalse(((string)(await database.ScalarAsync("SELECT data FROM tf_events LIMIT 1"))!).Contains("body", StringComparison.Ordinal));
    }
}
