using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TicketFlow.Testing;

namespace TicketFlow.SampleHost.Tests;

[TestClass]
public sealed class SampleHostSmokeTests
{
    private const string Api = "/ticketflow/api/workspaces/acme";

    [TestMethod]
    public async Task InMemoryHostBootsSeedsAndServesAgentsAndVisitors()
    {
        await using var app = Build(new() { ["TicketFlow:Storage"] = "InMemory" });
        await SampleHostApp.InitializeAsync(app, seed: true);
        await app.StartAsync();
        await VerifyAsync(app);
        await app.StopAsync();
    }

    [TestMethod]
    public async Task PostgresHostAppliesMigrationsSeedsTwiceAndServesAgents()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_sample_test_", maximumPoolSize: 16);
        await using var app = Build(new() { ["TicketFlow:Storage"] = "Postgres", ["ConnectionStrings:TicketFlow"] = database.ConnectionString });
        await SampleHostApp.InitializeAsync(app, seed: true);
        await SampleHostApp.InitializeAsync(app, seed: true);
        await app.StartAsync();
        await VerifyAsync(app);
        var health = await JsonAsync(await app.GetTestClient().GetAsync("/health"));
        Assert.AreEqual("Healthy", health.GetProperty("checks").GetProperty("postgres").GetProperty("status").GetString());
        await app.StopAsync();
    }

    [TestMethod]
    public void DevelopmentAuthenticationIsRefusedOutsideDevelopment() =>
        Assert.ThrowsExactly<InvalidOperationException>(() => SampleHostApp.Build(["--environment", "Production"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["TicketFlow:Storage"] = "InMemory", ["TicketFlow:DevelopmentAuthentication"] = "true" });
        }));

    private static WebApplication Build(Dictionary<string, string?> settings)
    {
        settings["TicketFlow:DevelopmentAuthentication"] = "true";
        return SampleHostApp.Build(["--environment", "Development"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(settings);
        });
    }

    private static async Task VerifyAsync(WebApplication app)
    {
        var anonymous = app.GetTestClient();
        var health = await anonymous.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        var report = await JsonAsync(health);
        Assert.AreEqual("Healthy", report.GetProperty("status").GetString());
        var loops = report.GetProperty("checks").GetProperty("ticketflow-workers").GetProperty("data");
        foreach (var loop in new[] { "event-delivery", "event-contacts", "event-surveys", "event-retention", "reporting-retention" })
            Assert.IsTrue(loops.GetProperty(loop).GetString()!.StartsWith("Running", StringComparison.Ordinal) || loops.GetProperty(loop).GetString()!.StartsWith("Pending", StringComparison.Ordinal),
                $"{loop} is hosted by the sample's workers.");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Api}/conversations")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/ticketflow/openapi/ticketflow.json")).StatusCode);
        Assert.Contains("wk_demo", await anonymous.GetStringAsync("/demo"));
        var articles = await JsonAsync(await anonymous.GetAsync("/ticketflow/public/workspaces/acme/portals/help/en/articles"));
        CollectionAssert.Contains(articles.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("slug").GetString()).ToList(), "reset-password");

        var admin = Client(app, "admin");
        var contact = await admin.PostAsJsonAsync($"{Api}/contacts", new { profile = new { displayName = "Grace" } });
        Assert.AreEqual(HttpStatusCode.Created, contact.StatusCode);
        var contactId = (await JsonAsync(contact)).GetProperty("id").GetString();
        var started = await admin.PostAsJsonAsync($"{Api}/conversations", new { inboxId = "web-chat", contactId, teamId = "tier1" });
        Assert.AreEqual(HttpStatusCode.Created, started.StatusCode);

        var agent = Client(app, "agent1");
        var me = await JsonAsync(await agent.GetAsync($"{Api}/me"));
        Assert.Contains("tier1", me.GetProperty("teams").EnumerateArray().Select(team => team.GetString()).ToList(), "The IdentityFlow resolver snapshots team membership.");
        var list = await JsonAsync(await agent.GetAsync($"{Api}/conversations?team=tier1"));
        Assert.IsGreaterThanOrEqualTo(1, list.GetProperty("items").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await agent.GetAsync($"{Api}/access/roles")).StatusCode);
    }

    private static HttpClient Client(WebApplication app, string user)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", user);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
