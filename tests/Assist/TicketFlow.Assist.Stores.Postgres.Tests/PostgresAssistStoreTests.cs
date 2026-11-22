using TicketFlow.Stores.Migrations.Postgres;
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
    public async Task EstimatedSettlementSweepAndMixedConcurrencyUseIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AssistStoreContract.VerifyEstimatedSettlementAsync(database.Store, database.Competing);
        await AssistStoreContract.VerifySweepAsync(database.Store, database.Competing);
        await AssistStoreContract.VerifyConcurrentReserveAndSettleAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task QuotaUsesTheDayCounterRowNotAScanOfUsage()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create(workspaceId: "counter");
        var first = AssistStoreContract.Reservation(AssistStoreContract.Noon, 300);
        Assert.AreEqual("reserved", AssistStoreContract.R(await database.Store.ReserveAsync(scope, first, 1000, 100)));
        await database.Competing.SettleAsync(scope, first.Id, AssistUsageState.Completed, new AssistUsage(100, 20, 0, 0), AssistStoreContract.Noon.AddSeconds(1));
        Assert.AreEqual(120L, await database.ScalarAsync("SELECT tokens FROM tf_assist_usage_days WHERE workspace_id = 'counter' AND day = DATE '2026-03-10'"));
        // The counter is authoritative: usage rows are no longer summed, so raising it directly blocks reservations.
        await database.ScalarAsync("UPDATE tf_assist_usage_days SET tokens = 1000 WHERE workspace_id = 'counter'");
        Assert.AreEqual("exceeded:DailyTokens", AssistStoreContract.R(await database.Store.ReserveAsync(scope, AssistStoreContract.Reservation(AssistStoreContract.Noon.AddMinutes(1), 1), 1000, 100)));
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM tf_assist_usage WHERE workspace_id = 'counter'"), "a refused reservation writes nothing");
        Assert.AreEqual("reserved", AssistStoreContract.R(await database.Store.ReserveAsync(scope, AssistStoreContract.Reservation(AssistStoreContract.Noon.AddDays(1), 900), 1000, 100)));
    }

    [TestMethod]
    public async Task VersionTwoBackfillsDayCountersFromExistingUsage()
    {
        await using var storage = await PostgresTestDatabase.CreateAsync("tf_assist_upgrade_");
        var versionOne = new PostgresMigrationCatalog(PostgresAssistMigrations.Catalog.Component, [PostgresAssistMigrations.Catalog.Migrations[0]]);
        await new PostgresMigrationRunner(storage.First, versionOne).ApplyAsync();
        await using (var insert = storage.First.CreateCommand(
            "INSERT INTO tf_assist_usage(tenant_id, project_id, environment_id, app_id, workspace_id, usage_id, format_version, feature, model, actor_kind, actor_id, " +
            "conversation_id, state, reserved_tokens, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, created_at, completed_at) VALUES " +
            "('tenant','project','test','app','legacy', gen_random_uuid(), 1, 1, 'model-a', 1, 'agent', NULL, 2, 500, 100, 50, 0, 0, '2026-03-10T10:00:00Z', '2026-03-10T10:00:01Z'), " +
            "('tenant','project','test','app','legacy', gen_random_uuid(), 1, 1, 'model-a', 1, 'agent', NULL, 1, 400, 0, 0, 0, 0, '2026-03-10T11:00:00Z', NULL)"))
            await insert.ExecuteNonQueryAsync();
        var store = new PostgresAssistStore(storage.First);
        await store.InitializeAsync();
        var scope = TestScopes.Create(workspaceId: "legacy");
        // Backfilled: 150 (completed) + 400 (reserved) = 550 counted on 2026-03-10.
        Assert.AreEqual("exceeded:DailyTokens", AssistStoreContract.R(await store.ReserveAsync(scope, AssistStoreContract.Reservation(AssistStoreContract.Noon, 451), 1000, 100)));
        Assert.AreEqual("reserved", AssistStoreContract.R(await store.ReserveAsync(scope, AssistStoreContract.Reservation(AssistStoreContract.Noon, 450), 1000, 100)));
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
