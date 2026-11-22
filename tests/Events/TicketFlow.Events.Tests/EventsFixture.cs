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

namespace TicketFlow.Events.Tests;

/// <summary>Records in-process deliveries; <see cref="Fail"/> makes it throw.</summary>
internal sealed class RecordingSubscriber : IEventSubscriber
{
    public string Name => "recorder";

    public List<EventDelivery> Deliveries { get; } = [];

    public bool Fail { get; set; }

    public IEnumerable<RenderedEvent> Events => Deliveries.SelectMany(delivery => delivery.Events);

    public Task HandleAsync(EventDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (Fail)
            throw new InvalidOperationException("Subscriber failure with customer data that must not be recorded.");
        Deliveries.Add(delivery);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory access, workspaces, contacts, conversations and events. Seeded: inboxes <c>email</c> and <c>chat</c>, teams <c>billing</c>
/// and <c>support</c>, contact <c>ada</c> (verified email). Principals: <c>owner</c> (Owner), <c>manager</c> (events:manage + events:read),
/// API clients <c>channel</c> (create, ingest), <c>sla-worker</c> and <c>forger</c> (conversation:manage), <c>hooks</c> (events:read,
/// conversation:read, contact:read), <c>content-hooks</c> (hooks + events:content), <c>billing-hooks</c> (events:read workspace,
/// conversation:read on team billing).
/// </summary>
internal sealed class EventsFixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 3, 2, 14, 0, 0, TimeSpan.Zero);

    private int nextGrant;
    private IAsyncDisposable? storage;

    /// <summary>
    /// When set (by the PostgreSQL test assembly), registers a durable event store before the in-memory default and returns its storage
    /// to dispose with the fixture.
    /// </summary>
    public static Func<TicketFlowBuilder, Task<IAsyncDisposable>>? StoreFactory { get; set; }

    private EventsFixture(ServiceProvider provider, FakeTimeProvider clock, RecordingSubscriber subscriber, FakeSurveyStore surveys)
    {
        Provider = provider;
        Clock = clock;
        Subscriber = subscriber;
        Surveys = surveys;
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public RecordingSubscriber Subscriber { get; }
    public FakeSurveyStore Surveys { get; }
    public TicketScope Scope { get; } = TestScopes.Create();
    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public UserPrincipal Manager => TestPrincipals.User(Scope, "manager");
    public UserPrincipal Agent => TestPrincipals.User(Scope, "agent");
    public ApiClientPrincipal Channel => TestPrincipals.ApiClient(Scope, "channel");
    public ApiClientPrincipal SlaWorker => TestPrincipals.ApiClient(Scope, "sla-worker");
    public ApiClientPrincipal Forger => TestPrincipals.ApiClient(Scope, "forger");

    public static async Task<EventsFixture> CreateAsync(Action<EventsOptions>? options = null)
    {
        var clock = TestClock.Create(Start);
        var subscriber = new RecordingSubscriber();
        var surveys = new FakeSurveyStore();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ISurveyStore>(surveys);
        services.AddSingleton<IEventSubscriber>(subscriber);
        var storage = StoreFactory is { } factory ? await factory(services.AddTicketFlow()) : null;
        services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddEvents(value =>
            {
                value.InitialRetryDelay = TimeSpan.FromSeconds(10);
                value.MaximumAttempts = 3;
                options?.Invoke(value);
            }).AddInMemoryEventStores();
        var fixture = new EventsFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock, subscriber, surveys)
        {
            storage = storage,
        };
        await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await fixture.GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("manager"), await fixture.CustomRoleAsync("event-managers", EventPermissions.Manage, EventPermissions.Read));
        await fixture.GrantAsync(new ApiClientId("channel"), await fixture.CustomRoleAsync("channel", ConversationPermissions.Create, ConversationPermissions.Ingest));
        var manage = await fixture.CustomRoleAsync("extensions", ConversationPermissions.Manage);
        await fixture.GrantAsync(new ApiClientId("sla-worker"), manage);
        await fixture.GrantAsync(new ApiClientId("forger"), manage);
        await fixture.GrantAsync(new ApiClientId("hooks"), await fixture.CustomRoleAsync("hooks", EventPermissions.Read, ConversationPermissions.Read, ContactPermissions.Read));
        await fixture.GrantAsync(new ApiClientId("content-hooks"), await fixture.CustomRoleAsync("content-hooks", EventPermissions.Read, EventPermissions.Content,
            ConversationPermissions.Read, ContactPermissions.Read));
        await fixture.GrantAsync(new ApiClientId("billing-hooks"), await fixture.CustomRoleAsync("events-only", EventPermissions.Read));
        await fixture.GrantAsync(new ApiClientId("billing-hooks"), await fixture.CustomRoleAsync("conversation-reader", ConversationPermissions.Read),
            new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));

        await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope, new Inbox(new InboxId("email"), "Email", new ChannelBinding(new ChannelKind("email"), "cfg")));
        await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope, new Inbox(new InboxId("chat"), "Chat", new ChannelBinding(new ChannelKind("live-chat"), "cfg2")));
        await fixture.Get<ITeamStore>().CreateAsync(fixture.Scope, new Team(new TeamId("billing"), "Billing"));
        await fixture.Get<ITeamStore>().CreateAsync(fixture.Scope, new Team(new TeamId("support"), "Support"));
        await fixture.CreateContactAsync("ada", "ada@example.com");
        await fixture.InScopeAsync(async provider =>
        {
            await provider.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync();
            return true;
        });
        return fixture;
    }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public IEventLog Log => Get<IEventLog>();

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

    public async Task MaskAsync(AccessFieldPolicy policy) =>
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessFieldPolicy>>((await Get<IAccessFieldPolicyStore>().CreateAsync(Scope, policy)).Value);

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Provider.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<ContactView> CreateContactAsync(string id, string email) => InScopeAsync(async provider =>
    {
        var identity = new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Email, email), new IdentityVerified("test", Start));
        var result = await provider.GetRequiredService<IContactService>().CreateAsync(Owner, new ContactDraft(new ContactProfile(id), [identity], Id: new ContactId(id)));
        return ((ContactApplied<ContactView>)result.Value!).Value;
    });

    /// <summary>Runs the outbox dispatcher until a run handles nothing.</summary>
    public async Task DispatchAsync()
    {
        for (var run = 0; run < 100; run++)
        {
            var result = await InScopeAsync(provider => provider.GetRequiredService<ConversationOutboxDispatcher>().RunOnceAsync());
            Assert.AreEqual(0, result.Retried + result.DeadLettered, "Projection failed.");
            if (result.Handled == 0)
                break;
        }
    }

    public Task<EventDeliveryRunResult> DeliverAsync() => InScopeAsync(provider => provider.GetRequiredService<EventDeliveryWorker>().RunOnceAsync());

    /// <summary>Runs delivery until nothing is delivered or advanced.</summary>
    public async Task DeliverAllAsync()
    {
        for (var run = 0; run < 100; run++)
        {
            var result = await DeliverAsync();
            if (result.Delivered + result.Advanced == 0)
                break;
        }
    }

    public async Task<IReadOnlyList<StoredIntegrationEvent>> EventsAsync() => await Log.ReadAsync(Scope, 0, 500);

    public async Task CommandAsync(Func<IConversationService, Task<ConversationCommandResult>> command, TimeSpan? advance = null)
    {
        Clock.Advance(advance ?? TimeSpan.FromMinutes(1));
        var result = await InScopeAsync(provider => command(provider.GetRequiredService<IConversationService>()));
        Assert.IsInstanceOfType<ConversationApplied>(result.Value, result.Value?.ToString());
    }

    public Task StartAsync(string id, string inbox = "email", string? team = null, string? externalId = null) =>
        CommandAsync(service => service.StartAsync(Channel, new StartConversationRequest(new InboxId(inbox), new ContactId("ada"),
            Team: team is null ? null : new TeamId(team), InitialMessage: new IncomingMessageDraft("Help please, my card 4111 was charged twice", ExternalId: externalId),
            Id: new ConversationId(id))));

    public Task ReplyAsync(string id) => CommandAsync(service => service.ReplyAsync(Owner, new ConversationId(id), new OutgoingMessageDraft("Refund issued")));

    public Task NoteAsync(string id) => CommandAsync(service => service.AddNoteAsync(Owner, new ConversationId(id), new NoteDraft("Private: customer is a VIP")));

    public Task AssignAsync(string id, string? agent) => CommandAsync(service => service.AssignAsync(Owner, new ConversationId(id), agent is null ? null : new UserId(agent)));

    public Task SetTeamAsync(string id, string? team) => CommandAsync(service => service.SetTeamAsync(Owner, new ConversationId(id), team is null ? null : new TeamId(team)));

    public Task StatusAsync(string id, ConversationStatus status) => CommandAsync(service => service.ChangeStatusAsync(Owner, new ConversationId(id), status));

    public Task IncomingAsync(string id) => CommandAsync(service => service.AppendIncomingAsync(Channel, new ConversationId(id), new IncomingMessageDraft("Still broken")));

    public Task SlaEventAsync(string id, PrincipalRef actor, SlaTimelineEvent slaEvent) =>
        CommandAsync(service => service.AppendExtensionAsync(actor, new ConversationId(id), SlaEventCodec.Encode(slaEvent)));

    public Task<EventResult<EventSubscriptionView>> SubscribeAsync(string id, string principal = "hooks", EventPrivacy privacy = EventPrivacy.IdsOnly,
        EventSubscriptionFilter? filter = null, int batchSize = 50, EventSubscriptionStart? start = null, PrincipalRef? actor = null) =>
        InScopeAsync(provider => provider.GetRequiredService<IEventSubscriptionService>().CreateAsync(actor ?? Owner,
            new EventSubscription(new EventSubscriptionId(id), id, new EventSinkRef("in-process", "recorder"), filter ?? EventSubscriptionFilter.Everything,
                new ApiClientId(principal), privacy, batchSize: batchSize), start ?? EventStartAtEarliest.Instance));

    public static T Value<T>(EventResult<T> result) => result switch
    {
        EventSucceeded<T> succeeded => succeeded.Value,
        EventNotFound => throw new AssertFailedException("Not found."),
        EventConflict conflict => throw new AssertFailedException($"Conflict at {conflict.CurrentRevision}."),
        EventRefused refused => throw new AssertFailedException($"Refused: {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public void Dispose()
    {
        Provider.Dispose();
        storage?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
