using TicketFlow.Access;
using TicketFlow.Channels;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.SampleHost;

/// <summary>
/// Idempotent demo provisioning (existing records are left unchanged): workspace settings, a live-chat inbox with widget and channel
/// endpoint (<see cref="WidgetKey"/>), the <c>tier1</c> team, users <c>admin</c> (Owner), <c>agent1</c> and <c>agent2</c> (Agent),
/// grant-holding API clients for the channel worker, routing engine, SLA tracker, automation and the MCP agent, a public help-center portal with one
/// published article and a CSAT survey. Grants and stores are written directly (trusted provisioning), feature content through the
/// authorized services as <c>admin</c>.
/// </summary>
public static class SampleSeeder
{
    public const string WidgetKey = "wk_demo";
    public const string PortalSlug = "help";
    public static ApiClientId ChannelWorker { get; } = new("channels");

    /// <summary>
    /// The MCP agent's API client (<c>X-Dev-Api-Key: mcp-assistant</c>, mapped with the IdentityFlow API client prefix <c>idf:</c>),
    /// granted the built-in Agent role.
    /// </summary>
    public static ApiClientId McpAssistant { get; } = new("idf:mcp-assistant");

    private static readonly InboxId ChatInbox = new("web-chat");

    public static async Task SeedAsync(IServiceProvider root, SampleSettings settings, CancellationToken cancellationToken = default)
    {
        await using var serviceScope = root.CreateAsyncScope();
        var services = serviceScope.ServiceProvider;
        var scope = settings.Scope;
        var partition = settings.Partition;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TicketFlow.SampleHost.Seed");

        await services.GetRequiredService<IWorkspaceSettingsStore>().CreateAsync(scope, new WorkspaceSettings("Acme Support", "en", "UTC"), cancellationToken);

        var roles = services.GetRequiredService<IAccessRoleStore>();
        var workers = new (string Role, ApiClientId Client, Permission[] Permissions)[]
        {
            ("channel-worker", ChannelWorker, [ConversationPermissions.Create, ConversationPermissions.Ingest, ContactPermissions.Read, ContactPermissions.Create]),
            ("routing-engine", new ApiClientId("ticketflow-routing"), [ConversationPermissions.Read, ConversationPermissions.Assign]),
            ("sla-worker", new ApiClientId("sla-worker"), [ConversationPermissions.Read, ConversationPermissions.Manage]),
            ("automation-worker", new ApiClientId("automation-worker"), [ConversationPermissions.Read, ConversationPermissions.Assign, ConversationPermissions.Manage,
                ConversationPermissions.Reply, ConversationPermissions.Resolve, ConversationPermissions.NoteWrite, ContactPermissions.Read]),
        };
        var grants = services.GetRequiredService<IAccessGrantStore>();
        async Task GrantAsync(string id, GrantSubject subject, RoleId role) =>
            await grants.CreateAsync(new AccessGrant(new GrantId("seed-" + id), partition, subject, role, new WorkspaceGrantScope(scope.WorkspaceId)), cancellationToken);
        foreach (var (role, client, permissions) in workers)
        {
            await roles.CreateAsync(scope, new AccessRole(new RoleId(role), role, permissions), cancellationToken);
            await GrantAsync(role, client, new RoleId(role));
        }
        await GrantAsync("admin", new UserId("admin"), BuiltInRoles.Owner.Id);
        await GrantAsync("agent1", new UserId("agent1"), BuiltInRoles.Agent.Id);
        await GrantAsync("agent2", new UserId("agent2"), BuiltInRoles.Agent.Id);
        await GrantAsync("mcp-assistant", McpAssistant, BuiltInRoles.Agent.Id);

        await services.GetRequiredService<IInboxStore>().CreateAsync(scope, new Inbox(ChatInbox, "Website chat", new ChannelBinding(new ChannelKind("livechat"), "cfg-web"),
            greetingMessage: "Hi! How can we help today?"), cancellationToken);
        await services.GetRequiredService<ITeamStore>().CreateAsync(scope, new Team(new TeamId("tier1"), "Tier 1", "First-line support",
            [new UserId("agent1"), new UserId("agent2")]), cancellationToken);
        await services.GetRequiredService<ILiveChatWidgetStore>().CreateAsync(scope, new LiveChatWidget("cfg-web", ChatInbox, settings.WidgetOrigins,
            greetingMessage: "Hi! How can we help today?"), cancellationToken);
        await services.GetRequiredService<IChannelEndpointStore>().CreateAsync(scope, new ChannelEndpoint(new ChannelKind("livechat"), "cfg-web", ChatInbox, WidgetKey), cancellationToken);

        var admin = new UserPrincipal(scope, new UserId("admin"));
        var helpCenter = services.GetRequiredService<IHelpCenterService>();
        await helpCenter.CreatePortalAsync(admin, new Portal(new PortalId("help"), new HelpCenterSlug(PortalSlug), "Acme Help", new HelpCenterLocale("en"),
            visibility: PortalVisibility.Public), cancellationToken);
        if (await helpCenter.CreateArticleAsync(admin, new Article(new ArticleId("reset-password"), new PortalId("help"),
            [new ArticleTranslation(new HelpCenterLocale("en"), "Reset your password", new HelpCenterSlug("reset-password"),
                "Open **Settings → Security** and choose *Forgot password*. We email you a reset link.", "How to reset your password", ["account"])]), cancellationToken)
            is TicketValue<StoredArticle> created)
            await helpCenter.PublishArticleAsync(admin, created.Value.Id, created.Value.Revision, cancellationToken);

        await services.GetRequiredService<ISurveyService>().CreateSurveyAsync(admin, new SurveyDefinition(new SurveyId("csat"), "Customer satisfaction", SurveyKind.Csat,
            new SurveyLocale("en"), [new SurveyQuestion(new SurveyLocale("en"), "How satisfied are you with our support?", "Anything we could do better?")],
            new SurveyTriggerPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromDays(14), TimeSpan.FromDays(7)), ChatInbox), cancellationToken);

        await services.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync(cancellationToken);
        await SampleDemoData.SeedAsync(services, settings, cancellationToken);
        logger.LogInformation("Seeded the sample workspace (widget key {WidgetKey}; users admin, agent1, agent2).", WidgetKey);
    }
}
