using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.Tests;

/// <summary>
/// In-memory access, workspaces, contacts, conversations and routing. Seeded: inboxes <c>email</c> (policy <c>main</c>), <c>chat</c>
/// (channel <c>live-chat</c>, policy <c>chat</c>), <c>plain</c> (no policy); team <c>billing</c> (members ann, bob); contacts <c>ada</c>,
/// <c>grace</c>. Principals: <c>owner</c> (Owner), <c>lead</c> (Supervisor), agents <c>ann</c>, <c>bob</c>, <c>cat</c>, <c>dee</c> (Agent),
/// <c>light</c> (LightAgent), <c>viewer</c> (Viewer); API clients <c>channel</c> (create, ingest) and <c>ticketflow-routing</c> (assign).
/// Policy <c>main</c> starts as round robin. Grants bypass the administrator.
/// </summary>
internal sealed class RoutingFixture : IDisposable
{
    public static readonly string[] AgentNames = ["ann", "bob", "cat", "dee"];
    private readonly IServiceScope requestScope;
    private int nextGrant;

    private RoutingFixture(ServiceProvider provider, FakeTimeProvider clock, List<RoutingEvent> events)
    {
        Provider = provider;
        Clock = clock;
        Events = events;
        requestScope = provider.CreateScope();
        var services = requestScope.ServiceProvider;
        Conversations = services.GetRequiredService<IConversationService>();
        Policies = services.GetRequiredService<IRoutingPolicyService>();
        Availability = services.GetRequiredService<IAgentAvailabilityService>();
        Collision = services.GetRequiredService<IConversationCollisionService>();
        Queue = services.GetRequiredService<IRoutingQueueService>();
        Dispatcher = services.GetRequiredService<ConversationOutboxDispatcher>();
        Maintenance = services.GetRequiredService<RoutingMaintenanceWorker>();
        Engine = services.GetRequiredService<IRoutingEngine>();
        PolicyStore = provider.GetRequiredService<IRoutingPolicyStore>();
        Agents = provider.GetRequiredService<IAgentAvailabilityStore>();
        Assignments = provider.GetRequiredService<IRoutingAssignmentStore>();
        ConversationStore = provider.GetRequiredService<InMemoryConversationStore>();
        Grants = provider.GetRequiredService<IAccessGrantStore>();
        Roles = provider.GetRequiredService<IAccessRoleStore>();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public List<RoutingEvent> Events { get; }
    public IConversationService Conversations { get; }
    public IRoutingPolicyService Policies { get; }
    public IAgentAvailabilityService Availability { get; }
    public IConversationCollisionService Collision { get; }
    public IRoutingQueueService Queue { get; }
    public ConversationOutboxDispatcher Dispatcher { get; }
    public RoutingMaintenanceWorker Maintenance { get; }
    public IRoutingEngine Engine { get; }
    public IRoutingPolicyStore PolicyStore { get; }
    public IAgentAvailabilityStore Agents { get; }
    public IRoutingAssignmentStore Assignments { get; }
    public InMemoryConversationStore ConversationStore { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public TicketScope Scope { get; } = TestScopes.Create();

    public UserPrincipal User(string id) => TestPrincipals.User(Scope, id);
    public UserPrincipal Owner => User("owner");
    public UserPrincipal Lead => User("lead");
    public ApiClientPrincipal Channel => TestPrincipals.ApiClient(Scope, "channel");

    /// <param name="stores">Registers routing stores (default: <see cref="RoutingTicketFlowBuilderExtensions.AddInMemoryRoutingStores"/>).</param>
    public static async Task<RoutingFixture> CreateAsync(Action<RoutingOptions>? options = null, bool engineGrant = true, Action<TicketFlowBuilder>? stores = null)
    {
        var clock = TestClock.Create();
        var events = new List<RoutingEvent>();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IRoutingEventObserver>(new RecordingObserver(events));
        var builder = services.AddTicketFlow();
        builder
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddRouting(options).AddRouting();
        if (stores is null)
            builder.AddInMemoryRoutingStores();
        else
            stores(builder);
        var fixture = new RoutingFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock, events);
        await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await fixture.GrantAsync(new UserId("lead"), BuiltInRoles.Supervisor);
        foreach (var agent in AgentNames)
            await fixture.GrantAsync(new UserId(agent), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("light"), BuiltInRoles.LightAgent);
        await fixture.GrantAsync(new UserId("viewer"), BuiltInRoles.Viewer);
        await fixture.GrantAsync(new ApiClientId("channel"), await fixture.CustomRoleAsync("channel", ConversationPermissions.Create, ConversationPermissions.Ingest));
        if (engineGrant)
            await fixture.GrantAsync(new ApiClientId("ticketflow-routing"), await fixture.CustomRoleAsync("routing-engine", ConversationPermissions.Assign));

        var inboxes = fixture.Provider.GetRequiredService<IInboxStore>();
        await inboxes.CreateAsync(fixture.Scope, new Inbox(new InboxId("email"), "Email", new ChannelBinding(new ChannelKind("email"), "cfg-email"), routingPolicy: new RoutingPolicyId("main")));
        await inboxes.CreateAsync(fixture.Scope, new Inbox(new InboxId("chat"), "Chat", new ChannelBinding(new ChannelKind("live-chat"), "cfg-chat"), routingPolicy: new RoutingPolicyId("chat")));
        await inboxes.CreateAsync(fixture.Scope, new Inbox(new InboxId("plain"), "Plain", new ChannelBinding(new ChannelKind("email"), "cfg-plain")));
        var teams = fixture.Provider.GetRequiredService<ITeamStore>();
        await teams.CreateAsync(fixture.Scope, new Team(new TeamId("billing"), "Billing", members: [new UserId("ann"), new UserId("bob")]));
        await teams.CreateAsync(fixture.Scope, new Team(new TeamId("tier2"), "Tier 2", members: [new UserId("dee")]));
        using (var scope = fixture.Provider.CreateScope())
        {
            var contacts = scope.ServiceProvider.GetRequiredService<IContactService>();
            foreach (var contact in new[] { "ada", "grace" })
                Assert.IsInstanceOfType<TicketValue<ContactView>>((await contacts.CreateAsync(fixture.Owner,
                    new ContactDraft(new ContactProfile(contact), Id: new ContactId(contact)))).Value);
        }
        await fixture.SetPolicyAsync(new RoutingPolicy(new RoutingPolicyId("main"), "Main", RoutingStrategy.RoundRobin));
        await fixture.SetPolicyAsync(new RoutingPolicy(new RoutingPolicyId("chat"), "Chat", RoutingStrategy.LeastBusy));
        await fixture.Dispatcher.RegisterAsync();
        return fixture;
    }

    public async Task SetPolicyAsync(RoutingPolicy policy)
    {
        var existing = await PolicyStore.GetAsync(Scope, policy.Id);
        var result = existing is null ? await PolicyStore.CreateAsync(Scope, policy) : await PolicyStore.UpdateAsync(Scope, policy, existing.Revision);
        Assert.IsInstanceOfType<StoreApplied<StoredRoutingPolicy>>(result.Value);
    }

    public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantEffect effect = GrantEffect.Allow, AccessCondition? condition = null, GrantScope? scope = null)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), subject, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId), effect, condition);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await Grants.CreateAsync(grant)).Value);
    }

    public async Task<AccessRole> CustomRoleAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId(id), id, permissions);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        return role;
    }

    /// <summary>Configures and brings agents online (through the availability service as the supervisor and the agent itself).</summary>
    public async Task OnlineAsync(string agent, int capacity = 10, IEnumerable<AgentSkill>? skills = null, IEnumerable<KeyValuePair<ChannelKind, int>>? capacities = null,
        bool bySupervisor = false)
    {
        var existing = await Agents.GetAsync(Scope, new UserId(agent));
        Value(await Availability.ConfigureAsync(Lead, new AgentRoutingSettings(new UserId(agent), capacity, capacities, skills), existing?.Revision));
        Value(await Availability.SetStatusAsync(bySupervisor ? Lead : User(agent), new UserId(agent), AgentStatus.Online));
    }

    public async Task<ConversationApplied> StartAsync(string id, string inbox = "email", string contact = "ada", TeamId? team = null, IReadOnlyList<LabelId>? labels = null,
        UserId? assignee = null, IReadOnlyDictionary<string, ConversationAttributeValue>? attributes = null) =>
        Applied(await Conversations.StartAsync(Channel, new StartConversationRequest(new InboxId(inbox), new ContactId(contact), Team: team, Labels: labels,
            Assignee: assignee, Attributes: attributes, InitialMessage: new IncomingMessageDraft("Help"), Id: new ConversationId(id))));

    /// <summary>Runs the outbox dispatcher until it has nothing left to do.</summary>
    public async Task DrainAsync()
    {
        for (var round = 0; round < 50; round++)
            if ((await Dispatcher.RunOnceAsync()).Handled == 0)
                return;
        Assert.Fail("The outbox did not drain.");
    }

    public async Task<UserId?> AssigneeAsync(string conversation) => (await ConversationStore.GetAsync(Scope, new ConversationId(conversation)))!.Conversation.Assignee;

    /// <summary>Assignment events with a non-null assignee on the conversation timeline.</summary>
    public async Task<IReadOnlyList<AssignmentChanged>> AssignmentsAsync(string conversation) =>
        [.. (await ConversationStore.ReadTimelineAsync(Scope, new ConversationId(conversation), 0, 200, true)).Select(item => item.Event.Value).OfType<AssignmentChanged>()];

    public static T Value<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> value => value.Value,
        TicketFailure failure => throw new AssertFailedException($"Expected a value but was {DescribeFailure(failure)} {string.Join("; ", failure.Errors)}."),
    };

    public static string Describe<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> => "value",
        TicketFailure failure => DescribeFailure(failure),
    };

    private static string DescribeFailure(TicketFailure failure) => failure.Kind switch
    {
        TicketFailureKind.NotFound => "not-found",
        TicketFailureKind.Conflict => $"conflict:{failure.CurrentRevision}",
        _ => $"refused:{failure.Kind}",
    };

    public static string Describe(RoutingOutcome outcome) => outcome switch
    {
        RoutingAssigned assigned => $"assigned:{assigned.Agent}",
        RoutingQueued queued => $"queued:{queued.Reason}",
        RoutingSettled settled => $"settled:{settled.Reason}",
    };

    public static ConversationApplied Applied(ConversationCommandResult result) => result switch
    {
        ConversationApplied applied => applied,
        ConversationNotFound => throw new AssertFailedException("Expected applied but was not found."),
        ConversationConflict conflict => throw new AssertFailedException($"Expected applied but was conflict at {conflict.CurrentRevision}."),
        ConversationRefused refused => throw new AssertFailedException($"Expected applied but was refused: {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }

    private sealed class RecordingObserver(List<RoutingEvent> events) : IRoutingEventObserver
    {
        public Task OnRoutingEventAsync(RoutingEvent routingEvent, CancellationToken cancellationToken = default)
        {
            lock (events)
                events.Add(routingEvent);
            return Task.CompletedTask;
        }
    }
}
