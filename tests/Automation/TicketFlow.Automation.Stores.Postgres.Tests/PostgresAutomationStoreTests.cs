using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TicketFlow.Automation.StoreContracts;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Automation.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresAutomationStoreTests
{
    private sealed class Database : IAsyncDisposable
    {
        private Database(PostgresTestDatabase storage, FakeTimeProvider clock)
        {
            Storage = storage;
            Clock = clock;
            Store = new(storage.First, clock);
            Competing = new(storage.Second, clock);
        }

        public PostgresTestDatabase Storage { get; }
        public FakeTimeProvider Clock { get; }
        public PostgresAutomationStore Store { get; }
        public PostgresAutomationStore Competing { get; }

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_automation_test_", async storage =>
        {
            var database = new Database(storage, TestClock.Create());
            await database.Store.InitializeAsync();
            return database;
        }, maximumPoolSize: 16);

        public ValueTask DisposeAsync() => Storage.DisposeAsync();
    }

    [TestMethod]
    public async Task RuleContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AutomationStoreContract.VerifyRulesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ScheduleContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AutomationStoreContract.VerifySchedulesAsync(database.Store, database.Competing, database.Clock);
    }

    [TestMethod]
    public async Task ConcurrentScheduleClaimsNeverShareALease()
    {
        await using var database = await Database.CreateAsync();
        var clients = Enumerable.Range(0, 4).Select(_ => new PostgresAutomationStore(database.Storage.OpenClient(), database.Clock)).ToList();
        await AutomationStoreContract.VerifyConcurrentScheduleClaimsAsync(clients, database.Clock);
    }

    [TestMethod]
    public async Task ExecutionContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AutomationStoreContract.VerifyExecutionsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task PurgeContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AutomationStoreContract.VerifyPurgeAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentBeginsHaveOneWinner()
    {
        await using var database = await Database.CreateAsync();
        var clients = Enumerable.Range(0, 4).Select(_ => (IAutomationExecutionStore)new PostgresAutomationStore(database.Storage.OpenClient(), database.Clock)).ToList();
        await AutomationStoreContract.VerifyConcurrentBeginAsync(clients);
    }

    [TestMethod]
    public async Task MacroContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AutomationStoreContract.VerifyMacrosAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task CannedResponseContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AutomationStoreContract.VerifyCannedResponsesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task LongIdentifiersFitTheBoundedKeys()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create(TestScopes.LongIdentifier(1), TestScopes.LongIdentifier(2), TestScopes.LongIdentifier(3), TestScopes.LongIdentifier(4), TestScopes.LongIdentifier(5));
        var rule = AutomationStoreContract.Rule(TestScopes.LongIdentifier(6));
        Assert.AreEqual("applied", AutomationStoreContract.Describe(await database.Store.CreateAsync(scope, rule, null)));
        Assert.AreEqual(rule, (await database.Competing.GetAsync(scope, rule.Id))!.Rule);
        var execution = AutomationStoreContract.Execution(rule: TestScopes.LongIdentifier(6), conversation: TestScopes.LongIdentifier(7));
        Assert.IsInstanceOfType<AutomationExecutionStarted>((await database.Store.BeginAsync(scope, execution)).Value);
        Assert.IsNotNull(await database.Competing.FindAsync(scope, execution.Rule, execution.Conversation, execution.TriggerKey));
    }

    [TestMethod]
    public async Task UndecodableRowsThrow()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        await database.Store.CreateAsync(scope, AutomationStoreContract.Rule("r"), null);
        await using (var corrupt = database.Storage.First.CreateCommand("UPDATE tf_automation_rules SET definition='{\"v\":9}'"))
            await corrupt.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, new AutomationRuleId("r")));
    }

    [TestMethod]
    public async Task UpgradeFromVersionOneBackfillsRuleSetsAndRetentionFlags()
    {
        await using var storage = await PostgresTestDatabase.CreateAsync("tf_automation_upgrade_");
        var clock = TestClock.Create();
        await new PostgresMigrationRunner(storage.First, PostgresAutomationMigrations.Catalog).ApplyAsync(targetVersion: 1);
        var scope = TestScopes.Create();
        var old = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var finished = AutomationStoreContract.Execution(rule: "u1", startedAt: old).Complete(old.AddSeconds(1), null);
        var running = AutomationStoreContract.Execution(rule: "u2", startedAt: old);
        await using (var insert = storage.First.CreateCommand("""
            INSERT INTO tf_automation_rules(tenant_id, project_id, environment_id, app_id, workspace_id, rule_id, priority, format_version, definition, revision, fence)
            VALUES(@t, @p, @e, @a, @w, 'legacy', 100, 1, @definition, 1, 0);
            INSERT INTO tf_automation_executions(tenant_id, project_id, environment_id, app_id, workspace_id, execution_id, rule_id, conversation_id, trigger_key, started_at,
                format_version, state, revision)
            VALUES(@t, @p, @e, @a, @w, @f, 'u1', 'c1', 'evt:1', @old, 1, @fstate, 1), (@t, @p, @e, @a, @w, @r, 'u2', 'c1', 'evt:1', @old, 1, @rstate, 1);
            """))
        {
            insert.Parameters.AddWithValue("t", scope.TenantId);
            insert.Parameters.AddWithValue("p", scope.ProjectId);
            insert.Parameters.AddWithValue("e", scope.EnvironmentId);
            insert.Parameters.AddWithValue("a", scope.AppId);
            insert.Parameters.AddWithValue("w", scope.WorkspaceId.Value);
            insert.Parameters.AddWithValue("definition", AutomationCodec.SerializeRule(AutomationStoreContract.Rule("legacy")));
            insert.Parameters.AddWithValue("f", finished.Id);
            insert.Parameters.AddWithValue("r", running.Id);
            insert.Parameters.AddWithValue("old", old);
            insert.Parameters.AddWithValue("fstate", AutomationCodec.SerializeExecution(finished));
            insert.Parameters.AddWithValue("rstate", AutomationCodec.SerializeExecution(running));
            await insert.ExecuteNonQueryAsync();
        }
        var store = new PostgresAutomationStore(storage.First, clock);
        await store.InitializeAsync();
        Assert.AreEqual(1, await store.GetRuleSetVersionAsync(scope));
        Assert.AreEqual(1, await store.PurgeAsync(old.AddDays(1), 100), "Only the finished legacy record is purged.");
        Assert.IsNotNull(await store.GetAsync(scope, running.Id));
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndVersioned()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Storage.First, PostgresAutomationMigrations.Catalog).InspectAsync();
        Assert.AreEqual(2, inspection.CurrentVersion);
        Assert.AreEqual("ticketflow.automation", inspection.Component);
    }
}
