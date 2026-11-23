using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Reporting.StoreContracts;
using TicketFlow.Reporting.Tests;
using TicketFlow.Testing;

namespace TicketFlow.Reporting.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresReportingStoreTests
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

        public PostgresReportingStore Store { get; }
        public PostgresReportingStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_reporting_test_", async storage =>
        {
            var database = new Database(storage);
            await database.Store.InitializeAsync();
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
    public async Task ProjectionContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await ReportingStoreContract.VerifyProjectionAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task AggregateContract()
    {
        await using var database = await Database.CreateAsync();
        await ReportingStoreContract.VerifyAggregatesAsync(database.Store);
    }

    [TestMethod]
    public async Task FactsAndBacklogContract()
    {
        await using var database = await Database.CreateAsync();
        await ReportingStoreContract.VerifyFactsAndBacklogAsync(database.Store);
    }

    [TestMethod]
    public async Task RetentionAndRebuildContract()
    {
        await using var database = await Database.CreateAsync();
        await ReportingStoreContract.VerifyRetentionAndRebuildAsync(database.Store);
    }

    [TestMethod]
    public async Task ConcurrentProjectionsOnTwoDataSourcesCountEachFactOnce()
    {
        await using var database = await Database.CreateAsync();
        await ReportingStoreContract.VerifyConcurrentProjectionsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    [DataRow(12)]
    [DataRow(13)]
    [DataRow(14)]
    public async Task AccessFilteredParity(int seed)
    {
        await using var database = await Database.CreateAsync();
        await AccessFilteredReportParity.VerifyAsync(database.Store, seed);
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndVersioned()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.InitializeAsync();
        await database.Competing.InitializeAsync();
        Assert.AreEqual(3, Convert.ToInt32(await database.ScalarAsync("SELECT max(version) FROM tf_store_migrations WHERE component = 'ticketflow.reporting'")));
    }

    [TestMethod]
    public async Task UndecodableRowsThrow()
    {
        await using var database = await Database.CreateAsync();
        var scope = ReportingStoreContract.NewScope();
        await database.Store.ApplyAsync(scope, new ReportProjection(ReportingStoreContract.State(), []), null);
        await database.ScalarAsync("ALTER TABLE tf_report_conversations DROP CONSTRAINT tf_report_conversations_format_version_check");
        await database.ScalarAsync("UPDATE tf_report_conversations SET format_version = 9");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetConversationAsync(scope, ReportingStoreContract.State().Conversation));
    }

    [TestMethod]
    public async Task ProjectorAndServiceRunAgainstPostgres()
    {
        await using var database = await Database.CreateAsync();
        using var fixture = await ReportingFixture.CreateAsync(configure: services => services.AddSingleton<IReportingStore>(database.Store));
        await fixture.StartAsync("c1", team: "billing");
        await fixture.AssignAsync("c1", "ann", TimeSpan.FromMinutes(5));
        await fixture.ReplyAsync("c1", fixture.Ann, TimeSpan.FromMinutes(25));
        await fixture.StatusAsync("c1", ConversationStatus.Resolved, TimeSpan.FromHours(1));
        await fixture.DispatchAsync();
        await fixture.SyncAsync("c1");
        var facts = await fixture.FactsAsync();
        Assert.HasCount(11, facts);
        var overview = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetOverviewAsync(fixture.Supervisor,
            new ReportQuery(ReportingFixture.Monday.AddDays(-1), ReportingFixture.Monday.AddDays(1)))));
        Assert.AreEqual((1L, 1L, 0L, TimeSpan.FromMinutes(30)), (overview.Created, overview.Resolved, overview.OpenBacklog, overview.FirstResponse.Median!.Value));
        var lead = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetLeaderboardAsync(fixture.BillingLead,
            new ReportLeaderboardQuery(new ReportQuery(ReportingFixture.Monday.AddDays(-1), ReportingFixture.Monday.AddDays(1))))));
        Assert.AreEqual("ann", lead.Rows.Single().Agent.Value);
        var writer = new StringWriter();
        var export = ReportingFixture.Value(await fixture.ReportAsync(service => service.ExportAsync(fixture.Owner,
            new ReportExportQuery(new ReportQuery(ReportingFixture.Monday.AddDays(-1), ReportingFixture.Monday.AddDays(1))), writer)));
        Assert.AreEqual(11, export.Rows);
    }
}
