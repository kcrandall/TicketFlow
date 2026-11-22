using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Assist.Providers.Claude;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Assist.Tests;

/// <summary>
/// In-memory access, workspaces, contacts, conversations, help center and assist. Seeded data: contact <c>ada</c> with a masked email
/// identity and a masked sensitive attribute, conversation <c>c1</c> with a masked sensitive attribute, a reply and an internal note,
/// a public portal with a published refund article and a private portal with a published internal article. The model is the
/// <see cref="ScriptedChatClient"/> (provider <c>scripted</c>) unless <see cref="CreateAsync"/> is given a Claude HTTP handler.
/// </summary>
internal sealed class AssistFixture : IDisposable
{
    public const string SecretPin = "PIN-4242-SECRET";
    public const string SecretNationalId = "NID-987-65-4321";
    public const string SecretEmail = "ada.secret@example.com";
    public const string NoteText = "Internal: customer is a VIP, approve refund quietly";
    public const string CustomerMessage = "My card was charged twice for the refund order";
    public static readonly HelpCenterLocale English = new("en");
    public static readonly ConversationId C1 = new("c1");

    private readonly IServiceScope requestScope;
    private int nextGrant;

    private AssistFixture(ServiceProvider provider, FakeTimeProvider clock, ScriptedChatClient model)
    {
        Provider = provider;
        Clock = clock;
        Model = model;
        requestScope = provider.CreateScope();
        var services = requestScope.ServiceProvider;
        Assist = services.GetRequiredService<IAssistService>();
        Administration = services.GetRequiredService<IAssistAdministration>();
        Conversations = services.GetRequiredService<IConversationService>();
        Contacts = services.GetRequiredService<IContactService>();
        HelpCenter = services.GetRequiredService<IHelpCenterService>();
        Store = services.GetRequiredService<IAssistStore>();
        Grants = services.GetRequiredService<IAccessGrantStore>();
        Roles = services.GetRequiredService<IAccessRoleStore>();
        Policies = services.GetRequiredService<IAccessFieldPolicyStore>();
        ScriptedProvider = services.GetServices<IAssistChatProvider>().OfType<ScriptedChatProvider>().FirstOrDefault();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public ScriptedChatClient Model { get; }
    public ScriptedChatProvider? ScriptedProvider { get; }
    public IAssistService Assist { get; }
    public IAssistAdministration Administration { get; }
    public IConversationService Conversations { get; }
    public IContactService Contacts { get; }
    public IHelpCenterService HelpCenter { get; }
    public IAssistStore Store { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public IAccessFieldPolicyStore Policies { get; }
    public TicketScope Scope { get; } = TestScopes.Create();
    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public ApiClientPrincipal Channel => TestPrincipals.ApiClient(Scope, "channel");

    public static readonly Permission[] AgentPermissions =
    [
        ConversationPermissions.Read, ConversationPermissions.Manage, ConversationPermissions.NoteRead, AccessPermissions.ContactRead, HelpCenterPermissions.Read, AssistPermissions.Use,
    ];

    public static async Task<AssistFixture> CreateAsync(Action<AssistOptions>? configure = null, HttpMessageHandler? claudeHandler = null)
    {
        var clock = TestClock.Create();
        var model = new ScriptedChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        var builder = services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddHelpCenter().AddInMemoryHelpCenterStores()
            .AddAssist(configure).AddInMemoryAssistStores();
        if (claudeHandler is null)
        {
            services.AddSingleton<IAssistChatProvider>(new ScriptedChatProvider(model));
            services.Configure<AssistOptions>(options =>
            {
                foreach (var id in new[] { "scripted-small", "scripted-large" })
                    options.Models.Add(new AssistModelDefinition(id, ScriptedChatProvider.ProviderName, id + "-v1", AssistModelCapabilities.StructuredOutput));
                foreach (var feature in Enum.GetValues<AssistFeature>())
                    options.FeatureModels.TryAdd(feature, feature is AssistFeature.Triage or AssistFeature.SentimentAndIntent ? "scripted-small" : "scripted-large");
            });
        }
        else
        {
            services.AddSingleton(new ClaudeAssistProvider(new ClaudeAssistOptions { ApiKey = "test-key-not-real", MaxRetries = 0 }, claudeHandler, clock));
            builder.AddClaudeAssist();
        }
        var fixture = new AssistFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock, model);
        await fixture.SeedAsync();
        return fixture;
    }

    private async Task SeedAsync()
    {
        await GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await GrantAsync(new ApiClientId("channel"), await CustomRoleAsync("channel", ConversationPermissions.Create, ConversationPermissions.Ingest));
        var services = Provider;
        await services.GetRequiredService<IInboxStore>().CreateAsync(Scope, new Inbox(new InboxId("email"), "Email", new ChannelBinding(new ChannelKind("email"), "cfg-email")));
        await services.GetRequiredService<ITeamStore>().CreateAsync(Scope, new Team(new TeamId("billing"), "Billing"));

        using (var scope = Provider.CreateScope())
        {
            var contactDefinitions = scope.ServiceProvider.GetRequiredService<IContactAttributeDefinitionService>();
            Assert.IsInstanceOfType<ContactApplied<StoredContactAttributeDefinition>>((await contactDefinitions.CreateAsync(Owner, new ContactAttributeDefinition("national-id", ContactAttributeType.Text, sensitive: true))).Value);
            Assert.IsInstanceOfType<ContactApplied<StoredContactAttributeDefinition>>((await contactDefinitions.CreateAsync(Owner, new ContactAttributeDefinition("tier", ContactAttributeType.Text))).Value);
            var conversationDefinitions = scope.ServiceProvider.GetRequiredService<IConversationAttributeDefinitionService>();
            Assert.IsInstanceOfType<ConversationValue<StoredConversationAttributeDefinition>>((await conversationDefinitions.CreateAsync(Owner, new ConversationAttributeDefinition("account-pin", ConversationAttributeType.Text, sensitive: true))).Value);
            Assert.IsInstanceOfType<ConversationValue<StoredConversationAttributeDefinition>>((await conversationDefinitions.CreateAsync(Owner, new ConversationAttributeDefinition("plan", ConversationAttributeType.Text))).Value);
        }
        await PolicyAsync(ContactFieldPaths.SensitivePolicy(ContactFieldPaths.Email));
        await PolicyAsync(ContactFieldPaths.SensitivePolicy(ContactFieldPaths.Attribute("national-id")));
        await PolicyAsync(ConversationFieldPaths.SensitivePolicy(ConversationFieldPaths.Attribute("account-pin")));

        Assert.IsInstanceOfType<ContactApplied<ContactView>>((await Contacts.CreateAsync(Owner, new ContactDraft(new ContactProfile("Ada Lovelace"),
            [new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Email, SecretEmail), new IdentityClaimed("widget", Clock.GetUtcNow()))],
            new Dictionary<string, ContactAttributeValue> { ["national-id"] = new TextAttributeValue(SecretNationalId), ["tier"] = new TextAttributeValue("platinum") },
            Id: new ContactId("ada")))).Value);

        Applied(await Conversations.StartAsync(Channel, new StartConversationRequest(new InboxId("email"), new ContactId("ada"),
            InitialMessage: new IncomingMessageDraft(CustomerMessage), Id: C1)));
        Applied(await Conversations.UpdateAttributesAsync(Owner, C1, new Dictionary<string, ConversationAttributeValue?>
        {
            ["account-pin"] = new ConversationTextValue(SecretPin),
            ["plan"] = new ConversationTextValue("gold"),
        }));
        Applied(await Conversations.ReplyAsync(Owner, C1, new OutgoingMessageDraft("Sorry about that, checking now.")));
        Applied(await Conversations.AddNoteAsync(Owner, C1, new NoteDraft(NoteText)));

        Value(await HelpCenter.CreatePortalAsync(Owner, new Portal(new PortalId("public"), new HelpCenterSlug("help"), "Help", English, [], PortalVisibility.Public)));
        Value(await HelpCenter.CreatePortalAsync(Owner, new Portal(new PortalId("internal"), new HelpCenterSlug("internal"), "Internal", English, [], PortalVisibility.Private)));
        await ArticleAsync("refunds", "public", "Refunds for duplicate charges", "If my card was charged twice for the refund order, the duplicate refund arrives in 5 days.");
        await ArticleAsync("playbook", "internal", "Internal refund playbook", "My card was charged twice for the refund order: escalation steps for staff only.");
    }

    private async Task ArticleAsync(string id, string portal, string title, string body)
    {
        Value(await HelpCenter.CreateArticleAsync(Owner, new Article(new ArticleId(id), new PortalId(portal),
            [new ArticleTranslation(English, title, new HelpCenterSlug(id), body, title, [])])));
        Value(await HelpCenter.PublishArticleAsync(Owner, new ArticleId(id), 1));
    }

    public async Task<UserPrincipal> UserWithAsync(string id, params Permission[] permissions)
    {
        var role = await CustomRoleAsync("role-" + id, permissions);
        await GrantAsync(new UserId(id), role);
        return TestPrincipals.User(Scope, id);
    }

    public Task<UserPrincipal> AgentAsync(string id = "agent") => UserWithAsync(id, AgentPermissions);

    public async Task GrantAsync(GrantSubject subject, AccessRole role)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), subject, role.Id, new WorkspaceGrantScope(Scope.WorkspaceId));
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await Grants.CreateAsync(grant)).Value);
    }

    public async Task<AccessRole> CustomRoleAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId(id), id, permissions);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        return role;
    }

    public async Task PolicyAsync(AccessFieldPolicy policy) =>
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessFieldPolicy>>((await Policies.CreateAsync(Scope, policy)).Value);

    public async Task<TimelinePage> TimelineAsync() => (TimelinePage)(await Conversations.ReadTimelineAsync(Owner, C1, 0, 200)).Value!;

    public static ConversationApplied Applied(ConversationCommandResult result) => result switch
    {
        ConversationApplied applied => applied,
        ConversationNotFound => throw new AssertFailedException("not found"),
        ConversationConflict conflict => throw new AssertFailedException($"conflict {conflict.CurrentRevision}"),
        ConversationRefused refused => throw new AssertFailedException($"refused {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public static T Value<T>(HelpCenterResult<T> result) => result switch
    {
        HelpCenterSucceeded<T> succeeded => succeeded.Value,
        HelpCenterRefused refused => throw new AssertFailedException($"refused {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public static AssistSucceeded<T> Succeeded<T>(AssistResult<T> result) => result switch
    {
        AssistSucceeded<T> succeeded => succeeded,
        AssistRefused refused => throw new AssertFailedException($"Expected success but was refused: {refused.Reason} {string.Join("; ", refused.Errors)}"),
        AssistProviderFailed failed => throw new AssertFailedException($"Expected success but the provider failed: {failed.Failure}"),
    };

    /// <summary>Exhaustive description of every <see cref="AssistResult{T}"/> case (no discard arm).</summary>
    public static string Describe<T>(AssistResult<T> result) => result switch
    {
        AssistSucceeded<T> succeeded => $"ok:{succeeded.Recording}",
        AssistRefused refused => $"refused:{refused.Reason}",
        AssistProviderFailed failed => $"failed:{failed.Failure}:{(failed.Retryable ? "retryable" : "final")}",
    };

    public static string Describe<T>(AssistAdminResult<T> result) => result switch
    {
        AssistValue<T> => "ok",
        AssistRefused refused => $"refused:{refused.Reason}" + (refused.CurrentRevision is { } revision ? $":{revision}" : ""),
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }
}
