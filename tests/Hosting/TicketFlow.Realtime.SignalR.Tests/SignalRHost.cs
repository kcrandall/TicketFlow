using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Channels;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Realtime.SignalR.Tests;

/// <summary>
/// An in-memory TicketFlow web host (TestServer) with the agent and visitor hubs and the upload endpoint. Agents authenticate with the
/// test header <c>X-Test-User</c> (owner, agent, stranger); widget key <c>wk_demo</c> allows <see cref="Origin"/>.
/// </summary>
internal sealed class SignalRHost : IAsyncDisposable
{
    public const string Origin = "https://shop.example.com";
    public const string WidgetKey = "wk_demo";
    public const string AgentPath = "/ticketflow/realtime/agent";
    public const string VisitorPath = "/ticketflow/realtime/visitor";

    private readonly List<HubConnection> connections = [];

    private SignalRHost(WebApplication app, TicketScope scope)
    {
        App = app;
        Scope = scope;
        Server = app.GetTestServer();
    }

    public WebApplication App { get; }
    public TestServer Server { get; }
    public TicketScope Scope { get; }

    public static async Task<SignalRHost> CreateAsync(Action<TicketFlowSignalROptions>? configure = null)
    {
        var scope = TestScopes.Create();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddChannels(options => options.WorkerPrincipal = target => new ApiClientPrincipal(target, new ApiClientId("channels"))).AddInMemoryChannelStores()
            .AddRealtime()
            .AddLiveChat().AddInMemoryLiveChatStores()
            .AddTicketFlowSignalR(options =>
            {
                options.VisitorPartition = AccessPartition.From(scope);
                options.ResolvePrincipal = (_, http) => ValueTask.FromResult(
                    http?.Request.Headers["X-Test-User"].ToString() is { Length: > 0 } user ? (PrincipalRef?)new UserPrincipal(scope, new UserId(user)) : null);
                configure?.Invoke(options);
            });
        builder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        var app = builder.Build();
        app.MapTicketFlowRealtime(AgentPath, VisitorPath);
        app.MapLiveChatAttachmentUpload();
        await app.StartAsync();
        var host = new SignalRHost(app, scope);
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
        var worker = new AccessRole(new RoleId("channel-worker"), "Channel worker",
            [ConversationPermissions.Create, ConversationPermissions.Ingest, ContactPermissions.Read, ContactPermissions.Create]);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessRole>>((await services.GetRequiredService<IAccessRoleStore>().CreateAsync(Scope, worker)).Value);
        await GrantAsync(new ApiClientId("channels"), worker.Id);

        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await services.GetRequiredService<IInboxStore>().CreateAsync(Scope,
            new Inbox(new InboxId("chat-inbox"), "Chat", new ChannelBinding(new ChannelKind("livechat"), "cfg-chat")))).Value);
        Assert.IsInstanceOfType<ChannelWriteApplied<StoredChannelEndpoint>>((await services.GetRequiredService<IChannelEndpointStore>().CreateAsync(Scope,
            new ChannelEndpoint(new ChannelKind("livechat"), "cfg-chat", new InboxId("chat-inbox"), WidgetKey))).Value);
        Assert.IsInstanceOfType<LiveChatWriteApplied<StoredLiveChatWidget>>((await services.GetRequiredService<ILiveChatWidgetStore>().CreateAsync(Scope,
            new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin], attachments: LiveChatAttachmentPolicy.Default))).Value);
        await services.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync();
    }

    public HubConnection Hub(string path, string? user = null, string origin = Origin)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, path), options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["Origin"] = origin;
                if (user is not null)
                    options.Headers["X-Test-User"] = user;
            })
            .Build();
        connections.Add(connection);
        return connection;
    }

    /// <summary>Collects <c>tf.event</c> payloads of a connection.</summary>
    public static Channel<JsonElement> Events(HubConnection connection)
    {
        var events = Channel.CreateUnbounded<JsonElement>();
        connection.On<JsonElement>(RealtimeWireFormat.EventMethod, payload => events.Writer.TryWrite(payload.Clone()));
        return events;
    }

    /// <summary>The next event of <paramref name="type"/> (others are skipped), within 10 seconds.</summary>
    public static async Task<JsonElement> NextAsync(Channel<JsonElement> events, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var payload = await events.Reader.ReadAsync(timeout.Token);
            if (payload.GetProperty("type").GetString() == type)
                return payload;
        }
    }

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public async Task DispatchAsync()
    {
        for (var run = 0; run < 20; run++)
        {
            using var serviceScope = App.Services.CreateScope();
            var result = await serviceScope.ServiceProvider.GetRequiredService<ConversationOutboxDispatcher>().RunOnceAsync();
            if (result.Handled + result.Retried + result.DeadLettered + result.Lost == 0)
                return;
        }
    }

    public async Task<ChannelReplyResult> ReplyAsync(string conversation, string body)
    {
        using var serviceScope = App.Services.CreateScope();
        return await serviceScope.ServiceProvider.GetRequiredService<IChannelReplyService>().ReplyAsync(new UserPrincipal(Scope, new UserId("agent")),
            new ConversationId(conversation), new OutgoingMessageDraft(body));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in connections)
            await connection.DisposeAsync();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
