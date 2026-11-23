using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Core;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class AdministrationTests
{
    private static async Task<(AccessFixture Fixture, UserPrincipal Owner, UserPrincipal Admin, UserPrincipal Agent)> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var fixture = AccessFixture.Create(configure);
        var owner = fixture.User("owner");
        var admin = fixture.User("admin");
        var agent = fixture.User("agent");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent);
        return (fixture, owner, admin, agent);
    }

    [TestMethod]
    public async Task RoleWideningCannotEscalate()
    {
        var (fixture, _, admin, _) = await CreateAsync();
        using var _ = fixture;
        var narrow = new AccessRole(new RoleId("r"), "R", [AccessPermissions.ConversationRead]);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(admin, narrow)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin,
            new AccessGrant(new GrantId("self"), fixture.Partition, admin.UserId, narrow.Id, fixture.Workspace))));

        var widened = new AccessRole(narrow.Id, "R", [AccessPermissions.ConversationRead, AccessPermissions.WorkspaceManage, AccessPermissions.AccessManageTenant]);
        var refused = (TicketFailure)(await fixture.Admin.UpdateRoleAsync(admin, widened, 1)).Value!;
        Assert.AreEqual(TicketFailureKind.Forbidden, refused.Kind);
        CollectionAssert.AreEqual(new[] { "The actor does not hold 'access:manage-tenant' on the target.", "The actor does not hold 'workspace:manage' on the target." }, refused.Errors.ToArray());
        Assert.AreEqual(narrow, (await fixture.Roles.GetAsync(fixture.Scope, narrow.Id))!.Role);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(admin, AccessPermissions.WorkspaceManage));

        // The follow-on attack also fails: a tenant Owner self-grant.
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin,
            new AccessGrant(new GrantId("takeover"), fixture.Partition, admin.UserId, BuiltInRoles.Owner.Id, TenantGrantScope.Instance))));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(admin,
            new AccessRole(new RoleId("settings"), "Settings", [AccessPermissions.WorkspaceManage]))));
    }

    [TestMethod]
    public async Task RolesReferencedByStrongerGrantsCannotBeChangedOrRecreatedByWeakerActors()
    {
        var (fixture, owner, admin, _) = await CreateAsync();
        using var _ = fixture;
        var settings = new AccessRole(new RoleId("settings"), "Settings", [AccessPermissions.WorkspaceManage, AccessPermissions.ConversationRead]);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(owner, settings)));
        await fixture.GrantAsync(new UserId("bob"), settings);
        await fixture.GrantAsync(new UserId("mallory"), settings, effect: GrantEffect.Deny);

        var narrowed = new AccessRole(settings.Id, "Settings", [AccessPermissions.ConversationRead]);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, narrowed, 1)));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(admin, settings.Id, 1)));
        Assert.AreEqual("refused:Conflict:1", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(owner, settings)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(owner, narrowed, 1)));
        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(owner, settings.Id, 1)));
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(owner, new AccessRole(new RoleId("ghost"), "x", []), 1)));
    }

    [TestMethod]
    public async Task TenantAuthorityRequiresTenantScopedGrants()
    {
        var (fixture, owner, _, _) = await CreateAsync();
        using var _ = fixture;
        var workspaceOwner = fixture.User("workspace-owner");
        await fixture.GrantAsync(workspaceOwner.UserId, BuiltInRoles.Owner);
        var tenantViewer = new AccessGrant(new GrantId("tenant-viewer"), fixture.Partition, new UserId("x"), BuiltInRoles.Viewer.Id, TenantGrantScope.Instance);
        var refused = (TicketFailure)(await fixture.Admin.CreateGrantAsync(workspaceOwner, tenantViewer)).Value!;
        Assert.AreEqual(TicketFailureKind.Forbidden, refused.Kind);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(owner, tenantViewer)));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(workspaceOwner, tenantViewer.Id, 1)));
        // Workspace-level management by the workspace Owner is unaffected.
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(workspaceOwner,
            new AccessGrant(new GrantId("ws-viewer"), fixture.Partition, new UserId("x"), BuiltInRoles.Viewer.Id, fixture.Workspace))));
    }

    [TestMethod]
    public async Task GrantChangesAreFencedBeforeAuthorization()
    {
        var (fixture, _, admin, _) = await CreateAsync();
        using var _ = fixture;
        AccessGrant Viewer(string role) => new(new GrantId("g"), fixture.Partition, new UserId("x"), new RoleId(role), fixture.Workspace);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(admin, Viewer("viewer"))));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, Viewer("agent"), 1)));
        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, Viewer("viewer"), 1)));
        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, new GrantId("g"), 1)));
        Assert.AreEqual(new RoleId("agent"), (await fixture.Grants.GetAsync(fixture.Partition, new GrantId("g")))!.Grant.Role);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, new GrantId("g"), 2)));
    }

    [TestMethod]
    public async Task BreakGlassGrantsCannotBeCreatedDirectlyOrUsedForEscalation()
    {
        var (fixture, owner, _, _) = await CreateAsync();
        using var _ = fixture;
        var direct = new AccessGrant(new GrantId("bg"), fixture.Partition, owner.UserId, BuiltInRoles.Supervisor.Id, fixture.Workspace,
            validUntil: fixture.Clock.GetUtcNow().AddHours(1), justification: "Incident 77 escalation", kind: GrantKind.BreakGlass);
        Assert.AreEqual("refused:Invalid", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(owner, direct)));

        var granter = fixture.User("granter");
        var role = await fixture.CustomRoleAsync("granter", AccessPermissions.AccessManage, AccessPermissions.AccessBreakGlass, AccessPermissions.ConversationRead);
        await fixture.GrantAsync(granter.UserId, role);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.ActivateBreakGlassAsync(
            new BreakGlassRequest(granter, new GrantId("bg-granter"), BuiltInRoles.Supervisor.Id, fixture.Workspace, TimeSpan.FromMinutes(30), "Incident 78 needs supervisor access"))));
        Assert.AreEqual("allowed:break-glass", await fixture.DescribeAsync(granter, AccessPermissions.ConversationReply));

        AccessGrant Grant(string id, AccessRole granted) => new(new GrantId(id), fixture.Partition, new UserId("newcomer"), granted.Id, fixture.Workspace);
        var escalation = (TicketFailure)(await fixture.Admin.CreateGrantAsync(granter, Grant("sup", BuiltInRoles.Supervisor))).Value!;
        Assert.AreEqual(TicketFailureKind.Forbidden, escalation.Kind);
        CollectionAssert.Contains(escalation.Errors.ToArray(), "The actor does not hold 'conversation:reply' on the target.");
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(granter, new AccessRole(new RoleId("replier"), "x", [AccessPermissions.ConversationReply]))));
        var reader = new AccessRole(new RoleId("reader"), "Reader", [AccessPermissions.ConversationRead]);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(granter, reader)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(granter, Grant("reader", reader))));
    }

    [TestMethod]
    public async Task FieldPoliciesAreAdministeredWithAuthorizationValidationAndAudit()
    {
        var (fixture, _, admin, agent) = await CreateAsync();
        using var _ = fixture;
        var email = new AccessFieldPolicy(new FieldPath("contact.email"), [AccessPermissions.ContactRead], [BuiltInRoles.Supervisor.Id], AccessPermissions.ContactReveal);
        var before = fixture.Audit.Snapshot().Count;
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateFieldPolicyAsync(agent, email)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateFieldPolicyAsync(admin, email)));
        Assert.AreEqual("refused:Conflict:1", AccessFixture.Describe(await fixture.Admin.CreateFieldPolicyAsync(admin, email)));
        var unknown = new AccessFieldPolicy(new FieldPath("contact.phone"), [new Permission("contact:peek")], [], new Permission("contact:unmask"));
        var invalid = (TicketFailure)(await fixture.Admin.CreateFieldPolicyAsync(admin, unknown)).Value!;
        CollectionAssert.AreEqual(new[] { "Unknown permission 'contact:peek'.", "Unknown permission 'contact:unmask'." }, invalid.Errors.ToArray());
        var relaxed = new AccessFieldPolicy(email.Field, [AccessPermissions.ContactRead], [BuiltInRoles.Agent.Id]);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.UpdateFieldPolicyAsync(agent, relaxed, 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateFieldPolicyAsync(admin, relaxed, 1)));
        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.UpdateFieldPolicyAsync(admin, email, 1)));
        Assert.AreEqual(relaxed, (await fixture.Policies.GetAsync(fixture.Scope, email.Field))!.Policy);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.DeleteFieldPolicyAsync(agent, email.Field, 2)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.DeleteFieldPolicyAsync(admin, email.Field, 2)));
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.DeleteFieldPolicyAsync(admin, email.Field, 2)));

        var events = fixture.Audit.Snapshot().Skip(before).ToList();
        Assert.HasCount(10, events);
        Assert.AreEqual(3, events.Count(e => e.Outcome == AccessAuditOutcome.Allowed));
        Assert.IsTrue(events.All(e => e.Kind is AccessAuditKind.FieldPolicyCreated or AccessAuditKind.FieldPolicyUpdated or AccessAuditKind.FieldPolicyDeleted));
        Assert.IsTrue(events.All(e => e.Field is not null));
    }

    [TestMethod]
    public async Task AdministrativeAuditIsRecordedAfterTheWrite()
    {
        // Documented limitation: the store write and the audit record are not one transaction.
        var sink = new SwitchableAuditSink();
        var (fixture, _, admin, _) = await CreateAsync(services => services.AddSingleton<IAccessAuditSink>(sink));
        using var _ = fixture;
        sink.Fail = true;
        var grant = new AccessGrant(new GrantId("unaudited"), fixture.Partition, new UserId("x"), BuiltInRoles.Viewer.Id, fixture.Workspace);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Admin.CreateGrantAsync(admin, grant));
        Assert.AreEqual(grant, (await fixture.Grants.GetAsync(fixture.Partition, grant.Id))!.Grant);
        Assert.IsEmpty(sink.Events);
    }
}
