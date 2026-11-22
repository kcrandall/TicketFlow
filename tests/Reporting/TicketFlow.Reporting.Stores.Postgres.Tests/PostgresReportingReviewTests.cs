using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Reporting.StoreContracts;
using TicketFlow.Reporting.Tests;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;
using static TicketFlow.Reporting.StoreContracts.ReportingStoreContract;

namespace TicketFlow.Reporting.Stores.Postgres.Tests;

/// <summary>Regression tests for the E14 review: label rollups, indexes, rebuild horizon and concurrency.</summary>
[TestClass]
public sealed class PostgresReportingReviewTests
{
    private static async Task<object?> ScalarAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    [TestMethod]
    public async Task RebuildHorizonContract()
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        await VerifyRebuildHorizonAsync(database.Store);
    }

    [TestMethod]
    [DataRow(21)]
    [DataRow(22)]
    public async Task LabelAggregatesMatchFacts(int seed)
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        await VerifyLabelAggregatesMatchFactsAsync(database.Store, seed);
    }

    [TestMethod]
    public async Task ALabelledFactWritesOneRollupRow()
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        var scope = NewScope();
        string[] labels = [.. Enumerable.Range(0, 40).Select(index => $"label-{index}")];
        await database.Store.ApplyAsync(scope, new ReportProjection(State(), [Count(ReportMetric.ConversationCreated, Day, dimensions: Dimensions(labels: labels))]), null);
        Assert.AreEqual(1L, await ScalarAsync(database.Primary, "SELECT count(*) FROM tf_report_rollups"));
        var byLabel = await database.Store.AggregateAsync(scope, Whole(Day, Day.AddDays(1), ReportDimension.Label, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance);
        Assert.HasCount(40, byLabel.Rows);
    }

    [TestMethod]
    public async Task AggregatesUseThePartialIndexes()
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        var target = NewScope();
        // Many scopes and mostly unlabelled facts, so both partial indexes are selective.
        for (var other = 0; other < 30; other++)
        {
            var scope = other == 0 ? target : NewScope();
            var facts = Enumerable.Range(0, 400).Select(index => Count(index % 2 == 0 ? ReportMetric.MessageSent : ReportMetric.MessageReceived, Day.AddMinutes(index * 7), $"c{other}",
                Dimensions(agent: $"u{index % 5}", labels: index % 40 == 0 ? ["vip"] : []))).ToList();
            Assert.StartsWith("applied:1:", W(await database.Store.ApplyAsync(scope, new ReportProjection(State($"c{other}"), facts), null)));
        }
        await ScalarAsync(database.Primary, "ANALYZE tf_report_rollups; ANALYZE tf_report_facts");

        async Task<string> PlanAsync(ReportAggregateQuery query)
        {
            await using var command = PostgresReportingStore.CreateAggregateCommand(target, query, new TeamInFilter([new TeamId("t0")]))!;
            command.CommandText = "EXPLAIN " + command.CommandText;
            await using var connection = await database.Primary.OpenConnectionAsync();
            command.Connection = connection;
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(0));
            return string.Join('\n', lines);
        }

        var rollupPlan = await PlanAsync(Whole(Day, Day.AddDays(3), ReportDimension.Agent, histograms: true, metrics: [ReportMetric.MessageSent, ReportMetric.MessageReceived]));
        StringAssert.Contains(rollupPlan, "tf_report_rollups_query_v2", rollupPlan);
        var labelPlan = await PlanAsync(Whole(Day, Day.AddDays(3), ReportDimension.Label, metrics: [ReportMetric.MessageSent]));
        StringAssert.Contains(labelPlan, "tf_report_facts_labelled", labelPlan);
        var labelFilterPlan = await PlanAsync(Whole(Day, Day.AddDays(3), filter: new ReportFilter(label: new LabelId("vip")), metrics: [ReportMetric.MessageSent]));
        StringAssert.Contains(labelFilterPlan, "tf_report_facts_labelled", labelFilterPlan);
    }

    [TestMethod]
    public async Task VersionOneSchemasUpgradeAndBackfillTheFactHorizon()
    {
        await using var storage = await PostgresTestDatabase.CreateAsync("tf_reporting_upgrade_", maximumPoolSize: 8);
        await new PostgresMigrationRunner(storage.First, PostgresReportingMigrations.Catalog).ApplyAsync(targetVersion: 1);
        var store = new PostgresReportingStore(storage.First);
        var scope = NewScope();
        var facts = Enumerable.Range(0, 10).Select(hour => Count(ReportMetric.ConversationCreated, Day.AddHours(hour), dimensions: Dimensions(labels: hour < 4 ? ["vip", "billing"] : []))).ToList();
        Assert.AreEqual("applied:1:10", W(await store.ApplyAsync(scope, new ReportProjection(State(), facts), null)));
        // Version 1 wrote one extra rollup row per label; simulate those rows and an earlier prune (rollups older than every fact).
        await ScalarAsync(storage.First, """
            INSERT INTO tf_report_rollups (tenant_id, project_id, environment_id, app_id, workspace_id, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, format_version)
            SELECT r.tenant_id, r.project_id, r.environment_id, r.app_id, r.workspace_id, r.metric, r.cell_us, r.inbox_id, r.team_id, r.agent_id, r.channel, r.priority, l.label, r.bin, r.count, r.total, r.minimum, r.maximum, 1
            FROM tf_report_rollups r JOIN tf_report_facts f ON f.cell_us = r.cell_us CROSS JOIN LATERAL unnest(f.labels) AS l(label);
            INSERT INTO tf_report_rollups (tenant_id, project_id, environment_id, app_id, workspace_id, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, format_version)
            SELECT tenant_id, project_id, environment_id, app_id, workspace_id, metric, cell_us - 86400000000, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, 1
            FROM tf_report_rollups WHERE label = '' AND cell_us = (SELECT min(cell_us) FROM tf_report_rollups);
            """);
        Assert.AreEqual(19L, await ScalarAsync(storage.First, "SELECT count(*) FROM tf_report_rollups"));

        await store.InitializeAsync();
        Assert.AreEqual(11L, await ScalarAsync(storage.First, "SELECT count(*) FROM tf_report_rollups"), "label rows removed; unlabelled rows kept");
        Assert.AreEqual(Day, await store.GetFactHorizonAsync(), "rollups predate every fact: horizon at the oldest remaining fact");
        var plain = await store.AggregateAsync(scope, Whole(Day.AddDays(-1), Day.AddDays(1), metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance);
        Assert.AreEqual(11, Total(plain, ReportMetric.ConversationCreated));
        var labelled = await store.AggregateAsync(scope, Whole(Day.AddDays(-1), Day.AddDays(1), ReportDimension.Label, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance);
        Assert.AreEqual("billing=4,vip=4", string.Join(",", labelled.Rows.Select(row => $"{row.Group}={row.Count}")));
        Assert.AreEqual($"refused:{Day:O}", B(await store.RebuildRollupsAsync(scope, Day.AddDays(-1), Day)));
        await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync(storage.First, """
            UPDATE tf_report_rollups SET label = 'x' WHERE cell_key = (SELECT cell_key FROM tf_report_rollups LIMIT 1)
            """));
    }

    [TestMethod]
    public async Task RebuildsRacingProjectionsNeverLoseOrDoubleCount()
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        var scope = NewScope();
        const int conversations = 40;
        var applies = Task.WhenAll(Enumerable.Range(0, conversations).Select(index => Task.Run(async () =>
        {
            var store = index % 2 == 0 ? database.Store : database.Competing;
            var facts = Enumerable.Range(0, 5).Select(item => Duration(ReportMetric.ResponseTime, Day.AddHours(1).AddMinutes(item), TimeSpan.FromSeconds(index + item + 1), $"c{index}")).ToList();
            Assert.AreEqual("applied:1:5", W(await store.ApplyAsync(scope, new ReportProjection(State($"c{index}"), facts), null)));
        })));
        var rebuilds = Task.Run(async () =>
        {
            var count = 0;
            while (!applies.IsCompleted || count < 3)
            {
                Assert.StartsWith("rebuilt:", B(await database.Competing.RebuildRollupsAsync(scope, Day, Day.AddDays(1))));
                count++;
            }
        });
        await Task.WhenAll(applies, rebuilds);
        var query = Whole(Day, Day.AddDays(1), histograms: true, metrics: ReportMetric.ResponseTime);
        var live = await database.Store.AggregateAsync(scope, query, AllowAllFilter.Instance);
        Assert.AreEqual(conversations * 5, Total(live, ReportMetric.ResponseTime));
        var expectedSum = Enumerable.Range(0, conversations).Sum(index => Enumerable.Range(0, 5).Sum(item => (long)(index + item + 1))) * 1_000_000;
        Assert.AreEqual(expectedSum, live.Rows.Sum(row => row.Sum));
        Assert.AreEqual($"rebuilt:{conversations * 5}", B(await database.Store.RebuildRollupsAsync(scope, Day, Day.AddDays(1))));
        CollectionAssert.AreEqual(live.Rows.ToArray(), (await database.Store.AggregateAsync(scope, query, AllowAllFilter.Instance)).Rows.ToArray());
    }

    [TestMethod]
    public async Task PrunesRacingRebuildsNeverRebuildPrunedRanges()
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        var scope = NewScope();
        var facts = Enumerable.Range(0, 96).Select(hour => Count(ReportMetric.ConversationCreated, Day.AddHours(hour))).ToList();
        await database.Store.ApplyAsync(scope, new ReportProjection(State(), facts), null);
        var plain = Whole(Day, Day.AddDays(4), metrics: ReportMetric.ConversationCreated);
        var prunes = Task.Run(async () =>
        {
            for (var day = 1; day <= 3; day++)
                while (await database.Competing.PruneFactsAsync(Day.AddDays(day), 5) > 0)
                {
                }
        });
        var rebuilds = Task.Run(async () =>
        {
            while (!prunes.IsCompleted)
                foreach (var day in new[] { 0, 1, 2 })
                    _ = await database.Store.RebuildRollupsAsync(scope, Day.AddDays(day), Day.AddDays(day + 1));
        });
        await Task.WhenAll(prunes, rebuilds);
        Assert.AreEqual(96, Total(await database.Store.AggregateAsync(scope, plain, AllowAllFilter.Instance), ReportMetric.ConversationCreated),
            "no rebuild ever ran over a range whose facts were pruned");
    }

    [TestMethod]
    public async Task ConcurrentSyncsOfOneConversationCountOnce()
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        using var fixture = await ReportingFixture.CreateAsync(configure: services => services.AddSingleton<IReportingStore>(database.Store));
        await ProjectorConcurrencyScenarios.ConcurrentSyncsOfOneConversationAsync(fixture);
    }

    [TestMethod]
    public async Task LateConsumerRacingTheLiveStreamCountsOnce()
    {
        await using var database = await PostgresReportingStoreTests.Database.CreateAsync();
        using var fixture = await ReportingFixture.CreateAsync(configure: services => services.AddSingleton<IReportingStore>(database.Store));
        await ProjectorConcurrencyScenarios.LateConsumerRacesLiveStreamAsync(fixture);
    }
}
