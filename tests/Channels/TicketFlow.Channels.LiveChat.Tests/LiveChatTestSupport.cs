using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Channels.LiveChat.StoreContracts;
using TicketFlow.Channels.Testing;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Realtime;
using TicketFlow.Realtime.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.LiveChat.Tests;

/// <summary>
/// A channel host plus realtime and live chat: inbox <c>chat-inbox</c> bound to livechat/cfg-chat with endpoint address (widget key)
/// <c>wk_demo</c> and the contract widget (origins https://shop.example.com and http://localhost:5173; name required).
/// </summary>
internal sealed class LiveChatHost : IAsyncDisposable
{
    public const string Origin = "https://shop.example.com";
    public const string WidgetKey = "wk_demo";
    public static readonly byte[] Secret = Encoding.UTF8.GetBytes("an-inbox-secret-of-at-least-32-bytes!!");

    private LiveChatHost(ChannelHost host, FakeSearch search, FakeTokenValidator validator, InMemoryAttachmentStore attachments)
    {
        Host = host;
        Search = search;
        TokenValidator = validator;
        Attachments = attachments;
    }

    public ChannelHost Host { get; }
    public TicketScope Scope => Host.Scope;
    public AccessPartition Partition => AccessPartition.From(Host.Scope);
    public ILiveChatVisitorService Visitors => Host.Services.GetRequiredService<ILiveChatVisitorService>();
    public ILiveChatWidgetService Widgets => Host.Services.GetRequiredService<ILiveChatWidgetService>();
    public IRealtimeSubscriptions Registry => Host.Services.GetRequiredService<IRealtimeSubscriptions>();
    public FakeSearch Search { get; }
    public FakeTokenValidator TokenValidator { get; }
    public InMemoryAttachmentStore Attachments { get; }

    public static async Task<LiveChatHost> CreateAsync(LiveChatWidget? widget = null, Action<LiveChatOptions>? options = null, bool seedWidget = true)
    {
        var search = new FakeSearch();
        var validator = new FakeTokenValidator();
        var attachments = new InMemoryAttachmentStore();
        var host = await ChannelHost.CreateAsync(configure: services =>
        {
            new TicketFlowBuilder(services).AddRealtime().AddLiveChat(options).AddInMemoryLiveChatStores();
            services.AddSingleton<IHelpCenterSearch>(search);
            services.AddSingleton<IVisitorTokenValidator>(validator);
            services.AddSingleton<IAttachmentStore>(attachments);
            services.AddSingleton<ILiveChatIdentitySecretResolver>(new FixedSecret());
        });
        var created = new LiveChatHost(host, search, validator, attachments);
        var inboxes = host.Services.GetRequiredService<IInboxStore>();
        Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await inboxes.CreateAsync(host.Scope,
            new Inbox(new InboxId("chat-inbox"), "Chat", new ChannelBinding(new ChannelKind("livechat"), "cfg-chat"), greetingMessage: "Inbox greeting", awayMessage: "Inbox away"))).Value);
        Assert.IsInstanceOfType<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(new ChannelKind("livechat"), "cfg-chat", new InboxId("chat-inbox"), WidgetKey))).Value);
        if (seedWidget)
            Assert.IsInstanceOfType<TicketValue<StoredLiveChatWidget>>((await created.Widgets.CreateAsync(host.Owner, widget ?? LiveChatStoreContract.Widget())).Value);
        return created;
    }

    public async Task<VisitorSessionGrant> StartAsync(bool prechat = true)
    {
        var grant = Value(await Visitors.StartSessionAsync(Partition, WidgetKey, Origin));
        if (!prechat)
            return grant;
        var context = Value(await Visitors.SubmitPrechatAsync(grant.Context, new Dictionary<string, string> { ["name"] = "Ada", ["email"] = "Ada@Example.com", ["order"] = "A-42" }));
        return grant with { Context = context };
    }

    public RecordingConnection ConnectVisitor(VisitorSessionContext context, string id)
    {
        var connection = new RecordingConnection(id, new VisitorSubscriber(context.Scope, context.Session.Session.Id), Idle);
        Assert.IsInstanceOfType<RealtimeConnected>(Registry.Connect(connection).Value);
        Assert.IsInstanceOfType<RealtimeSubscribed>(Registry.SubscribeAsync(id, new VisitorSessionTopic(context.Scope, context.Session.Session.Id)).GetAwaiter().GetResult().Value);
        return connection;
    }

    public async Task<RecordingConnection> ConnectAgentAsync(PrincipalRef principal, ConversationId conversation, string id)
    {
        var connection = new RecordingConnection(id, new AgentSubscriber(principal), Idle);
        Assert.IsInstanceOfType<RealtimeConnected>(Registry.Connect(connection).Value);
        Assert.IsInstanceOfType<RealtimeSubscribed>((await Registry.SubscribeAsync(id, new ConversationTopic(Scope, conversation))).Value);
        return connection;
    }

    public Task Idle() => Host.Services.GetRequiredService<RealtimeSubscriptionRegistry>().IdleAsync();

    public static T Value<T>(TicketResult<T, LiveChatRefused> result)
    {
        if (result.Value is LiveChatRefused refused)
            Assert.Fail($"Expected a value but got {refused.Reason}: {string.Join("; ", refused.Errors)}");
        return ((TicketValue<T>)result.Value!).Value;
    }

    public static LiveChatRefusal Refusal<T>(TicketResult<T, LiveChatRefused> result) => ChannelHost.Is<LiveChatRefused>(result.Value).Reason;

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    internal sealed class FixedSecret : ILiveChatIdentitySecretResolver
    {
        public Task<ReadOnlyMemory<byte>?> ResolveAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<byte>?>(configurationId == "cfg-chat" ? Secret : null);
    }

    internal sealed class FakeSearch : IHelpCenterSearch
    {
        public List<HelpCenterSearchQuery> Queries { get; } = [];

        public Task<TicketResult<IReadOnlyList<ArticleSnippet>>> SearchAsync(PrincipalRef actor, HelpCenterSearchQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ArticleSnippet>> SearchPublicAsync(TicketScope scope, HelpCenterSearchQuery query, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            return Task.FromResult<IReadOnlyList<ArticleSnippet>>([new ArticleSnippet(new ArticleId("reset"), query.Portal, query.Locale, "Reset your password", new HelpCenterSlug("reset"),
                null, "How to reset", 1.0, 1)]);
        }
    }

    internal sealed class FakeTokenValidator : IVisitorTokenValidator
    {
        public Task<VisitorTokenValidation> ValidateAsync(TicketScope scope, string configurationId, string token, CancellationToken cancellationToken = default) =>
            Task.FromResult<VisitorTokenValidation>(token == "good-token" ? new VisitorTokenAccepted("idf", "subject-7", "Grace") : VisitorTokenRejected.Instance);
    }
}
