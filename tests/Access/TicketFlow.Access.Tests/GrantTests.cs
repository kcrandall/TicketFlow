using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class GrantTests
{
    [TestMethod]
    public async Task WorkspaceGrantsInheritToTeamsAndInboxes()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        foreach (var resource in new[] { fixture.Resource(), fixture.Resource(team: "billing"), fixture.Resource(inbox: "email"), fixture.Resource(team: "x", inbox: "y") })
            Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, resource));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ContactExport));
    }

    [TestMethod]
    public async Task TeamAndInboxGrantsApplyOnlyWithinTheirResource()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Viewer, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("email")));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(team: "billing")));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(team: "sales")));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource()));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationRead, fixture.Resource(inbox: "email")));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(inbox: "email")));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationRead, fixture.Resource(inbox: "chat")));
    }

    [TestMethod]
    public async Task TenantGrantsApplyInEveryWorkspaceOfThePartition()
    {
        using var fixture = AccessFixture.Create();
        await fixture.GrantAsync(new UserId("ada"), BuiltInRoles.Viewer, TenantGrantScope.Instance);
        foreach (var workspace in new[] { "workspace", "east", "west" })
        {
            var scope = TestScopes.Create(workspaceId: workspace);
            var user = TestPrincipals.User(scope, "ada");
            Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationRead, fixture.Resource(scope: scope)));
        }
    }

    [TestMethod]
    public async Task DenyOverridesAllowAtTheSameOrABroaderScope()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        var billing = new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing"));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, billing);
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, billing, GrantEffect.Deny);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(team: "billing")));

        var other = fixture.User("grace");
        await fixture.GrantAsync(other.UserId, BuiltInRoles.Agent, billing);
        await fixture.GrantAsync(other.UserId, BuiltInRoles.Agent, TenantGrantScope.Instance, GrantEffect.Deny);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(other, AccessPermissions.ConversationReply, fixture.Resource(team: "billing")));
    }

    [TestMethod]
    public async Task NarrowerDenyCarvesOutOfBroaderAllowWithinItsScopeOnly()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip")), GrantEffect.Deny);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(inbox: "vip")));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(inbox: "email")));
    }

    [TestMethod]
    public async Task DenyAffectsOnlyTheDeniedRolesPermissions()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        await fixture.GrantAsync(user.UserId, BuiltInRoles.LightAgent, effect: GrantEffect.Deny);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.NoteRead));
    }

    [TestMethod]
    public async Task SubjectsResolveThroughUsersTeamsAndApiClients()
    {
        using var fixture = AccessFixture.Create();
        await fixture.GrantAsync(new TeamId("billing"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new ApiClientId("crm"), BuiltInRoles.Viewer);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fixture.User("ada", "billing"), AccessPermissions.ConversationReply));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fixture.User("ada", "sales"), AccessPermissions.ConversationReply));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fixture.User("billing"), AccessPermissions.ConversationReply));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fixture.Client(), AccessPermissions.ConversationRead));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fixture.Client("other"), AccessPermissions.ConversationRead));
        Assert.AreEqual("denied:UnsupportedPrincipal", await fixture.DescribeAsync(TestPrincipals.Automation(fixture.Scope), AccessPermissions.ConversationRead));
        Assert.AreEqual("denied:UnsupportedPrincipal", await fixture.DescribeAsync(TestPrincipals.System(fixture.Scope), AccessPermissions.ConversationRead));
    }

    [TestMethod]
    public async Task GrantsNeverCrossAnyScopeLevel()
    {
        using var fixture = AccessFixture.Create();
        await fixture.GrantAsync(new UserId("ada"), BuiltInRoles.Owner);
        await fixture.GrantAsync(new UserId("ada"), BuiltInRoles.Owner, new WorkspaceGrantScope(new WorkspaceId("workspace")));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fixture.User(), AccessPermissions.ConversationRead));
        foreach (var other in TestScopes.LevelVariants(fixture.Scope).Skip(1))
        {
            var principal = TestPrincipals.User(other, "ada");
            Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(principal, AccessPermissions.ConversationRead, fixture.Resource(scope: other)));
            Assert.AreEqual("denied:ScopeMismatch", await fixture.DescribeAsync(fixture.User(), AccessPermissions.ConversationRead, fixture.Resource(scope: other)));
            Assert.AreEqual("denied:ScopeMismatch", await fixture.DescribeAsync(principal, AccessPermissions.ConversationRead));
            Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(principal, AccessPermissions.ConversationRead, other)));
            Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(fixture.User(), AccessPermissions.ConversationRead, other)));
        }
    }

    [TestMethod]
    public async Task ValidityWindowsUseTheTimeProvider()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        var start = fixture.Clock.GetUtcNow();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, validFrom: start.AddHours(1), validUntil: start.AddHours(2));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, effect: GrantEffect.Deny, validFrom: start.AddHours(3));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        fixture.Clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromTicks(10));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        fixture.Clock.Advance(TimeSpan.FromTicks(10));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));

        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
    }

    [TestMethod]
    public void GrantsValidateTheirShape()
    {
        var partition = AccessPartition.From(TestScopes.Create());
        var workspace = new WorkspaceGrantScope(new WorkspaceId("workspace"));
        GrantSubject user = new UserId("ada");
        var now = TestClock.DefaultStart;
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(default, partition, user, BuiltInRoles.Agent.Id, workspace));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, default, BuiltInRoles.Agent.Id, workspace));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, user, default, workspace));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, validFrom: now, validUntil: now));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, justification: "line\nbreak"));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, justification: new string('j', 1025)));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, validUntil: now, kind: GrantKind.BreakGlass));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, justification: "Incident 1", kind: GrantKind.BreakGlass));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, GrantEffect.Deny, validUntil: now, justification: "Incident 1", kind: GrantKind.BreakGlass));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, (GrantEffect)9));
        Assert.ThrowsExactly<ArgumentException>(() => new TeamGrantScope(new WorkspaceId("w"), default));

        var offset = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(5)).AddTicks(7);
        var normalized = new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, workspace, validFrom: offset);
        Assert.AreEqual(TimeSpan.Zero, normalized.ValidFrom!.Value.Offset);
        Assert.AreEqual(offset.UtcTicks - 7, normalized.ValidFrom.Value.UtcTicks);
        Assert.AreEqual("AccessGrant { <redacted> }", normalized.ToString());
    }

    [TestMethod]
    public void ValidatorChecksRoleScopeAndExpiry()
    {
        var partition = AccessPartition.From(TestScopes.Create());
        GrantSubject user = new UserId("ada");
        var now = TestClock.DefaultStart;
        var custom = new AccessRole(new RoleId("triage"), "Triage", []);
        var tenant = new AccessGrant(new GrantId("g"), partition, user, custom.Id, TenantGrantScope.Instance);
        Assert.IsInstanceOfType<GrantInvalid>(AccessGrantValidator.Validate(tenant, custom, now).Value);
        Assert.IsInstanceOfType<GrantInvalid>(AccessGrantValidator.Validate(tenant, null, now).Value);
        Assert.IsInstanceOfType<GrantInvalid>(AccessGrantValidator.Validate(tenant, BuiltInRoles.Agent, now).Value);
        var expired = new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, TenantGrantScope.Instance, validUntil: now);
        var result = AccessGrantValidator.Validate(expired, BuiltInRoles.Agent, now);
        var described = result switch
        {
            GrantValid => "valid",
            GrantInvalid invalid => string.Join("|", invalid.Errors),
        };
        Assert.AreEqual("The grant is already expired.", described);
        var valid = new AccessGrant(new GrantId("g"), partition, user, BuiltInRoles.Agent.Id, TenantGrantScope.Instance, validUntil: now.AddSeconds(1));
        Assert.IsInstanceOfType<GrantValid>(AccessGrantValidator.Validate(valid, BuiltInRoles.Agent, now).Value);
    }

    [TestMethod]
    public async Task AdministratorAuthorizesGrantChangesAndPreventsEscalation()
    {
        using var fixture = AccessFixture.Create();
        var owner = fixture.User("owner");
        var admin = fixture.User("admin");
        var agent = fixture.User("agent");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent);
        AccessGrant Grant(string id, AccessRole role, GrantScope? scope = null, GrantEffect effect = GrantEffect.Allow) =>
            new(new GrantId(id), fixture.Partition, new UserId("newcomer"), role.Id, scope ?? fixture.Workspace, effect);

        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(agent, Grant("a1", BuiltInRoles.Viewer))));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin, Grant("a2", BuiltInRoles.Supervisor))));
        var escalation = (TicketFailure)(await fixture.Admin.CreateGrantAsync(admin, Grant("a3", BuiltInRoles.Owner))).Value!;
        Assert.AreEqual(TicketFailureKind.Forbidden, escalation.Kind);
        CollectionAssert.Contains(escalation.Errors.ToArray(), "The actor does not hold 'access:manage-tenant' on the target.");
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin, Grant("a4", BuiltInRoles.Viewer, TenantGrantScope.Instance))));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(owner, Grant("a5", BuiltInRoles.Owner, TenantGrantScope.Instance))));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin, Grant("a6", BuiltInRoles.Owner, effect: GrantEffect.Deny))));
        Assert.AreEqual("refused:Invalid", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin, Grant("a7", new AccessRole(new RoleId("missing"), "x", [])))));
        Assert.AreEqual("refused:Conflict:1", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin, Grant("a2", BuiltInRoles.Viewer))));
        var foreign = new AccessGrant(new GrantId("a8"), AccessPartition.From(TestScopes.Create(tenantId: "other")), new UserId("x"), BuiltInRoles.Viewer.Id, TenantGrantScope.Instance);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(owner, foreign)));
        var otherWorkspace = Grant("a9", BuiltInRoles.Viewer, new WorkspaceGrantScope(new WorkspaceId("elsewhere")));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(owner, otherWorkspace)));

        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, Grant("a2", BuiltInRoles.Agent), 1)));
        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, Grant("a2", BuiltInRoles.Viewer), 1)));
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, new GrantId("a5"), 1)));
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, Grant("a5", BuiltInRoles.Viewer), 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, new GrantId("a5"), 1)));
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, new GrantId("a5"), 1)));

        var events = fixture.Audit.Snapshot();
        Assert.HasCount(16, events);
        Assert.IsTrue(events.All(e => e.Kind is AccessAuditKind.GrantCreated or AccessAuditKind.GrantUpdated or AccessAuditKind.GrantRevoked));
        Assert.AreEqual(5, events.Count(e => e.Outcome == AccessAuditOutcome.Allowed));
    }
}
