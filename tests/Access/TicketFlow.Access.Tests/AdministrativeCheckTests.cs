using TicketFlow.Core;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class AdministrativeCheckTests
{
    private static string Describe(AccessCheckResult result) => result switch
    {
        AccessCheckPassed => "passed",
        TicketFailure refused => $"refused:{refused.Kind}",
    };

    [TestMethod]
    public async Task ConferRequiresHoldingPermissionsFromStandardGrants()
    {
        using var fixture = AccessFixture.Create();
        var supervisor = fixture.User("sup");
        var owner = fixture.User("owner");
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner);
        var adminToTeam = new AccessGrant(new GrantId("team-admin"), fixture.Partition, new TeamId("admins"), BuiltInRoles.Admin.Id, fixture.Workspace);
        var agentToTeam = new AccessGrant(new GrantId("team-agent"), fixture.Partition, new TeamId("agents"), BuiltInRoles.Agent.Id, fixture.Workspace);
        var auditBefore = fixture.Audit.Snapshot().Count;

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanConferAsync(supervisor, [new(adminToTeam)])));
        Assert.AreEqual("passed", Describe(await fixture.Admin.CheckCanConferAsync(supervisor, [new(agentToTeam)])));
        Assert.AreEqual("passed", Describe(await fixture.Admin.CheckCanConferAsync(owner, [new(adminToTeam), new(agentToTeam)])));
        // Deny and expired grants confer nothing.
        var denyAdmin = new AccessGrant(new GrantId("deny"), fixture.Partition, new TeamId("admins"), BuiltInRoles.Owner.Id, fixture.Workspace, GrantEffect.Deny);
        var expired = new AccessGrant(new GrantId("old"), fixture.Partition, new TeamId("admins"), BuiltInRoles.Owner.Id, fixture.Workspace,
            validFrom: fixture.Clock.GetUtcNow().AddDays(-2), validUntil: fixture.Clock.GetUtcNow().AddDays(-1));
        Assert.AreEqual("passed", Describe(await fixture.Admin.CheckCanConferAsync(supervisor, [new(denyAdmin), new(expired)])));
        // Checks never audit, even though access:manage-style permissions are sensitive.
        Assert.AreEqual(auditBefore, fixture.Audit.Snapshot().Count);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanConferAsync(Testing.TestPrincipals.Automation(fixture.Scope), [new(agentToTeam)])));
    }

    [TestMethod]
    public async Task ConferIgnoresBreakGlassAndNeedsTenantAuthorityForTenantGrants()
    {
        using var fixture = AccessFixture.Create();
        var responder = fixture.User("responder");
        await fixture.GrantAsync(responder.UserId, BuiltInRoles.Viewer);
        await fixture.SeedAsync(new AccessGrant(new GrantId("bg"), fixture.Partition, responder.UserId, BuiltInRoles.Supervisor.Id, fixture.Workspace,
            validFrom: fixture.Clock.GetUtcNow(), validUntil: fixture.Clock.GetUtcNow().AddHours(1), justification: "Incident 7 coverage", kind: GrantKind.BreakGlass));
        Assert.IsTrue((await fixture.AuthorizeAsync(responder, AccessPermissions.ConversationReply)).IsPermitted);
        var agentToTeam = new AccessGrant(new GrantId("team-agent"), fixture.Partition, new TeamId("agents"), BuiltInRoles.Agent.Id, fixture.Workspace);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanConferAsync(responder, [new(agentToTeam)])));

        var workspaceOwner = fixture.User("owner");
        await fixture.GrantAsync(workspaceOwner.UserId, BuiltInRoles.Owner);
        var tenantOwnerToTeam = new AccessGrant(new GrantId("tenant-owner"), fixture.Partition, new TeamId("root"), BuiltInRoles.Owner.Id, TenantGrantScope.Instance);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanConferAsync(workspaceOwner, [new(tenantOwnerToTeam)])));
        var tenantOwner = fixture.User("root");
        await fixture.GrantAsync(tenantOwner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        Assert.AreEqual("passed", Describe(await fixture.Admin.CheckCanConferAsync(tenantOwner, [new(tenantOwnerToTeam)])));
    }

    [TestMethod]
    public async Task ConferTargetsCanBeNarrowedAndMustStayInScope()
    {
        using var fixture = AccessFixture.Create();
        var lead = fixture.User("lead");
        await fixture.GrantAsync(lead.UserId, BuiltInRoles.Agent, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        var conditional = new AccessGrant(new GrantId("own"), fixture.Partition, new UserId("ada"), BuiltInRoles.Agent.Id, fixture.Workspace, condition: new TeamInPrincipalTeamsCondition());
        Assert.AreEqual("passed", Describe(await fixture.Admin.CheckCanConferAsync(lead, [new(conditional, new AccessResource(fixture.Scope, team: new TeamId("billing")))])));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanConferAsync(lead, [new(conditional, new AccessResource(fixture.Scope, team: new TeamId("sales")))])));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanConferAsync(lead, [new(conditional)])));
        var elsewhere = Testing.TestScopes.Create(workspaceId: "elsewhere");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanConferAsync(lead, [new(conditional, new AccessResource(elsewhere, team: new TeamId("billing")))])));
    }

    [TestMethod]
    public async Task RevokeChecksMirrorRevokeGrantAuthority()
    {
        using var fixture = AccessFixture.Create();
        var admin = fixture.User("admin");
        var supervisor = fixture.User("sup");
        var owner = fixture.User("owner");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner);
        var teamDeny = new AccessGrant(new GrantId("deny"), fixture.Partition, new TeamId("contractors"), BuiltInRoles.Viewer.Id, fixture.Workspace, GrantEffect.Deny);
        var tenantDeny = new AccessGrant(new GrantId("tenant-deny"), fixture.Partition, new TeamId("contractors"), BuiltInRoles.Viewer.Id, TenantGrantScope.Instance, GrantEffect.Deny);
        Assert.AreEqual("passed", Describe(await fixture.Admin.CheckCanRevokeAsync(supervisor, [])));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanRevokeAsync(supervisor, [teamDeny])));
        Assert.AreEqual("passed", Describe(await fixture.Admin.CheckCanRevokeAsync(admin, [teamDeny])));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanRevokeAsync(owner, [teamDeny, tenantDeny])));
        var foreign = new AccessGrant(new GrantId("foreign"), fixture.Partition, new TeamId("x"), BuiltInRoles.Viewer.Id, new WorkspaceGrantScope(new WorkspaceId("elsewhere")));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Admin.CheckCanRevokeAsync(owner, [foreign])));
    }
}
