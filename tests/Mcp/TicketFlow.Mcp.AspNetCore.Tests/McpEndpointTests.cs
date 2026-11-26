using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp.AspNetCore.Tests;

[TestClass]
public sealed class McpEndpointTests
{
    private const string ClientHeader = "X-Test-Client";

    [TestMethod]
    public async Task UnauthenticatedRequestsAreRejectedByTheHostsAuthorization()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();
        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
    }

    [TestMethod]
    public async Task AuthenticatedApiClientsActAsTheirResolvedPrincipalThroughAccess()
    {
        await using var app = await StartAsync();
        await using var client = await ConnectAsync(app, "assistant");

        var me = (await client.CallToolAsync("whoami")).StructuredContent!.Value;
        Assert.AreEqual("apiClient", me.GetProperty("principal").GetProperty("kind").GetString());
        Assert.AreEqual("assistant", me.GetProperty("principal").GetProperty("id").GetString());

        var list = await client.CallToolAsync("conversations_list");
        Assert.IsNull(list.IsError);
        var conversation = list.StructuredContent!.Value.GetProperty("items")[0];
        var reply = await client.CallToolAsync("conversations_reply", new Dictionary<string, object?>
        {
            ["conversationId"] = conversation.GetProperty("id").GetString(),
            ["body"] = "On it.",
        });
        Assert.IsNull(reply.IsError, reply.StructuredContent?.GetRawText());

        var delete = await client.CallToolAsync("conversations_delete", new Dictionary<string, object?>
        {
            ["conversationId"] = conversation.GetProperty("id").GetString(),
            ["expectedRevision"] = reply.StructuredContent!.Value.GetProperty("revision").GetInt64(),
        });
        Assert.IsTrue(delete.IsError);
        Assert.AreEqual("forbidden", delete.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());

        var tools = (await client.ListToolsAsync()).ToDictionary(tool => tool.Name);
        Assert.IsTrue(tools["conversations_reply"].ProtocolTool.Meta!["ticketflow/permitted"]!.GetValue<bool>());
        Assert.IsFalse(tools.ContainsKey("access_list_roles"), "The Access toolset is not plugged in.");
    }

    [TestMethod]
    public async Task UnknownCallersAreUnauthenticatedAndPoliciesStayWithTheHost()
    {
        await using var app = await StartAsync(policy: "mcp");
        await using (var stranger = await ConnectAsync(app, "stranger"))
        {
            var list = await stranger.CallToolAsync("conversations_list");
            Assert.IsNull(list.IsError, "A principal without grants gets an empty, Access-filtered list.");
            Assert.AreEqual(0, list.StructuredContent!.Value.GetProperty("items").GetArrayLength());
        }
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add(ClientHeader, "blocked");
        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(request)).StatusCode, "The named policy (host-owned) refuses this caller.");
    }

    private static async Task<McpClient> ConnectAsync(WebApplication app, string clientId)
    {
        var http = app.GetTestClient();
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { [ClientHeader] = clientId },
        }, http));
    }

    private static async Task<WebApplication> StartAsync(string? policy = null)
    {
        var scope = TestScopes.Create();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddTicketFlowEndpoints(options =>
            {
                options.Partition = AccessPartition.From(scope);
                options.ResolvePrincipal = (http, target) => ValueTask.FromResult(http.User.FindFirstValue("client") is { } client
                    ? (PrincipalRef?)new ApiClientPrincipal(target, new ApiClientId(client)) : null);
            });
        builder.Services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization(options => options.AddPolicy("mcp", rule => rule.RequireAuthenticatedUser()
            .RequireAssertion(context => !string.Equals(context.User.FindFirstValue("client"), "blocked", StringComparison.Ordinal))));
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTicketFlowTools(options => options.DefaultWorkspace = scope.WorkspaceId.Value)
            .WithTicketFlowHostPrincipals();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTicketFlowMcp("/mcp", policy);
        await app.StartAsync();
        await SeedAsync(app, scope);
        return app;
    }

    private static async Task SeedAsync(WebApplication app, TicketScope scope)
    {
        await using var serviceScope = app.Services.CreateAsyncScope();
        var services = serviceScope.ServiceProvider;
        var grants = services.GetRequiredService<IAccessGrantStore>();
        await grants.CreateAsync(new AccessGrant(new GrantId("owner"), AccessPartition.From(scope), new UserId("owner"), BuiltInRoles.Owner.Id, new WorkspaceGrantScope(scope.WorkspaceId)));
        await grants.CreateAsync(new AccessGrant(new GrantId("assistant"), AccessPartition.From(scope), new ApiClientId("assistant"), BuiltInRoles.Agent.Id,
            new WorkspaceGrantScope(scope.WorkspaceId)));
        await services.GetRequiredService<IInboxStore>().CreateAsync(scope, new Inbox(new InboxId("support"), "Support", new ChannelBinding(new ChannelKind("email"), "cfg")));
        var owner = new UserPrincipal(scope, new UserId("owner"));
        var contact = (TicketValue<ContactView>)(await services.GetRequiredService<IContactService>().CreateAsync(owner, new ContactDraft(new ContactProfile("Ada")))).Value!;
        await services.GetRequiredService<IConversationService>().StartAsync(owner, new StartConversationRequest(new InboxId("support"), contact.Value.Id));
    }
}

/// <summary>Test-only authentication: the <c>X-Test-Client</c> header names the API client (the host's own MCP auth in real apps).</summary>
internal sealed class HeaderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestHeader";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers["X-Test-Client"].ToString() is not { Length: > 0 } client)
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("client", client)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
