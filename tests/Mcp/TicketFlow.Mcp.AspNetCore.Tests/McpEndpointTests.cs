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
using TicketFlow.Access.AspNetCore;
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

    [TestMethod]
    public async Task McpResponsesSetCacheControlNoStore()
    {
        await using var app = await StartAsync();
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add(ClientHeader, "assistant");
        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore == true, $"Expected Cache-Control: no-store, got '{response.Headers.CacheControl}'.");
    }

    [TestMethod]
    public async Task ServiceAccountApiKeysEnforceWorkspaceVsTenantScopeAndRevocationOverHttpMcp()
    {
        var primaryScope = TestScopes.Create();
        var partition = AccessPartition.From(primaryScope);
        var secondaryWorkspace = new WorkspaceId("secondary");
        var secondaryScope = partition.InWorkspace(secondaryWorkspace);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddTicketFlowEndpoints(options => options.Partition = partition);
        builder.Services.AddTicketFlowApiKeyAuthentication(apiKey => apiKey.DefaultScope = primaryScope, setAsDefault: true);
        builder.Services.AddAuthorization();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTicketFlowTools(options =>
            {
                options.Toolsets = TicketFlowToolsets.All;
                options.DefaultWorkspace = primaryScope.WorkspaceId.Value;
            })
            .WithTicketFlowHostPrincipals();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTicketFlowMcp("/mcp");
        await app.StartAsync();

        string wsPlaintext;
        string wsKeyId;
        string tenantPlaintext;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var grants = services.GetRequiredService<IAccessGrantStore>();
            var tenantOwner = new UserPrincipal(primaryScope, new UserId("tenant-owner"));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-tenant-owner"), partition, new UserId("tenant-owner"), BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

            var inboxes = services.GetRequiredService<IInboxStore>();
            await inboxes.CreateAsync(primaryScope, new Inbox(new InboxId("inbox-primary"), "Primary", new ChannelBinding(new ChannelKind("email"), "cfg-p")));
            await inboxes.CreateAsync(secondaryScope, new Inbox(new InboxId("inbox-secondary"), "Secondary", new ChannelBinding(new ChannelKind("email"), "cfg-s")));

            var saService = services.GetRequiredService<IServiceAccountService>();
            var wsClient = new ApiClientId("sa-ws-scoped");
            Assert.IsInstanceOfType<TicketValue<AccessChangeApplied<StoredServiceAccount>>>(
                (await saService.CreateServiceAccountAsync(tenantOwner, new ServiceAccount(wsClient, partition, new WorkspaceGrantScope(primaryScope.WorkspaceId), "WS Bot"))).Value);
            await grants.CreateAsync(new AccessGrant(new GrantId("g-ws-bot"), partition, wsClient, BuiltInRoles.Agent.Id, new WorkspaceGrantScope(primaryScope.WorkspaceId)));
            var wsIssued = ((TicketValue<AccessChangeApplied<IssuedApiKey>>)(await saService.IssueApiKeyAsync(tenantOwner, new ApiKeyCreateRequest(wsClient, "ws-key"))).Value!).Value.Value;
            wsPlaintext = wsIssued.PlaintextKey;
            wsKeyId = wsIssued.Key.Id.Value;

            var tenantClient = new ApiClientId("sa-tenant-scoped");
            Assert.IsInstanceOfType<TicketValue<AccessChangeApplied<StoredServiceAccount>>>(
                (await saService.CreateServiceAccountAsync(tenantOwner, new ServiceAccount(tenantClient, partition, TenantGrantScope.Instance, "Tenant Bot"))).Value);
            await grants.CreateAsync(new AccessGrant(new GrantId("g-tenant-bot"), partition, tenantClient, BuiltInRoles.Agent.Id, TenantGrantScope.Instance));
            var tenantIssued = ((TicketValue<AccessChangeApplied<IssuedApiKey>>)(await saService.IssueApiKeyAsync(tenantOwner, new ApiKeyCreateRequest(tenantClient, "tenant-key"))).Value!).Value.Value;
            tenantPlaintext = tenantIssued.PlaintextKey;
        }

        // Workspace-scoped tf_sa_* key works in primary workspace, rejected in secondary workspace
        await using (var wsClient = await ConnectWithHeaderAsync(app, "X-Api-Key", wsPlaintext))
        {
            var me = (await wsClient.CallToolAsync("whoami")).StructuredContent!.Value;
            Assert.AreEqual("apiClient", me.GetProperty("principal").GetProperty("kind").GetString());
            Assert.AreEqual("sa-ws-scoped", me.GetProperty("principal").GetProperty("id").GetString());

            var crossWs = await wsClient.CallToolAsync("whoami", new Dictionary<string, object?> { ["workspace"] = secondaryWorkspace.Value });
            Assert.IsTrue(crossWs.IsError);
            Assert.AreEqual("unauthenticated", crossWs.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString(),
                "Workspace-scoped service account must not access another workspace.");
        }

        // Tenant-scoped tf_sa_* key (via Authorization: Bearer) works in BOTH primary and secondary workspaces
        await using (var tenantClient = await ConnectWithHeaderAsync(app, "Authorization", $"Bearer {tenantPlaintext}"))
        {
            var primaryMe = (await tenantClient.CallToolAsync("whoami")).StructuredContent!.Value;
            Assert.AreEqual(primaryScope.WorkspaceId.Value, primaryMe.GetProperty("scope").GetProperty("workspace").GetString());

            var secondaryMe = (await tenantClient.CallToolAsync("whoami", new Dictionary<string, object?> { ["workspace"] = secondaryWorkspace.Value })).StructuredContent!.Value;
            Assert.AreEqual(secondaryWorkspace.Value, secondaryMe.GetProperty("scope").GetProperty("workspace").GetString());
            Assert.AreEqual("agent", secondaryMe.GetProperty("roles")[0].GetString());
        }

        // Disabling the service account rejects subsequent HTTP /mcp requests with 401
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var saService = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
            var tenantOwner = new UserPrincipal(primaryScope, new UserId("tenant-owner"));
            Assert.IsInstanceOfType<TicketValue<AccessChangeApplied<StoredServiceAccount>>>(
                (await saService.SetServiceAccountEnabledAsync(tenantOwner, new ApiClientId("sa-ws-scoped"), isEnabled: false, "disabled in test")).Value);
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, await SendRawMcpAsync(app, new Dictionary<string, string> { ["X-Api-Key"] = wsPlaintext }),
            "Disabled service account key must be rejected with 401.");

        // Re-enabling works, then revoking the API key rejects with 401
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var saService = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
            var tenantOwner = new UserPrincipal(primaryScope, new UserId("tenant-owner"));
            Assert.IsInstanceOfType<TicketValue<AccessChangeApplied<StoredServiceAccount>>>(
                (await saService.SetServiceAccountEnabledAsync(tenantOwner, new ApiClientId("sa-ws-scoped"), isEnabled: true, "re-enabled")).Value);
            Assert.IsInstanceOfType<TicketValue<AccessChangeApplied<StoredApiKey>>>(
                (await saService.RevokeApiKeyAsync(tenantOwner, new ApiKeyId(wsKeyId), "revoked in test")).Value);
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, await SendRawMcpAsync(app, new Dictionary<string, string> { ["X-Api-Key"] = wsPlaintext }),
            "Revoked API key must be rejected with 401.");
    }

    [TestMethod]
    public async Task ExternalIdentityHeadersWithProviderPinnedRoleFederationWorkOverHttpMcp()
    {
        var scope = TestScopes.Create();
        var partition = AccessPartition.From(scope);
        const string ProxySecret = "test-upstream-shared-secret-value";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTicketFlow()
            .AddAccessControl(access => access.RoleFederation.Mode = RoleFederationMode.FederatedOnly)
            .AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddTicketFlowEndpoints(options => options.Partition = partition);
        builder.Services.AddTicketFlowExternalIdentityAuthentication(external =>
        {
            external.SharedSecretHeader = "X-Proxy-Secret";
            external.SharedSecret = ProxySecret;
            external.SubjectHeader = "X-Auth-User";
            external.RolesHeader = "X-Auth-Roles";
            external.AcceptRoleHeaders = true;
            external.ProviderName = "okta";
        }, setAsDefault: true);
        builder.Services.AddAuthorization();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTicketFlowTools(options => options.DefaultWorkspace = scope.WorkspaceId.Value)
            .WithTicketFlowHostPrincipals();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTicketFlowMcp("/mcp");
        await app.StartAsync();

        await using (var serviceScope = app.Services.CreateAsyncScope())
        {
            var services = serviceScope.ServiceProvider;
            var mappings = services.GetRequiredService<IRoleFederationMappingStore>();
            // Okta-pinned mapping -> Agent; AzureAD-pinned mapping -> Viewer (must not match okta caller)
            await mappings.UpsertAsync(
                new RoleFederationMappingId("okta-agents"),
                new RoleFederationMapping("okta", "support-agents", BuiltInRoles.Agent.Id, partition, new WorkspaceGrantScope(scope.WorkspaceId)),
                isEnabled: true,
                "seed");
            await mappings.UpsertAsync(
                new RoleFederationMappingId("azure-viewers"),
                new RoleFederationMapping("azuread", "support-viewers", BuiltInRoles.Viewer.Id, partition, new WorkspaceGrantScope(scope.WorkspaceId)),
                isEnabled: true,
                "seed");
            // Local non-admin grant for alice -> ignored under FederatedOnly!
            await services.GetRequiredService<IAccessGrantStore>().CreateAsync(
                new AccessGrant(new GrantId("local-supervisor"), partition, new UserId("alice"), BuiltInRoles.Supervisor.Id, new WorkspaceGrantScope(scope.WorkspaceId)));
        }

        // Without proxy secret -> 401
        Assert.AreEqual(HttpStatusCode.Unauthorized, await SendRawMcpAsync(app, new Dictionary<string, string>
        {
            ["X-Auth-User"] = "alice",
            ["X-Auth-Roles"] = "support-agents",
        }));

        // With valid proxy secret and okta external role -> Agent via federation, local Supervisor ignored in FederatedOnly
        await using var client = await ConnectWithHeadersAsync(app, new Dictionary<string, string>
        {
            ["X-Proxy-Secret"] = ProxySecret,
            ["X-Auth-User"] = "alice",
            ["X-Auth-Roles"] = "support-agents, support-viewers",
        });

        var me = (await client.CallToolAsync("whoami")).StructuredContent!.Value;
        var principal = me.GetProperty("principal");
        Assert.AreEqual("user", principal.GetProperty("kind").GetString());
        Assert.AreEqual("alice", principal.GetProperty("id").GetString());
        Assert.AreEqual("okta", principal.GetProperty("identityProvider").GetString());
        CollectionAssert.AreEquivalent(new[] { "support-agents", "support-viewers" },
            principal.GetProperty("externalRoles").EnumerateArray().Select(e => e.GetString()!).ToList());

        var roles = me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()!).ToList();
        CollectionAssert.AreEquivalent(new[] { "agent" }, roles, "Only okta-pinned mapping applies; azuread mapping and local non-admin grant are excluded.");
        var permissions = me.GetProperty("workspacePermissions").EnumerateArray().Select(e => e.GetString()!).ToList();
        CollectionAssert.Contains(permissions, "conversation:reply");
        CollectionAssert.DoesNotContain(permissions, "team:manage", "Local supervisor grant must not apply in FederatedOnly mode.");
    }

    private static Task<McpClient> ConnectWithHeaderAsync(WebApplication app, string header, string value) =>
        ConnectWithHeadersAsync(app, new Dictionary<string, string> { [header] = value });

    private static async Task<McpClient> ConnectWithHeadersAsync(WebApplication app, IDictionary<string, string> headers)
    {
        var http = app.GetTestClient();
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
        }, http));
    }

    private static async Task<HttpStatusCode> SendRawMcpAsync(WebApplication app, IDictionary<string, string> headers)
    {
        var http = app.GetTestClient();
        using var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
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
