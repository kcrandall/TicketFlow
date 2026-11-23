using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Identity.StoreContracts;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Identity.Tests;

[TestClass]
public sealed class StaffIdentityServiceTests
{
    private static readonly StaffIdentityRef Google = new("google", "sub-ada");
    private static readonly StaffIdentityRef Entra = new("entra-acme", "0f8e-ada");

    /// <summary>
    /// Seeded: <c>owner</c> (workspace Owner), <c>tenant-owner</c> (tenant Owner), <c>agent</c> (Agent), <c>viewer</c> (Viewer),
    /// <c>manager</c> (staff-identity:manage + structure reads only), <c>teamed</c> (member of team <c>escalation</c>, which holds
    /// Supervisor) and <c>newbie</c> (no grants).
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly IServiceScope request;
        private int nextGrant;

        private Fixture(ServiceProvider provider, TicketScope scope)
        {
            Provider = provider;
            Scope = scope;
            request = provider.CreateScope();
            Service = request.ServiceProvider.GetRequiredService<IStaffIdentityService>();
            Resolver = request.ServiceProvider.GetRequiredService<IStaffIdentityResolver>();
            Store = provider.GetRequiredService<InMemoryStaffIdentityStore>();
        }

        public ServiceProvider Provider { get; }
        public TicketScope Scope { get; }
        public IStaffIdentityService Service { get; }
        public IStaffIdentityResolver Resolver { get; }
        public InMemoryStaffIdentityStore Store { get; }

        public UserPrincipal User(string id) => new(Scope, new UserId(id));

        public static async Task<Fixture> CreateAsync()
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(TestClock.Create());
            services.AddSingleton<ITeamMembershipResolver>(new FixedTeams());
            services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddWorkspaces().AddInMemoryWorkspaceStores().AddStaffIdentity().AddInMemoryStaffIdentityStores();
            var fixture = new Fixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), TestScopes.Create());
            await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
            await fixture.GrantAsync(new UserId("tenant-owner"), BuiltInRoles.Owner, TenantGrantScope.Instance);
            await fixture.GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
            await fixture.GrantAsync(new UserId("viewer"), BuiltInRoles.Viewer);
            var manager = new AccessRole(new RoleId("identity-manager"), "Identity manager",
                [StaffIdentityPermissions.Manage, AccessPermissions.WorkspaceRead, AccessPermissions.TeamRead, AccessPermissions.InboxRead]);
            Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await fixture.Provider.GetRequiredService<IAccessRoleStore>().CreateAsync(fixture.Scope, manager)).Value);
            await fixture.GrantAsync(new UserId("manager"), manager);
            await fixture.GrantAsync(new TeamId("escalation"), BuiltInRoles.Supervisor);
            return fixture;
        }

        public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantScope? scope = null)
        {
            var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D3}"), AccessPartition.From(Scope), subject, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId));
            Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await Provider.GetRequiredService<IAccessGrantStore>().CreateAsync(grant)).Value);
        }

        public void Dispose()
        {
            request.Dispose();
            Provider.Dispose();
        }
    }

    private sealed class FixedTeams : ITeamMembershipResolver
    {
        public Task<IReadOnlyList<TeamId>> GetTeamsAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TeamId>>(user.Value == "teamed" ? [new TeamId("escalation")] : []);
    }

    private static string Describe<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> => "applied",
        TicketFailure refused => refused.Code == "taken"
            ? "refused:Taken"
            : $"refused:{refused.Kind}" + (refused.CurrentRevision is { } revision ? $":{revision}" : ""),
    };

    private static T Applied<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> applied => applied.Value,
        TicketFailure refused => throw new AssertFailedException($"refused {refused.Kind} {string.Join(";", refused.Errors)}"),
    };

    [TestMethod]
    public void ModelValidatesAndRedacts()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new StaffIdentityRef("Google", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => new StaffIdentityRef("google", " "));
        Assert.ThrowsExactly<ArgumentException>(() => new StaffIdentityRef("google", "bad\u0001"));
        Assert.ThrowsExactly<ArgumentException>(() => new StaffIdentityRef("google", new string('x', 257)));
        Assert.ThrowsExactly<ArgumentException>(() => new StaffProfile(new UserId("a"), notificationEmail: "not-an-email"));
        Assert.ThrowsExactly<ArgumentException>(() => new StaffProfile(new UserId("a"), displayName: "lone \ud800"));
        Assert.DoesNotContain("sub-ada", Google.ToString());
        Assert.DoesNotContain("ada@example.com", new StaffProfile(new UserId("a"), notificationEmail: "ada@example.com").ToString());
        Assert.IsTrue(StaffIdentityPermissions.Definitions.Single().IsSensitive);
    }

    [TestMethod]
    public async Task InMemoryStoreSatisfiesTheContract()
    {
        var store = new InMemoryStaffIdentityStore();
        await StaffIdentityStoreContract.VerifyLinksAsync(store, store);
        await StaffIdentityStoreContract.VerifyConcurrentLinksAsync(store, store);
        await StaffIdentityStoreContract.VerifyProfilesAsync(store, store);
    }

    [TestMethod]
    public async Task ManagersMapManyIdentitiesToOneUserAndTheResolverFollows()
    {
        using var fixture = await Fixture.CreateAsync();
        var owner = fixture.User("owner");
        Assert.IsNull(await fixture.Resolver.ResolveUserAsync(fixture.Scope, Google));
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(owner, Google, new UserId("agent"), "onboarding")));
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(owner, Entra, new UserId("agent"))));
        Assert.AreEqual(new UserId("agent"), await fixture.Resolver.ResolveUserAsync(fixture.Scope, Google));
        Assert.AreEqual(new UserId("agent"), await fixture.Resolver.ResolveUserAsync(fixture.Scope, Entra));
        Assert.IsNull(await fixture.Resolver.ResolveUserAsync(TestScopes.Create(workspaceId: "elsewhere"), Google), "mappings never cross workspaces");

        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(owner, Google, new UserId("agent"))), "idempotent");
        Assert.AreEqual("refused:Taken", Describe(await fixture.Service.LinkAsync(owner, Google, new UserId("viewer"))));
        var audit = fixture.Store.AuditEvents;
        Assert.AreEqual(2, audit.Count(item => item.Kind == StaffIdentityAuditKind.Linked));
        Assert.AreEqual("onboarding", audit[0].Justification);
        Assert.AreEqual("google", audit[0].Provider);
        Assert.AreEqual(new UserId("agent"), audit[0].User);

        // Users see their own identities; others need staff-identity:manage.
        Assert.HasCount(2, Applied(await fixture.Service.ListAsync(fixture.User("agent"), new UserId("agent"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.ListAsync(fixture.User("viewer"), new UserId("agent"))));
        Assert.HasCount(2, Applied(await fixture.Service.ListAsync(fixture.User("manager"), new UserId("agent"))));

        Assert.AreEqual(new UserId("agent"), Applied(await fixture.Service.UnlinkAsync(owner, Google, "left provider")));
        Assert.IsNull(await fixture.Resolver.ResolveUserAsync(fixture.Scope, Google));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Service.UnlinkAsync(owner, Google)));
        Assert.AreEqual(StaffIdentityAuditKind.Unlinked, fixture.Store.AuditEvents[^1].Kind);
    }

    [TestMethod]
    public async Task LinkingNeverHandsTheActorMoreAuthorityThanItHas()
    {
        using var fixture = await Fixture.CreateAsync();
        var manager = fixture.User("manager");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(fixture.User("agent"), Google, new UserId("agent"))), "staff-identity:manage is required");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(fixture.User("viewer"), Google, new UserId("newbie"))));

        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(manager, Google, new UserId("newbie"))), "a user without grants confers nothing");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(manager, Entra, new UserId("owner"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(manager, Entra, new UserId("agent"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(manager, Entra, new UserId("teamed"))), "team grants count");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(fixture.User("owner"), Entra, new UserId("tenant-owner"))),
            "tenant grants require tenant-scoped authority");
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(fixture.User("tenant-owner"), Entra, new UserId("owner"))));
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(fixture.User("owner"), new StaffIdentityRef("google", "sub-teamed"), new UserId("teamed"))));

        // Unlinking is held to the same rule (no locking out more privileged users).
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(manager, Entra)));
        Assert.AreEqual(new UserId("owner"), await fixture.Resolver.ResolveUserAsync(fixture.Scope, Entra));
        Assert.AreEqual(new UserId("newbie"), Applied(await fixture.Service.UnlinkAsync(manager, Google)));
    }

    [TestMethod]
    public async Task ProfilesAreSelfServiceAndMaskTheNotificationEmail()
    {
        using var fixture = await Fixture.CreateAsync();
        var agent = fixture.User("agent");
        var profile = new StaffProfile(new UserId("agent"), "Ada", "avatars/ada.png", "ada@example.com");
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Service.GetProfileAsync(agent, new UserId("agent"))));
        var created = Applied(await fixture.Service.SetProfileAsync(agent, profile, null));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual("refused:Conflict:1", Describe(await fixture.Service.SetProfileAsync(agent, profile, null)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.SetProfileAsync(fixture.User("viewer"), profile, 1)));
        Assert.AreEqual(2, Applied(await fixture.Service.SetProfileAsync(fixture.User("manager"), new StaffProfile(new UserId("agent"), "Ada L.", notificationEmail: "ada@example.com"), 1)).Revision);
        Assert.AreEqual(2, fixture.Store.AuditEvents.Count(item => item.Kind == StaffIdentityAuditKind.ProfileChanged));

        Assert.AreEqual("ada@example.com", Applied(await fixture.Service.GetProfileAsync(fixture.User("viewer"), new UserId("agent"))).NotificationEmail);
        var policy = new AccessFieldPolicy(StaffFieldPaths.NotificationEmail, [AccessPermissions.TeamRead], [BuiltInRoles.Supervisor.Id]);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessFieldPolicy>>((await fixture.Provider.GetRequiredService<IAccessFieldPolicyStore>().CreateAsync(fixture.Scope, policy)).Value);
        var masked = Applied(await fixture.Service.GetProfileAsync(fixture.User("viewer"), new UserId("agent")));
        Assert.IsTrue(masked.EmailMasked);
        Assert.IsNull(masked.NotificationEmail);
        Assert.AreEqual("Ada L.", masked.DisplayName);
        Assert.AreEqual("ada@example.com", Applied(await fixture.Service.GetProfileAsync(agent, new UserId("agent"))).NotificationEmail, "users always see their own email");
    }
}
