using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Automation;
using TicketFlow.Channels;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Realtime;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.AspNetCore.Tests;

/// <summary>
/// An in-memory TicketFlow web host (TestServer) with every endpoint group. Requests authenticate with the test header <c>X-Test-User</c>
/// (<c>owner</c> and <c>agent</c> hold workspace grants; any other user holds none) through the options principal resolver.
/// </summary>
internal sealed class EndpointHost : IAsyncDisposable
{
    public const string Api = "/ticketflow/api/workspaces/workspace";
    public const string Public = "/ticketflow/public/workspaces/workspace";

    private EndpointHost(WebApplication app, TicketScope scope)
    {
        App = app;
        Scope = scope;
        Server = app.GetTestServer();
    }

    public WebApplication App { get; }
    public TestServer Server { get; }
    public TicketScope Scope { get; }

    public static async Task<EndpointHost> CreateAsync(Action<TicketFlowEndpointOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var scope = TestScopes.Create();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddRouting().AddInMemoryRoutingStores()
            .AddSla().AddInMemorySlaStores()
            .AddAutomation().AddInMemoryAutomationStores()
            .AddHelpCenter().AddInMemoryHelpCenterStores()
            .AddSurveys().AddInMemorySurveyStores()
            .AddChannels(options => options.WorkerPrincipal = target => new ApiClientPrincipal(target, new ApiClientId("channels"))).AddInMemoryChannelStores()
            .AddRealtime()
            .AddLiveChat().AddInMemoryLiveChatStores()
            .AddTicketFlowEndpoints(options =>
            {
                options.Partition = AccessPartition.From(scope);
                options.ResolvePrincipal = (http, target) => ValueTask.FromResult(
                    http.Request.Headers["X-Test-User"].ToString() is { Length: > 0 } user ? (PrincipalRef?)new UserPrincipal(target, new UserId(user)) : null);
                options.MaximumRequestBodyBytes = 64 * 1024;
                configure?.Invoke(options);
            })
            .AddTicketFlowOpenApi();
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapTicketFlowEndpoints();
        app.MapTicketFlowOpenApi();
        await app.StartAsync();
        var host = new EndpointHost(app, scope);
        await host.SeedAsync();
        return host;
    }

    private async Task SeedAsync()
    {
        using var serviceScope = App.Services.CreateScope();
        var services = serviceScope.ServiceProvider;
        var grants = services.GetRequiredService<IAccessGrantStore>();
        var index = 0;
        async Task GrantAsync(GrantSubject subject, RoleId role) => Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await grants.CreateAsync(
            new AccessGrant(new GrantId($"seed-{index++}"), AccessPartition.From(Scope), subject, role, new WorkspaceGrantScope(Scope.WorkspaceId)))).Value);
        await GrantAsync(new UserId("owner"), BuiltInRoles.Owner.Id);
        await GrantAsync(new UserId("agent"), BuiltInRoles.Agent.Id);
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await services.GetRequiredService<IInboxStore>().CreateAsync(Scope,
            new Inbox(new InboxId("support"), "Support", new ChannelBinding(new ChannelKind("email"), "cfg-support")))).Value);
    }

    public HttpClient Client(string? user = "owner")
    {
        var client = Server.CreateClient();
        if (user is not null)
            client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    public static HttpRequestMessage Request(HttpMethod method, string path, object? body = null, long? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        if (ifMatch is { } revision)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{revision}\""));
        return request;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static long Revision(HttpResponseMessage response) => long.Parse(response.Headers.ETag!.Tag.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
