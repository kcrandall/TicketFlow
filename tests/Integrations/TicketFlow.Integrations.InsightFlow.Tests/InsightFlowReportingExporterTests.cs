using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Reporting;
using TicketFlow.Testing;

namespace TicketFlow.Integrations.InsightFlow.Tests;

[TestClass]
public sealed class InsightFlowReportingExporterTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TicketScope FactScope = TestScopes.Create(tenantId: "tf-tenant", projectId: "tf-proj", environmentId: "tf-env", appId: "tf-app", workspaceId: "acme");

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Auth, JsonDocument Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return respond(request, Requests.Count);
        }
    }

    private static InsightFlowExportOptions OptionsFor(int batchSize = 100, int maxBytes = InsightFlowExportOptions.DefaultMaximumPayloadBytes) => new()
    {
        Endpoint = new Uri("https://insightflow.example.com/v1/events/batch"),
        ApiKey = "if_secret_key",
        TenantId = "host-tenant",
        ProjectId = "host-project",
        Environment = "production",
        AppName = "support-portal",
        BatchSize = batchSize,
        MaximumPayloadBytes = maxBytes,
    };

    private static async Task SeedFactsAsync(InMemoryReportingStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var conv = new ConversationId($"c{i}");
            var at = Start.AddMinutes(i);
            var dims = new ReportDimensions(new InboxId("email"), new TeamId("billing"), new UserId("ann"), "email", ConversationPriority.High, [new LabelId("vip")]);
            var state = new ReportConversationState(conv, dims.Inbox, dims.Channel, at, ConversationStatusKind.Open, dims.Agent, dims.Team, dims.Priority, dims.Labels, null, null, 0, 1);
            var fact = new ReportFact(ReportFactIds.Created(FactScope, conv), ReportMetric.FirstResponseTime, conv, at, dims, ReportFact.Microseconds(TimeSpan.FromSeconds(45 + i)));
            await store.ApplyAsync(FactScope, new ReportProjection(state, [fact]), null);
        }
    }

    [TestMethod]
    public async Task ExportsFactsInClientTrackBatchRequestWireShapeWithHostScopeAndStableEventIds()
    {
        var clock = TestClock.Create(Start.AddHours(1));
        var store = new InMemoryReportingStore();
        await SeedFactsAsync(store, 2);
        var checkpoints = new InMemoryInsightFlowExportCheckpointStore();
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var exporter = new InsightFlowReportingExporter(store, checkpoints, Options.Create(OptionsFor()), clock, handler);

        var result = await exporter.ExportOnceAsync(FactScope);
        Assert.AreEqual(2, result.Exported);
        Assert.AreEqual(1, result.Batches);
        Assert.IsNull(result.FailureCode);

        var (uri, auth, doc) = handler.Requests.Single();
        Assert.AreEqual(new Uri("https://insightflow.example.com/v1/events/batch"), uri);
        Assert.AreEqual("Bearer if_secret_key", auth);

        var app = doc.RootElement.GetProperty("app");
        Assert.AreEqual("host-tenant", app.GetProperty("tenantId").GetString(), "Scope comes from host config, never from fact scope.");
        Assert.AreEqual("host-project", app.GetProperty("projectId").GetString());
        Assert.AreEqual("production", app.GetProperty("environment").GetString());
        Assert.AreEqual("support-portal", app.GetProperty("appName").GetString());

        var events = doc.RootElement.GetProperty("events");
        Assert.AreEqual(2, events.GetArrayLength());
        var first = events[0];
        Assert.AreEqual("ticketflow.reporting.first_response_time", first.GetProperty("eventName").GetString());
        Assert.AreEqual("agent:ann", first.GetProperty("identity").GetProperty("id").GetString());
        var firstEventId = first.GetProperty("eventId").GetString()!;
        Assert.AreEqual('8', firstEventId[14]);
        Assert.AreEqual(45_000_000L, first.GetProperty("properties").GetProperty("duration_us").GetInt64());
        Assert.AreEqual("c0", first.GetProperty("properties").GetProperty("conversation_id").GetString());
        Assert.IsFalse(doc.RootElement.GetRawText().Contains("tf-tenant", StringComparison.Ordinal), "Fact scope must not leak into InsightFlow app scope.");

        // Re-running with a reset checkpoint emits identical eventIds for deduplication.
        var checkpoints2 = new InMemoryInsightFlowExportCheckpointStore();
        using var exporter2 = new InsightFlowReportingExporter(store, checkpoints2, Options.Create(OptionsFor()), clock, handler);
        await exporter2.ExportOnceAsync(FactScope);
        Assert.AreEqual(firstEventId, handler.Requests[1].Body.RootElement.GetProperty("events")[0].GetProperty("eventId").GetString());
    }

    [TestMethod]
    public async Task CheckpointsAdvanceAcrossPagesAndHoldOnFailure()
    {
        var clock = TestClock.Create(Start.AddHours(1));
        var store = new InMemoryReportingStore();
        await SeedFactsAsync(store, 3);
        var checkpoints = new InMemoryInsightFlowExportCheckpointStore();

        // Fail first with 503 -> checkpoint stays null.
        using (var failing = new InsightFlowReportingExporter(store, checkpoints, Options.Create(OptionsFor(batchSize: 2)), clock,
            new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
        {
            var failed = await failing.ExportOnceAsync(FactScope);
            Assert.AreEqual("http-503", failed.FailureCode);
            Assert.IsFalse(failed.PermanentFailure);
            Assert.IsNull(await checkpoints.GetAsync(FactScope));
        }

        // Now succeed across 2 pages (2 + 1), then 0.
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var exporter = new InsightFlowReportingExporter(store, checkpoints, Options.Create(OptionsFor(batchSize: 2)), clock, handler);
        var page1 = await exporter.ExportOnceAsync(FactScope);
        Assert.AreEqual((2, true), (page1.Exported, page1.HasMore));
        var page2 = await exporter.ExportOnceAsync(FactScope);
        Assert.AreEqual((1, false), (page2.Exported, page2.HasMore));
        var page3 = await exporter.ExportOnceAsync(FactScope);
        Assert.AreEqual(0, page3.Exported);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SplitsOversizedPayloadsAndHttp413AndValidatesOptions()
    {
        var clock = TestClock.Create(Start.AddHours(1));
        var store = new InMemoryReportingStore();
        await SeedFactsAsync(store, 4);
        var checkpoints = new InMemoryInsightFlowExportCheckpointStore();

        var handler413 = new ScriptedHandler((req, attempt) =>
            attempt == 1 ? new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) : new HttpResponseMessage(HttpStatusCode.OK));
        using var exporter = new InsightFlowReportingExporter(store, checkpoints, Options.Create(OptionsFor()), clock, handler413);
        var result = await exporter.ExportOnceAsync(FactScope);
        Assert.AreEqual(4, result.Exported);
        Assert.AreEqual(2, result.Batches);
        Assert.AreEqual(3, handler413.Requests.Count, "1st attempt 413, then 2 split halves succeed.");

        var opts = OptionsFor();
        Assert.IsFalse(opts.ToString().Contains("if_secret_key", StringComparison.Ordinal));
        var bad = OptionsFor();
        bad.Endpoint = new Uri("http://insecure.example.com/batch");
        Assert.ThrowsExactly<InvalidOperationException>(() => new InsightFlowReportingExporter(store, checkpoints, Options.Create(bad), clock));

        var services = new ServiceCollection();
        services.AddSingleton<IReportingStore>(store);
        services.AddTicketFlow().AddInsightFlowReportingExport(o =>
        {
            o.Endpoint = new Uri("https://insightflow.example.com/v1/events/batch");
            o.TenantId = "t";
            o.ProjectId = "p";
            o.Environment = "e";
            o.AppName = "a";
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsNotNull(provider.GetRequiredService<InsightFlowReportingExporter>());
    }
}
