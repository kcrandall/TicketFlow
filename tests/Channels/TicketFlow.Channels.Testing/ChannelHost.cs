using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Testing;

/// <summary>
/// A composed TicketFlow host for channel tests: Access, workspaces, contacts, conversations and channels. Stores default to the
/// in-memory implementations; pass <c>stores</c> to use others (for example PostgreSQL). Seeded principals: <c>owner</c> (Owner),
/// <c>agent</c> (Agent), <c>viewer</c> (Viewer) and the API client <c>channels</c> (custom role: conversation create/ingest, contact
/// read/create) used as the channel worker. Seeded inboxes: <c>email-inbox</c> (email/cfg-email), <c>wa-inbox</c>
/// (whatsapp/cfg-wa), <c>sms-inbox</c> (sms/cfg-sms) with endpoints. The outbox dispatcher's consumers are registered.
/// </summary>
public sealed class ChannelHost : IAsyncDisposable
{
    private readonly IServiceScope scope;
    private int nextGrant;

    private ChannelHost(ServiceProvider provider, FakeTimeProvider clock, TicketScope ticketScope)
    {
        Provider = provider;
        Clock = clock;
        Scope = ticketScope;
        scope = provider.CreateScope();
        Services = scope.ServiceProvider;
    }

    public ServiceProvider Provider { get; }
    public IServiceProvider Services { get; }
    public FakeTimeProvider Clock { get; }
    public TicketScope Scope { get; }

    public IInboundChannelPipeline Pipeline => Services.GetRequiredService<IInboundChannelPipeline>();
    public IChannelReplyService Replies => Services.GetRequiredService<IChannelReplyService>();
    public IChannelDeliveryIngestor Deliveries => Services.GetRequiredService<IChannelDeliveryIngestor>();
    public IChannelEndpointService Endpoints => Services.GetRequiredService<IChannelEndpointService>();
    public IConversationService Conversations => Services.GetRequiredService<IConversationService>();
    public IContactService Contacts => Services.GetRequiredService<IContactService>();
    public ConversationOutboxDispatcher Outbox => Services.GetRequiredService<ConversationOutboxDispatcher>();
    public IChannelConversationStateStore States => Services.GetRequiredService<IChannelConversationStateStore>();
    public IChannelThreadStore Threads => Services.GetRequiredService<IChannelThreadStore>();
    public IChannelDispatchStore Dispatches => Services.GetRequiredService<IChannelDispatchStore>();

    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public UserPrincipal Agent => TestPrincipals.User(Scope, "agent");
    public UserPrincipal Viewer => TestPrincipals.User(Scope, "viewer");
    public ApiClientPrincipal Worker => TestPrincipals.ApiClient(Scope, "channels");

    public static async Task<ChannelHost> CreateAsync(
        Action<IServiceCollection>? configure = null,
        Action<TicketFlowBuilder>? stores = null,
        Action<ChannelsOptions>? channelOptions = null,
        TicketScope? scope = null,
        Func<IServiceProvider, Task>? initialize = null)
    {
        var clock = TestClock.Create();
        var ticketScope = scope ?? TestScopes.Create();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.Configure<ConversationOutboxDispatcherOptions>(options =>
        {
            options.InitialRetryDelay = TimeSpan.FromSeconds(1);
            options.MaximumRetryDelay = TimeSpan.FromSeconds(4);
        });
        var builder = services.AddTicketFlow()
            .AddAccessControl()
            .AddWorkspaces()
            .AddContacts()
            .AddConversations()
            .AddChannels(options =>
            {
                options.WorkerPrincipal = target => new ApiClientPrincipal(target, new ApiClientId("channels"));
                channelOptions?.Invoke(options);
            });
        if (stores is null)
            builder.AddInMemoryAccessStores().AddInMemoryWorkspaceStores().AddInMemoryContactStores().AddInMemoryConversationStores().AddInMemoryChannelStores();
        else
            stores(builder);
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        if (initialize is not null)
            await initialize(provider);
        var host = new ChannelHost(provider, clock, ticketScope);
        await host.SeedAsync();
        return host;
    }

    private async Task SeedAsync()
    {
        await GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
        await GrantAsync(new UserId("viewer"), BuiltInRoles.Viewer);
        var worker = new AccessRole(new RoleId("channel-worker"), "Channel worker",
            [ConversationPermissions.Create, ConversationPermissions.Ingest, ContactPermissions.Read, ContactPermissions.Create]);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await Services.GetRequiredService<IAccessRoleStore>().CreateAsync(Scope, worker)).Value);
        await GrantAsync(new ApiClientId("channels"), worker);

        var inboxes = Services.GetRequiredService<IInboxStore>();
        foreach (var (inbox, kind, configuration) in new[] { ("email-inbox", "email", "cfg-email"), ("wa-inbox", "whatsapp", "cfg-wa"), ("sms-inbox", "sms", "cfg-sms") })
        {
            Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await inboxes.CreateAsync(Scope,
                new Inbox(new InboxId(inbox), inbox, new ChannelBinding(new ChannelKind(kind), configuration)))).Value);
            Assert.IsInstanceOfType<TicketValue<StoredChannelEndpoint>>((await Endpoints.CreateAsync(Owner,
                new ChannelEndpoint(new ChannelKind(kind), configuration, new InboxId(inbox), $"{kind}:{configuration}"))).Value);
        }
        await Outbox.RegisterAsync();
    }

    public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantScope? grantScope = null)
    {
        var grant = new AccessGrant(new GrantId($"channel-seed-{nextGrant++:D4}"), AccessPartition.From(Scope), subject, role.Id,
            grantScope ?? new WorkspaceGrantScope(Scope.WorkspaceId), GrantEffect.Allow, null);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await Services.GetRequiredService<IAccessGrantStore>().CreateAsync(grant)).Value);
    }

    /// <summary>
    /// Runs the conversation outbox dispatcher until a run claims nothing (each run delivers at most one entry per conversation and
    /// consumer, because later entries wait for earlier ones) and returns the summed counts.
    /// </summary>
    public async Task<ConversationOutboxDispatchResult> DispatchAsync()
    {
        int handled = 0, retried = 0, deadLettered = 0, lost = 0;
        for (var run = 0; run < 50; run++)
        {
            var result = await Outbox.RunOnceAsync();
            if (result.Handled + result.Retried + result.DeadLettered + result.Lost == 0)
                break;
            handled += result.Handled;
            retried += result.Retried;
            deadLettered += result.DeadLettered;
            lost += result.Lost;
        }
        return new ConversationOutboxDispatchResult(handled, retried, deadLettered, lost);
    }

    public InboundChannelMessage Email(string messageId, string from = "ada@example.com", string body = "Hello", string? inReplyTo = null,
        IEnumerable<string>? references = null, string subject = "Order 42", DateTimeOffset? receivedAt = null) =>
        new(Scope, new ChannelKind("email"), "cfg-email", messageId, new ChannelSenderEvidence(ContactIdentityKind.Email, from, verified: false),
            receivedAt ?? Clock.GetUtcNow(), bodyText: body, inReplyTo: inReplyTo, references: references, subject: subject, senderDisplayName: "Ada");

    public InboundChannelMessage WhatsApp(string messageId, string from = "+15550001111", string body = "Hi", DateTimeOffset? receivedAt = null) =>
        new(Scope, new ChannelKind("whatsapp"), "cfg-wa", messageId, new ChannelSenderEvidence(ContactIdentityKind.WhatsApp, from, verified: true),
            receivedAt ?? Clock.GetUtcNow(), bodyText: body, externalThreadId: from);

    public static T Is<T>(object? value)
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value!;
    }

    public async ValueTask DisposeAsync()
    {
        scope.Dispose();
        await Provider.DisposeAsync();
    }
}
