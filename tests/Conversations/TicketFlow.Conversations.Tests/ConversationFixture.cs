using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Conversations.Tests;

/// <summary>
/// In-memory access engine, workspaces, contacts and the conversation service. Seeded: inboxes <c>email</c> (enabled) and
/// <c>closed-inbox</c> (disabled), team <c>billing</c>, contacts <c>ada</c> and <c>grace</c>. Principals: <c>owner</c> (Owner),
/// <c>agent</c> (Agent), <c>light</c> (LightAgent), <c>viewer</c> (Viewer), API client <c>channel</c> (custom role: create, ingest).
/// Grants bypass the administrator.
/// </summary>
internal sealed class ConversationFixture : IDisposable
{
    private readonly IServiceScope requestScope;
    private int nextGrant;

    private ConversationFixture(ServiceProvider provider, FakeTimeProvider clock)
    {
        Provider = provider;
        Clock = clock;
        requestScope = provider.CreateScope();
        Conversations = requestScope.ServiceProvider.GetRequiredService<IConversationService>();
        Definitions = requestScope.ServiceProvider.GetRequiredService<IConversationAttributeDefinitionService>();
        ContactsService = requestScope.ServiceProvider.GetRequiredService<IContactService>();
        Follower = requestScope.ServiceProvider.GetRequiredService<ConversationContactMergeFollower>();
        Store = provider.GetRequiredService<InMemoryConversationStore>();
        Grants = provider.GetRequiredService<IAccessGrantStore>();
        Roles = provider.GetRequiredService<IAccessRoleStore>();
        Policies = provider.GetRequiredService<IAccessFieldPolicyStore>();
        Catalog = provider.GetRequiredService<IPermissionCatalog>();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public IConversationService Conversations { get; }
    public IConversationAttributeDefinitionService Definitions { get; }
    public IContactService ContactsService { get; }
    public ConversationContactMergeFollower Follower { get; }
    public InMemoryConversationStore Store { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public IAccessFieldPolicyStore Policies { get; }
    public IPermissionCatalog Catalog { get; }
    public TicketScope Scope { get; } = TestScopes.Create();

    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public UserPrincipal Agent => TestPrincipals.User(Scope, "agent");
    public UserPrincipal Light => TestPrincipals.User(Scope, "light");
    public UserPrincipal Viewer => TestPrincipals.User(Scope, "viewer");
    public ApiClientPrincipal Channel => TestPrincipals.ApiClient(Scope, "channel");

    public static async Task<ConversationFixture> CreateAsync(Action<ConversationsOptions>? options = null, Action<IServiceCollection>? configure = null)
    {
        var clock = TestClock.Create();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        configure?.Invoke(services);
        services.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddWorkspaces()
            .AddInMemoryWorkspaceStores()
            .AddContacts()
            .AddInMemoryContactStores()
            .AddConversations(options)
            .AddConversations()
            .AddInMemoryConversationStores();
        var fixture = new ConversationFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock);
        await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await fixture.GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("light"), BuiltInRoles.LightAgent);
        await fixture.GrantAsync(new UserId("viewer"), BuiltInRoles.Viewer);
        var channel = await fixture.CustomRoleAsync("channel", ConversationPermissions.Create, ConversationPermissions.Ingest);
        await fixture.GrantAsync(new ApiClientId("channel"), channel);

        var inboxes = provider(fixture).GetRequiredService<IInboxStore>();
        await inboxes.CreateAsync(fixture.Scope, new Inbox(new InboxId("email"), "Email", new ChannelBinding(new ChannelKind("email"), "cfg-email")));
        await inboxes.CreateAsync(fixture.Scope, new Inbox(new InboxId("closed-inbox"), "Old", new ChannelBinding(new ChannelKind("email"), "cfg-old"), enabled: false));
        await provider(fixture).GetRequiredService<ITeamStore>().CreateAsync(fixture.Scope, new Team(new TeamId("billing"), "Billing"));
        foreach (var contact in new[] { "ada", "grace" })
            Assert.IsInstanceOfType<ContactApplied<ContactView>>((await fixture.ContactsService.CreateAsync(fixture.Owner,
                new ContactDraft(new ContactProfile(contact), Id: new ContactId(contact)))).Value);
        return fixture;

        static ServiceProvider provider(ConversationFixture fixture) => fixture.Provider;
    }

    public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantEffect effect = GrantEffect.Allow, AccessCondition? condition = null, GrantScope? scope = null)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), subject, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId), effect, condition);
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

    public async Task DefineAsync(ConversationAttributeDefinition definition) =>
        Assert.IsInstanceOfType<ConversationValue<StoredConversationAttributeDefinition>>((await Definitions.CreateAsync(Owner, definition)).Value);

    public async Task<ConversationApplied> StartAsync(string id, string contact = "ada", string? externalId = null, string body = "Help please",
        ChannelThreadRef? thread = null, TeamId? team = null, UserId? assignee = null, IReadOnlyList<LabelId>? labels = null)
    {
        var result = await Conversations.StartAsync(Channel, new StartConversationRequest(new InboxId("email"), new ContactId(contact), thread,
            Labels: labels, Team: team, Assignee: assignee, InitialMessage: new IncomingMessageDraft(body, ExternalId: externalId), Id: new ConversationId(id)));
        return Applied(result);
    }

    public static ConversationApplied Applied(ConversationCommandResult result) => result switch
    {
        ConversationApplied applied => applied,
        ConversationNotFound => throw new AssertFailedException("Expected applied but was not found."),
        ConversationConflict conflict => throw new AssertFailedException($"Expected applied but was conflict at {conflict.CurrentRevision}."),
        ConversationRefused refused => throw new AssertFailedException($"Expected applied but was refused: {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public static string Describe(ConversationCommandResult result) => result switch
    {
        ConversationApplied applied => applied.Replayed ? $"replayed:{applied.Revision}" : $"applied:{applied.Revision}",
        ConversationNotFound => "not-found",
        ConversationConflict conflict => $"conflict:{conflict.CurrentRevision}",
        ConversationRefused refused => $"refused:{refused.Reason}",
    };

    public static ConversationView View(ConversationReadResult result) => result switch
    {
        ConversationView view => view,
        ConversationNotFound => throw new AssertFailedException("Expected a view but was not found."),
        ConversationRefused refused => throw new AssertFailedException($"Expected a view but was refused: {refused.Reason}"),
    };

    public static TimelinePage Page(TimelineReadResult result) => result switch
    {
        TimelinePage page => page,
        ConversationNotFound => throw new AssertFailedException("Expected a page but was not found."),
        ConversationRefused refused => throw new AssertFailedException($"Expected a page but was refused: {refused.Reason}"),
    };

    public static ConversationListPage List(ConversationListResult result) => result switch
    {
        ConversationListPage page => page,
        ConversationRefused refused => throw new AssertFailedException($"Expected a list but was refused: {refused.Reason}"),
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }
}
