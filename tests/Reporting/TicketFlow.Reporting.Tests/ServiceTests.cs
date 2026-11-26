using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Surveys;
using TicketFlow.Surveys.StoreContracts;
using static TicketFlow.Reporting.StoreContracts.ReportingStoreContract;

namespace TicketFlow.Reporting.Tests;

[TestClass]
public sealed class ServiceTests
{
    private static readonly DateTimeOffset Start = ReportingFixture.Monday;
    private static readonly ReportQuery Week = new(Start.AddHours(-14), Start.AddDays(7));

    /// <summary>
    /// Seeds facts for three agents: ann (team billing), bob (team support) and an unassigned conversation, plus states (2 open) and
    /// survey responses (CSAT 5, 4, 2 for ann/bob/ann; NPS 10, 9, 3, 8).
    /// </summary>
    private static async Task SeedAsync(ReportingFixture fixture)
    {
        var ann = Dimensions("email", "billing", "ann", labels: ["vip"]);
        var bob = Dimensions("email", "support", "bob");
        var none = Dimensions("chat", null, null, "live-chat", ConversationPriority.Low);
        await Apply(fixture, State("c1", inbox: "email", team: "billing", assignee: "ann", labels: ["vip"]),
            Count(ReportMetric.ConversationCreated, Start, "c1", ann), Duration(ReportMetric.FirstResponseTime, Start.AddMinutes(10), TimeSpan.FromMinutes(10), "c1", ann),
            Duration(ReportMetric.FirstResponseTime, Start.AddMinutes(20), TimeSpan.FromMinutes(20), "c1", ann),
            Count(ReportMetric.ConversationResolved, Start.AddHours(2), "c1", ann), Duration(ReportMetric.ResolutionTime, Start.AddHours(2), TimeSpan.FromHours(2), "c1", ann),
            Count(ReportMetric.SlaAchieved, Start.AddHours(1), "c1", ann), Count(ReportMetric.MessageSent, Start.AddMinutes(10), "c1", ann));
        await Apply(fixture, State("c2", inbox: "email", team: "support", assignee: "bob", status: ConversationStatusKind.Resolved),
            Count(ReportMetric.ConversationCreated, Start.AddDays(1), "c2", bob), Duration(ReportMetric.FirstResponseTime, Start.AddDays(1).AddMinutes(30), TimeSpan.FromMinutes(30), "c2", bob),
            Count(ReportMetric.ConversationResolved, Start.AddDays(1).AddHours(1), "c2", bob), Count(ReportMetric.ConversationResolved, Start.AddDays(2), "c2", bob),
            Count(ReportMetric.SlaAchieved, Start.AddDays(1), "c2", bob), Count(ReportMetric.SlaBreached, Start.AddDays(1), "c2", bob),
            Count(ReportMetric.MessageSent, Start.AddDays(1), "c2", bob), Count(ReportMetric.MessageSent, Start.AddDays(1).AddMinutes(5), "c2", bob));
        await Apply(fixture, State("c3", inbox: "chat", team: null, assignee: null),
            Count(ReportMetric.ConversationCreated, Start.AddDays(2), "c3", none), Duration(ReportMetric.FirstResponseTime, Start.AddDays(2).AddHours(2), TimeSpan.FromHours(2), "c3", none),
            Count(ReportMetric.SlaAchieved, Start.AddDays(2), "c3", none));

        var surveys = fixture.Get<ISurveyStore>();
        async Task RespondAsync(SurveyKind kind, int score, string agent, string team)
        {
            var invitation = SurveyStoreContract.SampleInvitation(kind == SurveyKind.Csat ? "csat" : "nps", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Start, kind,
                "email", team, agent);
            var stored = await SurveyStoreContract.CreateSentAsync(surveys, fixture.Scope, invitation, SurveyToken.Create(), Start);
            Assert.AreEqual("recorded", SurveyStoreContract.R(await surveys.RecordResponseAsync(fixture.Scope, SurveyResponse.For(stored.Invitation, score, null, Start.AddHours(1)))));
        }
        await RespondAsync(SurveyKind.Csat, 5, "ann", "billing");
        await RespondAsync(SurveyKind.Csat, 4, "bob", "support");
        await RespondAsync(SurveyKind.Csat, 2, "ann", "billing");
        await RespondAsync(SurveyKind.Nps, 10, "ann", "billing");
        await RespondAsync(SurveyKind.Nps, 9, "bob", "support");
        await RespondAsync(SurveyKind.Nps, 3, "bob", "support");
        await RespondAsync(SurveyKind.Nps, 8, "ann", "billing");
    }

    private static async Task Apply(ReportingFixture fixture, ReportConversationState state, params ReportFact[] facts) =>
        Assert.StartsWith("applied:1:", W(await fixture.Store.ApplyAsync(fixture.Scope, new ReportProjection(state, facts), null)), StringComparison.Ordinal);

    [TestMethod]
    public async Task OverviewSummarizesVolumeResponseTimesSlaAndSatisfaction()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await SeedAsync(fixture);
        var overview = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetOverviewAsync(fixture.Supervisor, Week)));
        Assert.AreEqual((3L, 3L, 3L, 2L), (overview.Created, overview.Resolved, overview.MessagesSent, overview.OpenBacklog));
        Assert.AreEqual(4, overview.FirstResponse.Count);
        Assert.AreEqual(TimeSpan.FromMinutes(20), overview.FirstResponse.Median, "nearest rank of 10, 20, 30, 120 minutes");
        Assert.AreEqual(TimeSpan.FromHours(2), overview.FirstResponse.P90);
        Assert.AreEqual(TimeSpan.FromMinutes(45), overview.FirstResponse.Average);
        Assert.AreEqual(TimeSpan.FromHours(2), overview.Resolution.Median);
        Assert.AreEqual(ReportDurationSummary.Empty, overview.ResponseBusiness);
        Assert.AreEqual(75.0, overview.SlaCompliancePercent);
        Assert.IsNotNull(overview.Satisfaction);
        Assert.AreEqual(200.0 / 3, overview.Satisfaction.CsatPercent!.Value, 1e-9);
        Assert.AreEqual(25.0, overview.Satisfaction.NpsScore);
        Assert.AreEqual(Week.From, overview.From);

        // One of inbox/team/agent narrows satisfaction too; channel/priority/label filters cannot be applied to survey responses.
        var billing = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetOverviewAsync(fixture.Supervisor, new ReportQuery(Week.From, Week.To, new ReportFilter(team: new TeamId("billing"))))));
        Assert.AreEqual((1L, 1L, 2L), (billing.Created, billing.Resolved, billing.Satisfaction!.CsatResponses));
        var vip = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetOverviewAsync(fixture.Supervisor, new ReportQuery(Week.From, Week.To, new ReportFilter(label: new LabelId("vip"))))));
        Assert.AreEqual((1L, 1L), (vip.Created, vip.OpenBacklog));
        Assert.IsNull(vip.Satisfaction);
    }

    [TestMethod]
    public async Task ReportsAreFilteredByAccessPlans()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await SeedAsync(fixture);
        async Task<ReportOverview> OverviewAsync(PrincipalRef actor) => ReportingFixture.Value(await fixture.ReportAsync(service => service.GetOverviewAsync(actor, Week)));

        var lead = await OverviewAsync(fixture.BillingLead);          // report:view on team billing only
        Assert.AreEqual((1L, 1L, 2, 1L), (lead.Created, lead.Resolved, lead.FirstResponse.Count, lead.OpenBacklog));
        Assert.AreEqual(2, lead.Satisfaction!.CsatResponses);
        var ann = await OverviewAsync(fixture.Ann);                   // report:view-own: rows attributed to ann
        Assert.AreEqual((1L, 1L, 1L), (ann.Created, ann.MessagesSent, ann.SlaAchieved));
        Assert.AreEqual(2, ann.Satisfaction!.NpsResponses);
        var bob = await OverviewAsync(fixture.Bob);                   // no report permission: empty, not refused
        Assert.AreEqual((0L, 0L, 0L), (bob.Created, bob.OpenBacklog, bob.Satisfaction!.CsatResponses));
        Assert.IsNull(bob.SlaCompliancePercent);

        var board = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetLeaderboardAsync(fixture.Ann, new ReportLeaderboardQuery(Week))));
        CollectionAssert.AreEqual(new[] { "ann" }, board.Rows.Select(row => row.Agent.Value).ToArray());
        var trend = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetTrendsAsync(fixture.BillingLead,
            new ReportTrendQuery(Week, [ReportMetric.ConversationCreated], ReportGranularity.Day, ReportDimension.Team))));
        CollectionAssert.AreEqual(new[] { "billing" }, trend.Points.Select(point => point.Group).ToArray());
    }

    [TestMethod]
    public async Task LeaderboardRanksAgents()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await SeedAsync(fixture);
        async Task<string[]> RankAsync(ReportLeaderboardSort sort) =>
            [.. ReportingFixture.Value(await fixture.ReportAsync(service => service.GetLeaderboardAsync(fixture.Supervisor, new ReportLeaderboardQuery(Week, sort)))).Rows.Select(row => row.Agent.Value)];
        CollectionAssert.AreEqual(new[] { "bob", "ann" }, await RankAsync(ReportLeaderboardSort.Resolved));
        CollectionAssert.AreEqual(new[] { "bob", "ann" }, await RankAsync(ReportLeaderboardSort.MessagesSent));
        CollectionAssert.AreEqual(new[] { "ann", "bob" }, await RankAsync(ReportLeaderboardSort.FirstResponseMedian));
        var board = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetLeaderboardAsync(fixture.Supervisor, new ReportLeaderboardQuery(Week, limit: 1))));
        var bob = board.Rows.Single();
        Assert.AreEqual((2L, 2L, TimeSpan.FromMinutes(30)), (bob.Resolved, bob.MessagesSent, bob.FirstResponse.Median!.Value));
        Assert.AreEqual(1, bob.Satisfaction!.CsatSatisfied);
        Assert.AreEqual(0.0, bob.Satisfaction.NpsScore);
    }

    [TestMethod]
    public async Task TrendsBucketInTheWorkspaceTimeZoneAcrossDaylightSaving()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        // New York: Sat 2026-03-07 23:30 EST, Sun 03-08 00:30 EST, Sun 23:30 EDT (= Mon 03:30 UTC), Mon 03-09 00:30 EDT.
        DateTimeOffset[] instants =
        [
            new(2026, 3, 8, 4, 30, 0, TimeSpan.Zero), new(2026, 3, 8, 5, 30, 0, TimeSpan.Zero), new(2026, 3, 9, 3, 30, 0, TimeSpan.Zero), new(2026, 3, 9, 4, 30, 0, TimeSpan.Zero),
        ];
        await Apply(fixture, State("c1"), [.. instants.Select(at => Count(ReportMetric.ConversationCreated, at, dimensions: Dimensions(priority: ConversationPriority.Urgent)))]);
        await Apply(fixture, State("c2"), Duration(ReportMetric.ResponseTime, instants[1], TimeSpan.FromMinutes(5), "c2"), Duration(ReportMetric.ResponseTime, instants[2], TimeSpan.FromMinutes(7), "c2"));
        var query = new ReportQuery(new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero));
        var trend = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetTrendsAsync(fixture.Owner,
            new ReportTrendQuery(query, [ReportMetric.ConversationCreated, ReportMetric.ResponseTime], ReportGranularity.Day, ReportDimension.Priority))));
        Assert.AreEqual("America/New_York", trend.TimeZoneId);
        Assert.HasCount(4, trend.Buckets);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        string Day(DateTimeOffset bucket) => TimeZoneInfo.ConvertTime(bucket, zone).ToString("MM-dd", CultureInfo.InvariantCulture);
        CollectionAssert.AreEqual(new[] { "03-07:ConversationCreated:urgent:1", "03-08:ResponseTime:high:2", "03-08:ConversationCreated:urgent:2", "03-09:ConversationCreated:urgent:1" },
            trend.Points.Select(point => $"{Day(point.BucketStart)}:{point.Metric}:{point.Group}:{point.Count}").ToArray());
        var response = trend.Points.Single(point => point.Metric == ReportMetric.ResponseTime);
        Assert.AreEqual((TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(7)), (response.Median!.Value, response.P90!.Value));

        var hourly = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetTrendsAsync(fixture.Owner,
            new ReportTrendQuery(new ReportQuery(instants[0], instants[0].AddHours(3)), [ReportMetric.ConversationCreated], ReportGranularity.Hour))));
        Assert.AreEqual(2, hourly.Points.Count);
    }

    [TestMethod]
    public async Task ExportStreamsBoundedAuditedCsv()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await SeedAsync(fixture);
        await Apply(fixture, State("c9"), Count(ReportMetric.MessageReceived, Start.AddHours(3), "c9", Dimensions(agent: "=HYPERLINK(\"x\")")));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var summary = ReportingFixture.Value(await fixture.ReportAsync(service => service.ExportAsync(fixture.Owner, new ReportExportQuery(Week), writer)));
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual((19L, false), (summary.Rows, summary.Truncated));
        Assert.HasCount(20, lines);
        Assert.AreEqual("source_id,metric,occurred_at,conversation_id,inbox_id,team_id,agent_id,channel,priority,labels,value,unit", lines[0]);
        Assert.IsTrue(lines.Any(line => line.Contains(",first-response-time,", StringComparison.Ordinal) && line.EndsWith(",600000000,us", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.Contains(",\"'=HYPERLINK(\"\"x\"\")\",", StringComparison.Ordinal)), "formula neutralized and quoted");
        var audit = fixture.Get<InMemoryAccessAuditSink>().Snapshot();
        Assert.IsTrue(audit.Any(item => item.Kind == AccessAuditKind.FilterPlanned && item.Permission == ReportPermissions.Export && string.Equals(item.Actor.Id, "owner", StringComparison.Ordinal)));

        var bounded = new StringWriter(CultureInfo.InvariantCulture);
        var truncated = ReportingFixture.Value(await fixture.ReportAsync(service => service.ExportAsync(fixture.Owner, new ReportExportQuery(Week, maximumRows: 5), bounded)));
        Assert.AreEqual((5L, true), (truncated.Rows, truncated.Truncated));

        var refused = await fixture.ReportAsync(service => service.ExportAsync(fixture.Supervisor, new ReportExportQuery(Week), new StringWriter()));
        Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)refused.Value!).Kind);
        Assert.IsTrue(fixture.Get<InMemoryAccessAuditSink>().Snapshot().Any(item => item.Permission == ReportPermissions.Export && string.Equals(item.Actor.Id, "supervisor", StringComparison.Ordinal)),
            "refused export plans are audited too");
    }

    [TestMethod]
    public async Task ExportPagesThroughLargeRanges()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await Apply(fixture, State("c1"), [.. Enumerable.Range(0, 2500).Select(index => Count(ReportMetric.MessageReceived, Start.AddSeconds(index)))]);
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var all = ReportingFixture.Value(await fixture.ReportAsync(service => service.ExportAsync(fixture.Owner, new ReportExportQuery(Week), writer)));
        Assert.AreEqual((2500L, false), (all.Rows, all.Truncated));
        var bounded = ReportingFixture.Value(await fixture.ReportAsync(service => service.ExportAsync(fixture.Owner, new ReportExportQuery(Week, 2000), new StringWriter())));
        Assert.AreEqual((2000L, true), (bounded.Rows, bounded.Truncated));
        var exact = ReportingFixture.Value(await fixture.ReportAsync(service => service.ExportAsync(fixture.Owner, new ReportExportQuery(Week, 2500), new StringWriter())));
        Assert.AreEqual((2500L, false), (exact.Rows, exact.Truncated));
    }

    [TestMethod]
    public async Task RetentionPrunesFactsAndRollupsIndependently()
    {
        using var fixture = await ReportingFixture.CreateAsync(start: Start.AddDays(100), options: options =>
        {
            options.FactRetention = TimeSpan.FromDays(30);
            options.PruneBatchSize = 2;
            options.MaximumPruneBatches = 2;
        });
        await Apply(fixture, State("c1"), [.. Enumerable.Range(0, 5).Select(index => Count(ReportMetric.MessageReceived, Start.AddHours(index)))]);
        var worker = () => fixture.InScopeAsync(services => services.GetRequiredService<ReportingRetentionWorker>().RunOnceAsync());
        Assert.AreEqual(new ReportingRetentionResult(4, 0), await worker());
        Assert.AreEqual(new ReportingRetentionResult(1, 0), await worker());
        Assert.IsEmpty(await fixture.FactsAsync());
        var overview = ReportingFixture.Value(await fixture.ReportAsync(service => service.GetOverviewAsync(fixture.Owner, Week)));
        Assert.AreEqual(5, overview.MessagesReceived, "rollups are kept");
    }

    [TestMethod]
    public async Task CompositionRegistersPermissionsAndTheOutboxConsumer()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        var catalog = fixture.Get<IPermissionCatalog>();
        Assert.IsTrue(catalog.Find(ReportPermissions.Export)!.IsSensitive);
        Assert.IsFalse(catalog.Find(ReportPermissions.ViewOwn)!.IsSensitive);
        var consumers = await fixture.Get<IConversationOutbox>().ListConsumersAsync();
        CollectionAssert.Contains(consumers.ToArray(), ReportingOptions.Consumer);
        await fixture.InScopeAsync(services =>
        {
            Assert.IsInstanceOfType<SurveyReportSatisfactionSource>(services.GetRequiredService<IReportSatisfactionSource>());
            Assert.ContainsSingle(services.GetServices<IConversationOutboxHandler>().OfType<ReportingProjector>());
            return Task.FromResult(true);
        });
    }
}
