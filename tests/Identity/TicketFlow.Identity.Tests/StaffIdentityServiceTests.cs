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

        public UserPrincipal User(string id, params string[] teams) => TestPrincipals.User(Scope, id, teams);

        public static async Task<Fixture> CreateAsync(Action<AccessControlOptions>? configureAccess = null, Func<TicketScope, UserId, IReadOnlyList<TeamId>>? teamsForUser = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(TestClock.Create());
            services.AddSingleton<ITeamMembershipResolver>(new FixedTeams(teamsForUser));
            services.AddTicketFlow().AddAccessControl(configureAccess).AddInMemoryAccessStores().AddWorkspaces().AddInMemoryWorkspaceStores().AddStaffIdentity().AddInMemoryStaffIdentityStores();
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

        public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantScope? scope = null, GrantEffect effect = GrantEffect.Allow, AccessCondition? condition = null)
        {
            var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D3}"), AccessPartition.From(Scope), subject, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId), effect, condition);
            Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await Provider.GetRequiredService<IAccessGrantStore>().CreateAsync(grant)).Value);
        }

        public void Dispose()
        {
            request.Dispose();
            Provider.Dispose();
        }
    }

    private sealed class FixedTeams(Func<TicketScope, UserId, IReadOnlyList<TeamId>>? resolve = null) : ITeamMembershipResolver
    {
        public Task<IReadOnlyList<TeamId>> GetTeamsAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default) =>
            Task.FromResult(resolve is not null
                ? resolve(scope, user)
                : string.Equals(user.Value, "teamed", StringComparison.Ordinal) ? [new TeamId("escalation")] : (IReadOnlyList<TeamId>)[]);
    }

    private static string Describe<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> => "applied",
        TicketFailure refused => string.Equals(refused.Code, "taken"
, StringComparison.Ordinal) ? "refused:Taken"
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
        Assert.DoesNotContain("sub-ada", Google.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ada@example.com", new StaffProfile(new UserId("a"), notificationEmail: "ada@example.com").ToString(), StringComparison.Ordinal);
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

    [TestMethod]
    public async Task LinkingRejectsTeamFederatedPrivilegeEscalation()
    {
        using var fixture = await Fixture.CreateAsync(
            configureAccess: options =>
            {
                options.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
                options.RoleFederation.MapTeamsAsExternalRoles = true;
                options.RoleFederation.AllowDirectRoleIdMatching = true;
                options.RoleFederation.StaticMappings["static-sup"] = BuiltInRoles.Supervisor.Id;
            },
            teamsForUser: (_, user) => user.Value switch
            {
                "fed-static" => [new TeamId("static-sup")],
                "fed-direct" => [new TeamId("supervisor")],
                "fed-local-ws" => [new TeamId("local-agents")],
                "fed-local-tenant" => [new TeamId("local-tenant-viewers")],
                _ => [],
            });

        var mappings = fixture.Provider.GetRequiredService<IRoleFederationMappingStore>();
        var partition = AccessPartition.From(fixture.Scope);
        Assert.IsTrue((await mappings.UpsertAsync(
            new RoleFederationMappingId("m-local-ws"),
            new RoleFederationMapping("local", "local-agents", BuiltInRoles.Agent.Id, partition, new WorkspaceGrantScope(fixture.Scope.WorkspaceId)),
            isEnabled: true,
            actorPrincipalId: "user:owner")).IsSuccess);
        Assert.IsTrue((await mappings.UpsertAsync(
            new RoleFederationMappingId("m-local-tenant"),
            new RoleFederationMapping("local", "local-tenant-viewers", BuiltInRoles.Viewer.Id, partition, TenantGrantScope.Instance),
            isEnabled: true,
            actorPrincipalId: "user:tenant-owner")).IsSuccess);

        var manager = fixture.User("manager");
        var owner = fixture.User("owner");
        var tenantOwner = fixture.User("tenant-owner");

        // All four targets have 0 local grants in IAccessGrantStore; their privileges come strictly from team-based role federation.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(manager, new StaffIdentityRef("google", "sub-static"), new UserId("fed-static"))),
            "Static team-federated role must block escalation.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(manager, new StaffIdentityRef("google", "sub-direct"), new UserId("fed-direct"))),
            "Direct team-to-role-id federated role must block escalation.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(manager, new StaffIdentityRef("google", "sub-local-ws"), new UserId("fed-local-ws"))),
            "Dynamic local-provider team-federated role must block escalation.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(manager, new StaffIdentityRef("google", "sub-local-tenant"), new UserId("fed-local-tenant"))),
            "Tenant-scoped team-federated role must block manager.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(owner, new StaffIdentityRef("google", "sub-local-tenant"), new UserId("fed-local-tenant"))),
            "Workspace owner cannot link an identity to a user with a tenant-scoped team-federated role.");

        // Workspace owner holds all workspace-scoped permissions and can link workspace team-federated targets.
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(owner, new StaffIdentityRef("google", "sub-static"), new UserId("fed-static"))));
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(owner, new StaffIdentityRef("google", "sub-direct"), new UserId("fed-direct"))));
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(owner, new StaffIdentityRef("google", "sub-local-ws"), new UserId("fed-local-ws"))));
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(tenantOwner, new StaffIdentityRef("google", "sub-local-tenant"), new UserId("fed-local-tenant"))));

        // Unlinking enforces the same team-federated anti-escalation check.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(manager, new StaffIdentityRef("google", "sub-static"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(owner, new StaffIdentityRef("google", "sub-local-tenant"))));
    }

    [TestMethod]
    public async Task BreakGlassAndStrippedFederatedStaffIdentityManageAreRefusedAgainstZeroGrantTargets()
    {
        using var fixture = await Fixture.CreateAsync(configureAccess: options =>
        {
            options.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
            options.RoleFederation.AllowAdministrativeRoles = false;
            options.RoleFederation.StaticMappings["idp-owner"] = BuiltInRoles.Owner.Id;
            options.RoleFederation.StaticMappings["idp-id-mgr"] = new RoleId("identity-manager");
        });

        using var scope = fixture.Provider.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAccessAdministrator>();
        var grants = fixture.Provider.GetRequiredService<IAccessGrantStore>();
        var now = fixture.Provider.GetRequiredService<TimeProvider>().GetUtcNow();

        // 1. ActivateBreakGlassAsync must reject any role containing staff-identity:manage.
        var onCallRole = new AccessRole(new RoleId("on-call-bg"), "On-call", [AccessPermissions.AccessBreakGlass]);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await fixture.Provider.GetRequiredService<IAccessRoleStore>().CreateAsync(fixture.Scope, onCallRole)).Value);
        await fixture.GrantAsync(new UserId("on-call"), onCallRole);
        var bgAttempt = await admin.ActivateBreakGlassAsync(new BreakGlassRequest(
            fixture.User("on-call"),
            new GrantId("bg-staff-id"),
            new RoleId("identity-manager"),
            new WorkspaceGrantScope(fixture.Scope.WorkspaceId),
            TimeSpan.FromMinutes(30),
            "Emergency staff identity link"));
        Assert.AreEqual("refused:Invalid", Describe(bgAttempt), "Break-glass roles must not include staff-identity:manage.");

        // 2. Even a directly-seeded break-glass grant with staff-identity:manage must be refused against a 0-grant target.
        var bgGrant = new AccessGrant(
            new GrantId("seeded-bg-staff"),
            AccessPartition.From(fixture.Scope),
            new UserId("bg-actor"),
            new RoleId("identity-manager"),
            new WorkspaceGrantScope(fixture.Scope.WorkspaceId),
            GrantEffect.Allow,
            validFrom: now.AddMinutes(-5),
            validUntil: now.AddHours(1),
            justification: "Seeded emergency break-glass grant",
            kind: GrantKind.BreakGlass);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await grants.CreateAsync(bgGrant)).Value);

        var bgActor = fixture.User("bg-actor");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(bgActor, Google, new UserId("newbie"))),
            "Break-glass staff-identity:manage must be refused even when target has 0 local grants.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.ListAsync(bgActor, new UserId("newbie"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.SetProfileAsync(bgActor, new StaffProfile(new UserId("newbie"), "X"), null)));

        // Seed a link as owner, then verify break-glass actor cannot unlink even a 0-grant user.
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(fixture.User("owner"), Google, new UserId("newbie"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(bgActor, Google)));

        // 3. Federated roles with AllowAdministrativeRoles = false must have staff-identity:manage stripped.
        var fedOwner = new UserPrincipal(fixture.Scope, new UserId("fed-owner"), externalRoles: ["idp-owner"], identityProvider: "okta");
        var fedManager = new UserPrincipal(fixture.Scope, new UserId("fed-mgr"), externalRoles: ["idp-id-mgr"], identityProvider: "okta");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(fedOwner, Entra, new UserId("newbie"))),
            "Federated Owner must have staff-identity:manage stripped when AllowAdministrativeRoles is false.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(fedManager, Entra, new UserId("newbie"))),
            "Federated custom role must have staff-identity:manage stripped when AllowAdministrativeRoles is false.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(fedOwner, Google)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(fedManager, Google)));
    }

    [TestMethod]
    public async Task StaffIdentityManageEvaluatesConditionsConservativelyAndEnforcesSubScopeDenies()
    {
        using var fixture = await Fixture.CreateAsync();
        var identityManagerRole = new AccessRole(new RoleId("identity-manager"), "Identity manager", [StaffIdentityPermissions.Manage]);
        var targetUser = new UserId("target-user");
        var owner = fixture.User("owner");

        // Seed an existing link so UnlinkAsync can be tested against a live link.
        var seededLink = new StaffIdentityRef("google", "sub-existing");
        Assert.AreEqual("applied", Describe(await fixture.Service.LinkAsync(owner, seededLink, targetUser)));

        // 1. Conditional Deny (AssigneeIsSelfCondition / TeamInPrincipalTeamsCondition) blocks LinkAsync, UnlinkAsync, ListAsync, SetProfileAsync.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("cond-deny-assignee", new AssigneeIsSelfCondition()),
            ("cond-deny-team", new TeamInPrincipalTeamsCondition()),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, identityManagerRole);
            await fixture.GrantAsync(actor.UserId, identityManagerRole, effect: GrantEffect.Deny, condition: condition);

            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(actor, new StaffIdentityRef("entra", $"sub-{name}"), targetUser)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(actor, seededLink)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.ListAsync(actor, targetUser)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.SetProfileAsync(actor, new StaffProfile(targetUser, "Blocked"), null)), name);
        }

        // 2. Negated conditional Allow (Not(AssigneeIsSelfCondition) / Not(TeamInPrincipalTeamsCondition)) does NOT grant staff-identity:manage.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("cond-allow-not-assignee", new NotCondition(new AssigneeIsSelfCondition())),
            ("cond-allow-not-team", new NotCondition(new TeamInPrincipalTeamsCondition())),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, identityManagerRole, condition: condition);

            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(actor, new StaffIdentityRef("entra", $"sub-{name}"), targetUser)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(actor, seededLink)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.ListAsync(actor, targetUser)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.SetProfileAsync(actor, new StaffProfile(targetUser, "Blocked"), null)), name);
        }

        // 3. Sub-scope Deny (Inbox / Team) blocks staff-identity:manage on abstract workspace target.
        foreach (var (name, denyScope) in new (string, GrantScope)[]
        {
            ("subscope-deny-inbox", new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip"))),
            ("subscope-deny-team", new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing"))),
        })
        {
            var actor = fixture.User(name);
            await fixture.GrantAsync(actor.UserId, identityManagerRole);
            await fixture.GrantAsync(actor.UserId, identityManagerRole, scope: denyScope, effect: GrantEffect.Deny);

            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.LinkAsync(actor, new StaffIdentityRef("entra", $"sub-{name}"), targetUser)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UnlinkAsync(actor, seededLink)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.ListAsync(actor, targetUser)), name);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.SetProfileAsync(actor, new StaffProfile(targetUser, "Blocked"), null)), name);
        }
    }
}
