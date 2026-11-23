using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Reporting.Tests;

/// <summary>
/// In-memory access engine, workspaces (time zone America/New_York), contacts, conversations, surveys and reporting. Seeded:
/// calendar <c>ny</c> (Mon-Fri 09:00-17:00 America/New_York), inboxes <c>email</c> (calendar ny) and <c>chat</c> (channel
/// <c>live-chat</c>, no calendar), teams <c>billing</c> and <c>support</c>, contact <c>ada</c>. Principals: <c>owner</c> (Owner),
/// agents <c>ann</c> (Agent + <c>report:view-own</c>) and <c>bob</c> (Agent), <c>supervisor</c> (Supervisor), <c>billing-lead</c>
/// (<c>report:view</c> on team billing only), API clients <c>channel</c> (create, ingest), <c>bot</c> (reply), <c>sla-worker</c> and
/// <c>forger</c> (both conversation:manage).
/// </summary>
internal sealed class ReportingFixture : IDisposable
{
    public static readonly DateTimeOffset Monday = new(2026, 3, 2, 14, 0, 0, TimeSpan.Zero);   // 09:00 in New York

    private int nextGrant;

    private ReportingFixture(ServiceProvider provider, FakeTimeProvider clock)
    {
        Provider = provider;
        Clock = clock;
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public TicketScope Scope { get; } = TestScopes.Create();
    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public UserPrincipal Ann => TestPrincipals.User(Scope, "ann");
    public UserPrincipal Bob => TestPrincipals.User(Scope, "bob");
    public UserPrincipal Supervisor => TestPrincipals.User(Scope, "supervisor");
    public UserPrincipal BillingLead => TestPrincipals.User(Scope, "billing-lead", "billing");
    public ApiClientPrincipal Channel => TestPrincipals.ApiClient(Scope, "channel");
    public ApiClientPrincipal Bot => TestPrincipals.ApiClient(Scope, "bot");
    public ApiClientPrincipal SlaWorker => TestPrincipals.ApiClient(Scope, "sla-worker");
    public ApiClientPrincipal Forger => TestPrincipals.ApiClient(Scope, "forger");

    public static async Task<ReportingFixture> CreateAsync(DateTimeOffset? start = null, Action<IServiceCollection>? configure = null, Action<ReportingOptions>? options = null)
    {
        var clock = TestClock.Create(start ?? Monday);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        configure?.Invoke(services);
        services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddInMemorySurveyStores()
            .AddReporting(options).AddInMemoryReportingStores();
        var fixture = new ReportingFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock);
        await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await fixture.GrantAsync(new UserId("ann"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("ann"), await fixture.CustomRoleAsync("own-stats", ReportPermissions.ViewOwn));
        await fixture.GrantAsync(new UserId("bob"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("supervisor"), BuiltInRoles.Supervisor);
        await fixture.GrantAsync(new UserId("billing-lead"), await fixture.CustomRoleAsync("team-reports", ReportPermissions.View),
            scope: new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        await fixture.GrantAsync(new ApiClientId("channel"), await fixture.CustomRoleAsync("channel", ConversationPermissions.Create, ConversationPermissions.Ingest));
        await fixture.GrantAsync(new ApiClientId("bot"), await fixture.CustomRoleAsync("bot", ConversationPermissions.Reply));
        var manage = await fixture.CustomRoleAsync("extensions", ConversationPermissions.Manage);
        await fixture.GrantAsync(new ApiClientId("sla-worker"), manage);
        await fixture.GrantAsync(new ApiClientId("forger"), manage);

        await fixture.Get<IWorkspaceSettingsStore>().CreateAsync(fixture.Scope, new WorkspaceSettings("Acme", "en", "America/New_York"));
        await fixture.Get<IBusinessCalendarStore>().CreateAsync(fixture.Scope, NewYork());
        await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope, new Inbox(new InboxId("email"), "Email", new ChannelBinding(new ChannelKind("email"), "cfg"), new BusinessCalendarId("ny")));
        await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope, new Inbox(new InboxId("chat"), "Chat", new ChannelBinding(new ChannelKind("live-chat"), "cfg2")));
        await fixture.Get<ITeamStore>().CreateAsync(fixture.Scope, new Team(new TeamId("billing"), "Billing"));
        await fixture.Get<ITeamStore>().CreateAsync(fixture.Scope, new Team(new TeamId("support"), "Support"));
        using (var scope = fixture.Provider.CreateScope())
        {
            Assert.IsInstanceOfType<TicketValue<ContactView>>((await scope.ServiceProvider.GetRequiredService<IContactService>().CreateAsync(fixture.Owner,
                new ContactDraft(new ContactProfile("ada"), Id: new ContactId("ada")))).Value);
            await scope.ServiceProvider.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync();
        }
        return fixture;
    }

    /// <summary>Monday-Friday 09:00-17:00 America/New_York.</summary>
    public static BusinessCalendar NewYork() => new(new BusinessCalendarId("ny"), "New York", "America/New_York",
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(day => new WeeklyBusinessHours(day, BusinessHours.From(9, 0, 17, 0)))]);

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public IReportingStore Store => Get<IReportingStore>();

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

    /// <summary>Runs the outbox dispatcher until a run handles nothing.</summary>
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

    public Task SyncAsync(string id) => InScopeAsync(async services =>
    {
        await services.GetRequiredService<ReportingProjector>().SyncAsync(Scope, new ConversationId(id));
        return true;
    });

    public async Task CommandAsync(Func<IConversationService, Task<ConversationCommandResult>> command, TimeSpan? advance = null)
    {
        if (advance is { } delay)
            Clock.Advance(delay);
        var result = await InScopeAsync(services => command(services.GetRequiredService<IConversationService>()));
        Assert.IsInstanceOfType<ConversationApplied>(result.Value, result.Value?.ToString());
    }

    public Task StartAsync(string id, string inbox = "email", string? team = null, string? assignee = null, IReadOnlyList<LabelId>? labels = null, bool message = true) =>
        CommandAsync(service => service.StartAsync(Channel, new StartConversationRequest(new InboxId(inbox), new ContactId("ada"), Labels: labels,
            Team: team is null ? null : new TeamId(team), Assignee: assignee is null ? null : new UserId(assignee),
            InitialMessage: message ? new IncomingMessageDraft("Help please") : null, Id: new ConversationId(id))));

    public Task IncomingAsync(string id, TimeSpan advance) =>
        CommandAsync(service => service.AppendIncomingAsync(Channel, new ConversationId(id), new IncomingMessageDraft("More")), advance);

    public Task ReplyAsync(string id, PrincipalRef actor, TimeSpan advance) =>
        CommandAsync(service => service.ReplyAsync(actor, new ConversationId(id), new OutgoingMessageDraft("Answer")), advance);

    public Task AssignAsync(string id, string? agent, TimeSpan advance) =>
        CommandAsync(service => service.AssignAsync(Owner, new ConversationId(id), agent is null ? null : new UserId(agent)), advance);

    public Task SetTeamAsync(string id, string? team, TimeSpan advance) =>
        CommandAsync(service => service.SetTeamAsync(Owner, new ConversationId(id), team is null ? null : new TeamId(team)), advance);

    public Task StatusAsync(string id, ConversationStatus status, TimeSpan advance) =>
        CommandAsync(service => service.ChangeStatusAsync(Owner, new ConversationId(id), status), advance);

    public Task SlaEventAsync(string id, PrincipalRef actor, SlaTimelineEvent slaEvent, TimeSpan advance) =>
        CommandAsync(service => service.AppendExtensionAsync(actor, new ConversationId(id), SlaEventCodec.Encode(slaEvent)), advance);

    /// <summary>Every fact of the scope in store order.</summary>
    public async Task<IReadOnlyList<ReportFact>> FactsAsync(DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var facts = new List<ReportFact>();
        ReportFactCursor? after = null;
        do
        {
            var page = await Store.ListFactsAsync(Scope, new ReportFactQuery(from ?? Monday.AddDays(-30), to ?? Monday.AddDays(300), limit: 1000, after: after), AllowAllFilter.Instance);
            facts.AddRange(page.Items);
            after = page.Next;
        }
        while (after is not null);
        return facts;
    }

    public Task<TicketResult<T>> ReportAsync<T>(Func<IReportingService, Task<TicketResult<T>>> call) =>
        InScopeAsync(services => call(services.GetRequiredService<IReportingService>()));

    public static T Value<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> succeeded => succeeded.Value,
        TicketFailure refused => throw new AssertFailedException($"Refused: {refused.Kind} {string.Join("; ", refused.Errors)}"),
    };

    public void Dispose() => Provider.Dispose();
}
