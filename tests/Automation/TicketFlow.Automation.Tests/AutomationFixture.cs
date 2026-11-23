using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Automation.Tests;

/// <summary>
/// In-memory access engine, workspaces, contacts, conversations and automation. Seeded: inbox <c>email</c> (calendar <c>ny</c>,
/// Mon-Fri 09:00-17:00 America/New_York), team <c>billing</c>, contact <c>ada</c> (name Ada, locale de), attribute definitions
/// <c>plan</c> (text) and <c>ssn</c> (sensitive text, masked for everyone). Principals: <c>owner</c> (Owner), <c>agent</c> (Agent +
/// <c>macro:run</c>), <c>light</c> (LightAgent + <c>macro:run</c>), <c>viewer</c> (Viewer), API clients <c>channel</c> (create, ingest),
/// <c>sla-worker</c> (manage) and <c>automation-worker</c> (the automation client; permissions configurable).
/// </summary>
internal sealed class AutomationFixture : IDisposable
{
    public static readonly Permission[] DefaultWorkerPermissions =
    [
        ConversationPermissions.Read, ConversationPermissions.Assign, ConversationPermissions.Manage, ConversationPermissions.Reply,
        ConversationPermissions.Resolve, ConversationPermissions.NoteWrite, ContactPermissions.Read,
    ];

    private int nextGrant;

    private AutomationFixture(ServiceProvider provider, FakeTimeProvider clock)
    {
        Provider = provider;
        Clock = clock;
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public TicketScope Scope { get; } = TestScopes.Create();
    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public UserPrincipal Agent => TestPrincipals.User(Scope, "agent");
    public UserPrincipal Light => TestPrincipals.User(Scope, "light");
    public UserPrincipal Viewer => TestPrincipals.User(Scope, "viewer");
    public ApiClientPrincipal Channel => TestPrincipals.ApiClient(Scope, "channel");
    public ApiClientPrincipal SlaWorker => TestPrincipals.ApiClient(Scope, "sla-worker");
    public ApiClientPrincipal Worker => TestPrincipals.ApiClient(Scope, "automation-worker");

    public static async Task<AutomationFixture> CreateAsync(DateTimeOffset? start = null, Action<IServiceCollection>? configure = null, Permission[]? workerPermissions = null,
        Action<AutomationOptions>? options = null)
    {
        var clock = TestClock.Create(start ?? new DateTimeOffset(2026, 1, 5, 15, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        configure?.Invoke(services);
        services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddAutomation(options).AddInMemoryAutomationStores();
        var fixture = new AutomationFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock);
        await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        var macroRunner = await fixture.CustomRoleAsync("macro-runner", AutomationPermissions.MacroRun);
        await fixture.GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("agent"), macroRunner);
        await fixture.GrantAsync(new UserId("light"), BuiltInRoles.LightAgent);
        await fixture.GrantAsync(new UserId("light"), macroRunner);
        await fixture.GrantAsync(new UserId("viewer"), BuiltInRoles.Viewer);
        await fixture.GrantAsync(new ApiClientId("channel"), await fixture.CustomRoleAsync("channel", ConversationPermissions.Create, ConversationPermissions.Ingest));
        await fixture.GrantAsync(new ApiClientId("sla-worker"), await fixture.CustomRoleAsync("sla-worker", ConversationPermissions.Manage));
        await fixture.GrantAsync(new ApiClientId("automation-worker"), await fixture.CustomRoleAsync("automation-worker", workerPermissions ?? DefaultWorkerPermissions));

        await fixture.Get<IBusinessCalendarStore>().CreateAsync(fixture.Scope, new BusinessCalendar(new BusinessCalendarId("ny"), "New York", "America/New_York",
            [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                .Select(day => new WeeklyBusinessHours(day, BusinessHours.From(9, 0, 17, 0)))]));
        await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope, new Inbox(new InboxId("email"), "Email", new ChannelBinding(new ChannelKind("email"), "cfg"),
            new BusinessCalendarId("ny")));
        await fixture.Get<ITeamStore>().CreateAsync(fixture.Scope, new Team(new TeamId("billing"), "Billing"));
        var definitions = fixture.Get<IConversationAttributeDefinitionStore>();
        await definitions.CreateAsync(fixture.Scope, new ConversationAttributeDefinition("plan", ConversationAttributeType.Text));
        await definitions.CreateAsync(fixture.Scope, new ConversationAttributeDefinition("ssn", ConversationAttributeType.Text, sensitive: true));
        await fixture.InScopeAsync(async services =>
        {
            Assert.IsInstanceOfType<TicketValue<ContactView>>((await services.GetRequiredService<IContactService>().CreateAsync(fixture.Owner,
                new ContactDraft(new ContactProfile("Ada", locale: "de"), Id: new ContactId("ada")))).Value);
            await services.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync();
            return 0;
        });
        return fixture;
    }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantScope? scope = null)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), subject, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId),
            GrantEffect.Allow, null);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await Get<IAccessGrantStore>().CreateAsync(grant)).Value);
    }

    public async Task<AccessRole> CustomRoleAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId(id), id, permissions);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await Get<IAccessRoleStore>().CreateAsync(Scope, role)).Value);
        return role;
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Provider.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public async Task<ConversationOutboxDispatchResult> DispatchAsync()
    {
        int handled = 0, retried = 0, deadLettered = 0, lost = 0;
        for (var run = 0; run < 100; run++)
        {
            var result = await InScopeAsync(services => services.GetRequiredService<ConversationOutboxDispatcher>().RunOnceAsync());
            handled += result.Handled;
            retried += result.Retried;
            deadLettered += result.DeadLettered;
            lost += result.Lost;
            if (result.Handled == 0 && result.Retried == 0)
                break;
        }
        return new ConversationOutboxDispatchResult(handled, retried, deadLettered, lost);
    }

    public Task<ConversationCommandResult> CommandAsync(Func<IConversationService, Task<ConversationCommandResult>> command) =>
        InScopeAsync(services => command(services.GetRequiredService<IConversationService>()));

    public Task<T> RulesAsync<T>(Func<IAutomationRuleService, Task<T>> action) => InScopeAsync(services => action(services.GetRequiredService<IAutomationRuleService>()));

    public async Task<StoredAutomationRule> CreateRuleAsync(AutomationRule rule) =>
        Assert.IsInstanceOfType<TicketValue<StoredAutomationRule>>((await RulesAsync(service => service.CreateAsync(Owner, rule))).Value).Value;

    public async Task StartAsync(string id, string message = "Hello", IReadOnlyDictionary<string, ConversationAttributeValue>? attributes = null)
    {
        var result = await CommandAsync(service => service.StartAsync(Channel, new StartConversationRequest(new InboxId("email"), new ContactId("ada"), Attributes: attributes,
            InitialMessage: new IncomingMessageDraft(message), Id: new ConversationId(id))));
        Assert.IsInstanceOfType<ConversationApplied>(result.Value);
    }

    public async Task<Conversation> ConversationAsync(string id) => (await Get<IConversationStore>().GetAsync(Scope, new ConversationId(id)))!.Conversation;

    public Task<IReadOnlyList<TimelineEvent>> TimelineAsync(string id) => Get<IConversationStore>().ReadTimelineAsync(Scope, new ConversationId(id), 0, 200, includeNotes: true);

    public async Task<IReadOnlyList<StoredAutomationExecution>> ExecutionsAsync(string? conversation = null) =>
        (await Get<IAutomationExecutionStore>().ListAsync(Scope, new AutomationExecutionQuery(conversation: conversation is null ? null : new ConversationId(conversation), limit: 100)))
            .Executions;

    public void Dispose() => Provider.Dispose();
}
