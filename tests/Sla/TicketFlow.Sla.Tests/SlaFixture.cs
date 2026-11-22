using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla.Tests;

/// <summary>
/// In-memory access engine, workspaces, contacts, conversations and SLA. Seeded: calendar <c>ny</c> (Mon-Fri 09:00-17:00
/// America/New_York), policy <c>standard</c> (4 business-hour first response with a 75 % escalation, 8 h next response, 3-day
/// resolution), inbox <c>email</c> (calendar + policy), team <c>billing</c>, contact <c>ada</c>. Principals: <c>owner</c> (Owner),
/// <c>agent</c> (Agent, no SLA permissions), <c>supervisor</c> (Agent + custom <c>sla-reader</c>), <c>billing-lead</c> (sla:read and
/// conversation:read only on team billing conversations), API clients <c>channel</c> (create, ingest) and <c>sla-worker</c>
/// (conversation:manage).
/// </summary>
internal sealed class SlaFixture : IDisposable
{
    private int nextGrant;

    private SlaFixture(ServiceProvider provider, FakeTimeProvider clock)
    {
        Provider = provider;
        Clock = clock;
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public TicketScope Scope { get; } = TestScopes.Create();
    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public UserPrincipal Agent => TestPrincipals.User(Scope, "agent");
    public UserPrincipal Supervisor => TestPrincipals.User(Scope, "supervisor");
    public UserPrincipal BillingLead => TestPrincipals.User(Scope, "billing-lead", "billing");
    public ApiClientPrincipal Channel => TestPrincipals.ApiClient(Scope, "channel");

    public static async Task<SlaFixture> CreateAsync(DateTimeOffset start, Action<IServiceCollection>? configure = null, bool grantWorker = true)
    {
        var clock = TestClock.Create(start);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        configure?.Invoke(services);
        services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddSla().AddInMemorySlaStores();
        var fixture = new SlaFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock);
        await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await fixture.GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("supervisor"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("supervisor"), await fixture.CustomRoleAsync("sla-reader", SlaPermissions.Read));
        await fixture.GrantAsync(new UserId("billing-lead"), await fixture.CustomRoleAsync("billing-lead", SlaPermissions.Read, ConversationPermissions.Read),
            scope: new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        await fixture.GrantAsync(new ApiClientId("channel"), await fixture.CustomRoleAsync("channel", ConversationPermissions.Create, ConversationPermissions.Ingest));
        if (grantWorker)
            await fixture.GrantWorkerAsync();

        await fixture.Get<IBusinessCalendarStore>().CreateAsync(fixture.Scope, SlaEngineTests.NewYork());
        using (var scope = fixture.Provider.CreateScope())
        {
            var policies = scope.ServiceProvider.GetRequiredService<ISlaPolicyService>();
            Assert.IsInstanceOfType<SlaValue<StoredSlaPolicy>>((await policies.CreateAsync(fixture.Owner, new SlaPolicy(new SlaPolicyId("standard"), "Standard",
                [new SlaRule("default", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(4), TimeSpan.FromHours(8), TimeSpan.FromDays(3)), SlaClockBasis.BusinessHours,
                    [new SlaEscalation(SlaMetric.FirstResponse, 75, [new NotifyTeamAction(new TeamId("billing"))])])]))).Value);
            await scope.ServiceProvider.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync();
        }
        await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope, new Inbox(new InboxId("email"), "Email", new ChannelBinding(new ChannelKind("email"), "cfg"),
            new BusinessCalendarId("ny"), new SlaPolicyId("standard")));
        await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope, new Inbox(new InboxId("plain"), "No SLA", new ChannelBinding(new ChannelKind("email"), "cfg2")));
        await fixture.Get<ITeamStore>().CreateAsync(fixture.Scope, new Team(new TeamId("billing"), "Billing"));
        using (var scope = fixture.Provider.CreateScope())
            Assert.IsInstanceOfType<ContactApplied<ContactView>>((await scope.ServiceProvider.GetRequiredService<IContactService>().CreateAsync(fixture.Owner,
                new ContactDraft(new ContactProfile("ada"), Id: new ContactId("ada")))).Value);
        return fixture;
    }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public async Task GrantWorkerAsync() =>
        await GrantAsync(new ApiClientId("sla-worker"), await CustomRoleAsync("sla-worker", ConversationPermissions.Manage));

    public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantScope? scope = null)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), subject, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId),
            GrantEffect.Allow, null);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await Get<IAccessGrantStore>().CreateAsync(grant)).Value);
    }

    public async Task<AccessRole> CustomRoleAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId(id), id, permissions);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessRole>>((await Get<IAccessRoleStore>().CreateAsync(Scope, role)).Value);
        return role;
    }

    /// <summary>Runs <paramref name="action"/> with services from a fresh DI scope (like one host loop iteration).</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Provider.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>
    /// Runs the outbox dispatcher until a run handles nothing (a consumer sees one change per conversation per run, because later
    /// changes wait for earlier ones). Returns the summed counts.
    /// </summary>
    public async Task<ConversationOutboxDispatchResult> DispatchAsync()
    {
        int handled = 0, retried = 0, deadLettered = 0, lost = 0;
        for (var run = 0; run < 50; run++)
        {
            var result = await InScopeAsync(services => services.GetRequiredService<ConversationOutboxDispatcher>().RunOnceAsync());
            handled += result.Handled;
            retried += result.Retried;
            deadLettered += result.DeadLettered;
            lost += result.Lost;
            if (result.Handled == 0)
                break;
        }
        return new ConversationOutboxDispatchResult(handled, retried, deadLettered, lost);
    }

    public Task<SlaTimerRunResult> RunTimersAsync() => InScopeAsync(services => services.GetRequiredService<SlaTimerScheduler>().RunOnceAsync());

    public Task<ConversationCommandResult> CommandAsync(Func<IConversationService, Task<ConversationCommandResult>> command) =>
        InScopeAsync(services => command(services.GetRequiredService<IConversationService>()));

    public async Task StartAsync(string id, string inbox = "email", TeamId? team = null)
    {
        var result = await CommandAsync(service => service.StartAsync(Channel, new StartConversationRequest(new InboxId(inbox), new ContactId("ada"), Team: team,
            InitialMessage: new IncomingMessageDraft("Help please"), Id: new ConversationId(id))));
        Assert.IsInstanceOfType<ConversationApplied>(result.Value);
    }

    /// <summary>SLA timeline events of a conversation, read through the store (notes excluded).</summary>
    public async Task<IReadOnlyList<SlaTimelineEvent>> SlaEventsAsync(string id)
    {
        var events = await Get<IConversationStore>().ReadTimelineAsync(Scope, new ConversationId(id), 0, 200, includeNotes: false);
        var result = new List<SlaTimelineEvent>();
        foreach (var item in events)
            if (item.Event.Value is ExtensionEvent extension && SlaEventCodec.TryDecode(extension, out var decoded))
                result.Add(decoded);
        return result;
    }

    public void Dispose() => Provider.Dispose();
}
