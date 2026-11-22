using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;

namespace TicketFlow.SampleHost.Tests;

[TestClass]
public sealed class SampleHostMcpTests
{
    [TestMethod]
    public async Task McpEndpointRunsToolsAsTheSeededApiClientWithItsAgentRole()
    {
        await using var app = SampleHostApp.Build(["--environment", "Development", "--applicationName", "TicketFlow.SampleHost"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TicketFlow:Storage"] = "InMemory",
                ["TicketFlow:DevelopmentAuthentication"] = "true",
            });
        });
        await SampleHostApp.InitializeAsync(app, seed: true);
        await app.StartAsync();
        var http = app.GetTestClient();

        using (var anonymous = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", System.Text.Encoding.UTF8, "application/json"),
        })
        {
            anonymous.Headers.Accept.ParseAdd("application/json");
            anonymous.Headers.Accept.ParseAdd("text/event-stream");
            Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, (await http.SendAsync(anonymous)).StatusCode);
        }

        await using (var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { [DevelopmentAuthenticationHandler.ApiKeyHeader] = "mcp-assistant" },
        }, http)))
        {
            var me = (await client.CallToolAsync("whoami")).StructuredContent!.Value;
            Assert.AreEqual("apiClient", me.GetProperty("principal").GetProperty("kind").GetString());
            Assert.AreEqual("idf:mcp-assistant", me.GetProperty("principal").GetProperty("id").GetString(), "IdentityFlow API clients are prefixed.");
            Assert.AreEqual("agent", me.GetProperty("roles")[0].GetString());

            var inboxes = await client.CallToolAsync("admin_list_inboxes");
            CollectionAssert.Contains(inboxes.StructuredContent!.Value.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToList(), "web-chat");
            var roles = await client.CallToolAsync("explain_access", new Dictionary<string, object?> { ["tool"] = "conversations_delete" });
            Assert.IsFalse(roles.StructuredContent!.Value.GetProperty("allowed").GetBoolean());
        }
        await app.StopAsync();
    }
}
