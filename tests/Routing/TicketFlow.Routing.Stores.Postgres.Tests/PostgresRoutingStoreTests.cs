using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TicketFlow.Routing.StoreContracts;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Routing.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresRoutingStoreTests
{
    internal sealed class Database : IAsyncDisposable
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
        public PostgresRoutingStore Store { get; }
        public PostgresRoutingStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;
        public NpgsqlDataSource SecondSource => storage.Second;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_routing_test_", async storage =>
        {
            var database = new Database(storage, TestClock.Create());
            await database.Store.InitializeAsync();
            return database;
        }, maximumPoolSize: 24);

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var command = Primary.CreateCommand(sql);
            return await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task PolicyContract()
    {
        await using var database = await Database.CreateAsync();
        await RoutingStoreContract.VerifyPoliciesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task AgentContract()
    {
        await using var database = await Database.CreateAsync();
        await RoutingStoreContract.VerifyAgentsAsync(database.Store, database.Competing, database.Clock);
    }

    [TestMethod]
    public async Task AvailableAgentFilterContract()
    {
        await using var database = await Database.CreateAsync();
        await RoutingStoreContract.VerifyAvailableFiltersAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task DatabaseClockGovernsLeases()
    {
        await using var database = await Database.CreateAsync();
        // No TimeProvider: leases, reservations and expiries use clock_timestamp(), shared by every client.
        var store = new PostgresRoutingStore(database.Primary);
        var competing = new PostgresRoutingStore(database.SecondSource);
        Assert.IsTrue(store.UsesDatabaseClock);
        Assert.IsFalse(database.Store.UsesDatabaseClock);
        var scope = TestScopes.Create(workspaceId: "db-clock");
        var serverNow = (DateTime)(await database.ScalarAsync("SELECT clock_timestamp()"))!;
        await store.EnqueueAsync(scope, RoutingStoreContract.Snapshot("c1", TestClock.DefaultStart));
        var entry = (await ((IRoutingQueueStore)competing).GetAsync(scope, new Core.ConversationId("c1")))!.Entry;
        Assert.IsTrue(Math.Abs((entry.QueuedAt.UtcDateTime - serverNow.ToUniversalTime()).TotalSeconds) < 30, "queued at comes from the database clock");
        var claims = (await store.ClaimDueAsync(10, TimeSpan.FromSeconds(1))).Where(claim => claim.Scope == scope).ToList();
        Assert.AreEqual(1, claims.Count);
        Assert.AreEqual(0, (await competing.ClaimDueAsync(10, TimeSpan.FromSeconds(1))).Count(claim => claim.Scope == scope), "the lease is live for every client");
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var reclaimed = (await competing.ClaimDueAsync(10, TimeSpan.FromSeconds(30))).Single(claim => claim.Scope == scope);
        Assert.IsTrue(reclaimed.Fence > claims[0].Fence);
        Assert.IsFalse(await store.CompleteAttemptAsync(scope, entry.Conversation, claims[0].Fence,
            new RoutingQueueUpdate(TestClock.DefaultStart, entry.TargetInbox, 0, false)));
        Assert.IsTrue(await competing.CompleteAttemptAsync(scope, entry.Conversation, reclaimed.Fence,
            new RoutingQueueUpdate(TestClock.DefaultStart, entry.TargetInbox, 0, false)));
    }

    [TestMethod]
    public async Task AssignmentContract()
    {
        await using var database = await Database.CreateAsync();
        await RoutingStoreContract.VerifyAssignmentsAsync(database.Store, database.Competing, database.Clock);
    }

    [TestMethod]
    public async Task ReservationRaceContractAcrossClients()
    {
        await using var database = await Database.CreateAsync();
        await RoutingStoreContract.VerifyReservationRacesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task QueueContract()
    {
        await using var database = await Database.CreateAsync();
        await RoutingStoreContract.VerifyQueueAsync(database.Store, database.Competing, database.Clock);
    }

    [TestMethod]
    public async Task PresenceContract()
    {
        await using var database = await Database.CreateAsync();
        await RoutingStoreContract.VerifyPresenceAsync(database.Store, database.Competing, database.Clock);
    }

    [TestMethod]
    public async Task InitializeIsIdempotentAndUsesBoundedKeys()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        Assert.AreEqual(2L, await database.ScalarAsync($"SELECT count(*) FROM tf_store_migrations WHERE component='{PostgresRoutingMigrations.Catalog.Component}'"));
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname='tf_routing_queue_due_v2'"));
        Assert.AreEqual(0L, await database.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = current_schema() AND indexname='tf_routing_queue_due'"));
        var scope = TestScopes.Create(tenantId: TestScopes.LongIdentifier(1), workspaceId: TestScopes.LongIdentifier(2));
        var conversation = new Core.ConversationId(TestScopes.LongIdentifier(3));
        await database.Store.EnqueueAsync(scope, new RoutingQueueSnapshot(conversation, new Core.InboxId(TestScopes.LongIdentifier(4)), null,
            new Core.ContactId(TestScopes.LongIdentifier(5)), [new Core.LabelId(TestScopes.LongIdentifier(6))], [], TestClock.DefaultStart, 1));
        Assert.IsNotNull(await ((IRoutingQueueStore)database.Competing).GetAsync(scope, conversation));
        Assert.AreEqual(32, await database.ScalarAsync("SELECT octet_length(conversation_key) FROM tf_routing_queue LIMIT 1"));
    }

    [TestMethod]
    public async Task UndecodableRowsThrow()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        await database.Store.CreateAsync(scope, new RoutingPolicy(new Workspaces.RoutingPolicyId("p"), "P", RoutingStrategy.RoundRobin));
        await database.ScalarAsync("UPDATE tf_routing_policies SET document='{\"v\":2}'");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Competing.GetAsync(scope, new Workspaces.RoutingPolicyId("p")));
    }
}
