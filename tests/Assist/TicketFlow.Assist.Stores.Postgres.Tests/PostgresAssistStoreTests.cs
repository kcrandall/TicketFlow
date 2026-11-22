using Npgsql;
using TicketFlow.Assist.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Assist.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresAssistStoreTests
{
    internal sealed class Database : IAsyncDisposable
    {
        private readonly PostgresTestDatabase storage;

        private Database(PostgresTestDatabase storage)
        {
            this.storage = storage;
            Store = new(storage.First);
            Competing = new(storage.Second);
        }

        public PostgresAssistStore Store { get; }
        public PostgresAssistStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_assist_test_", async storage =>
        {
            var database = new Database(storage);
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
    public async Task SettingsContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AssistStoreContract.VerifySettingsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ReservationContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AssistStoreContract.VerifyReservationsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentReservationsNeverExceedTheQuota()
    {
        await using var database = await Database.CreateAsync();
        await AssistStoreContract.VerifyConcurrentReservationsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task SummaryAndPurgeContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AssistStoreContract.VerifySummaryAndPurgeAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndRowsAreVersionedWithoutContent()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        var scope = TestScopes.Create(tenantId: TestScopes.LongIdentifier(1), workspaceId: TestScopes.LongIdentifier(2));
        var record = AssistStoreContract.Reservation(AssistStoreContract.Noon, 10);
        Assert.AreEqual("reserved", AssistStoreContract.R(await database.Store.ReserveAsync(scope, record, 100, 10)));
        Assert.AreEqual(record.Id, (await database.Competing.GetUsageAsync(scope, record.Id))!.Id);
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM tf_assist_usage WHERE format_version = 1"));
        Assert.AreEqual(32, await database.ScalarAsync("SELECT length(usage_key) FROM tf_assist_usage"));
        var columns = (long)(await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'tf_assist_usage' AND column_name IN ('prompt','output','body')"))!;
        Assert.AreEqual(0L, columns);
    }

    [TestMethod]
    public async Task UndecodableRowsThrow()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create(workspaceId: "corrupt");
        Assert.AreEqual("applied:1", AssistStoreContract.W(await database.Store.PutSettingsAsync(scope, new AssistSettings(true, 10, 1), 0)));
        await database.ScalarAsync("UPDATE tf_assist_settings SET settings = '{\"v\":9}'::jsonb");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetSettingsAsync(scope));
    }
}
