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
    public async Task InitializationIsIdempotentAndVersioned()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Storage.First, PostgresAutomationMigrations.Catalog).InspectAsync();
        Assert.AreEqual(1, inspection.CurrentVersion);
        Assert.AreEqual("ticketflow.automation", inspection.Component);
    }
}
