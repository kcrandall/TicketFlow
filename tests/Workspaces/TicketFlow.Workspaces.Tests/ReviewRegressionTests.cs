using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces.StoreContracts;
using static TicketFlow.Workspaces.Tests.WorkspaceFixture;

namespace TicketFlow.Workspaces.Tests;

/// <summary>Regression tests for the E3 post-ticket review findings.</summary>
[TestClass]
public sealed class ReviewRegressionTests
{
    [TestMethod]
    public async Task RemovingMembersFromADeniedTeamNeedsRevokeAuthority()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var mallory = fixture.User("mallory", "contractors");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        var teamLead = await fixture.CustomRoleAsync("team-lead", AccessPermissions.TeamManage, AccessPermissions.TeamRead);
        await fixture.GrantAsync(mallory.UserId, teamLead, fixture.TeamScope("contractors"));
        await fixture.GrantAsync(mallory.UserId, BuiltInRoles.Agent);
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("contractors"), "Contractors", null, [mallory.UserId, new UserId("bob")])));
        await fixture.GrantAsync(new TeamId("contractors"), BuiltInRoles.Agent, effect: GrantEffect.Deny);
        Assert.IsFalse((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(mallory, AccessPermissions.ConversationReply, WorkspaceResources.Workspace(fixture.Scope)))).IsPermitted);

        // Self-removal (or removing anyone) would lift the deny: it needs access:manage like revoking the grant.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.RemoveMemberAsync(mallory, new TeamId("contractors"), mallory.UserId, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.UpdateAsync(mallory, new Team(new TeamId("contractors"), "Contractors", null, [new UserId("bob")]), 1)));
        // Renaming and adding members do not lift the deny.
        Assert.AreEqual(2L, Value(await fixture.Teams.UpdateAsync(mallory, new Team(new TeamId("contractors"), "Renamed", null, [mallory.UserId, new UserId("bob")]), 1)).Revision);
        Assert.AreEqual(3L, Value(await fixture.Teams.RemoveMemberAsync(admin, new TeamId("contractors"), mallory.UserId, 2)).Revision);
    }

    [TestMethod]
    public async Task ExpiredDenyGrantsDoNotBlockRemoval()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var supervisor = fixture.User("supervisor");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("night"), "Night", null, [new UserId("ada")])));
        var now = fixture.Clock.GetUtcNow();
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await fixture.Grants.CreateAsync(new AccessGrant(new GrantId("old-deny"), fixture.Partition,
            new TeamId("night"), BuiltInRoles.Agent.Id, fixture.Workspace, GrantEffect.Deny, validFrom: now.AddDays(-2), validUntil: now.AddDays(-1)))).Value);
        Assert.AreEqual(2L, Value(await fixture.Teams.RemoveMemberAsync(supervisor, new TeamId("night"), new UserId("ada"), 1)).Revision);
    }

    [TestMethod]
    public async Task DeletingATeamNeedsAuthorityOverAndRevokesItsGrants()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var supervisor = fixture.User("supervisor");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("contractors"), "Contractors", null, [new UserId("ada")])));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("plain"), "Plain")));
        var deny = await fixture.GrantAsync(new TeamId("contractors"), BuiltInRoles.Agent, effect: GrantEffect.Deny);
        var allow = await fixture.GrantAsync(new TeamId("contractors"), BuiltInRoles.Viewer, fixture.TeamScope("contractors"));
        var unrelated = await fixture.GrantAsync(new UserId("ada"), BuiltInRoles.Viewer);

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.DeleteAsync(supervisor, new TeamId("contractors"), 1)));
        Assert.IsNotNull(await fixture.TeamStore.GetAsync(fixture.Scope, new TeamId("contractors")));
        Assert.AreEqual("ok", Describe(await fixture.Teams.DeleteAsync(supervisor, new TeamId("plain"), 1)));

        var auditBefore = fixture.Audit.Snapshot().Count;
        Assert.AreEqual("ok", Describe(await fixture.Teams.DeleteAsync(admin, new TeamId("contractors"), 1)));
        Assert.IsNull(await fixture.Grants.GetAsync(fixture.Partition, deny.Id));
        Assert.IsNull(await fixture.Grants.GetAsync(fixture.Partition, allow.Id));
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, unrelated.Id));
        Assert.AreEqual(2, fixture.Audit.Snapshot().Skip(auditBefore).Count(e => e.Kind == AccessAuditKind.GrantRevoked && e.Outcome == AccessAuditOutcome.Allowed));
        Assert.IsEmpty(await fixture.Resolver.GetTeamsAsync(fixture.Scope, new UserId("ada")));
    }

    [TestMethod]
    public async Task BreakGlassNeverAuthorizesConferringTeamGrants()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var responder = fixture.User("responder");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(responder.UserId, BuiltInRoles.Viewer);
        var now = fixture.Clock.GetUtcNow();
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await fixture.Grants.CreateAsync(new AccessGrant(new GrantId("bg"), fixture.Partition, responder.UserId,
            BuiltInRoles.Supervisor.Id, fixture.Workspace, validFrom: now, validUntil: now.AddHours(1), justification: "Incident 12 staffing", kind: GrantKind.BreakGlass))).Value);
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("agents"), "Agents")));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("empty"), "Empty")));
        await fixture.GrantAsync(new TeamId("agents"), BuiltInRoles.Agent);

        // Break-glass supplies team:manage, but conferring the team's Agent grant counts standard grants only.
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(responder, new TeamId("empty"), new UserId("ada"), 1)).Revision);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(responder, new TeamId("agents"), responder.UserId, 1)));
    }

    [TestMethod]
    public async Task WorkspaceOwnersCannotConferTenantGrantsThroughTeams()
    {
        using var fixture = Create();
        var owner = fixture.User("owner");
        var root = fixture.User("root");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner);
        await fixture.GrantAsync(root.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        Value(await fixture.Teams.CreateAsync(owner, new Team(new TeamId("platform"), "Platform")));
        await fixture.GrantAsync(new TeamId("platform"), BuiltInRoles.Owner, TenantGrantScope.Instance);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(owner, new TeamId("platform"), owner.UserId, 1)));
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(root, new TeamId("platform"), owner.UserId, 1)).Revision);
    }

    [TestMethod]
    public async Task MembersOwnTeamConditionedGrantsCountAsConferred()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var lead = fixture.User("lead");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        var teamLead = await fixture.CustomRoleAsync("team-lead", AccessPermissions.TeamManage, AccessPermissions.TeamRead);
        await fixture.GrantAsync(lead.UserId, teamLead, fixture.TeamScope("vip"));
        await fixture.GrantAsync(lead.UserId, teamLead, fixture.TeamScope("billing"));
        await fixture.GrantAsync(lead.UserId, BuiltInRoles.Agent, fixture.TeamScope("billing"));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("vip"), "VIP")));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("billing"), "Billing")));
        // Ada may work on conversations of any team she belongs to.
        await fixture.GrantAsync(new UserId("ada"), BuiltInRoles.Agent, condition: new TeamInPrincipalTeamsCondition());

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(lead, new TeamId("vip"), new UserId("ada"), 1)));
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(lead, new TeamId("billing"), new UserId("ada"), 1)).Revision);
        // Members without such grants are unaffected.
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(lead, new TeamId("vip"), new UserId("bob"), 1)).Revision);

        // A member's own team-conditioned deny is lifted by removal, so removal needs revoke authority.
        await fixture.GrantAsync(new UserId("carl"), BuiltInRoles.Viewer, effect: GrantEffect.Deny, condition: new TeamInPrincipalTeamsCondition());
        Assert.AreEqual(3L, Value(await fixture.Teams.AddMemberAsync(admin, new TeamId("billing"), new UserId("carl"), 2)).Revision);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.RemoveMemberAsync(lead, new TeamId("billing"), new UserId("carl"), 3)));
        Assert.AreEqual(4L, Value(await fixture.Teams.RemoveMemberAsync(admin, new TeamId("billing"), new UserId("carl"), 3)).Revision);
    }

    [TestMethod]
    public async Task DeletingAnInboxRevokesItsAgentGrants()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var inboxAdmin = fixture.User("inbox-admin");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        var inboxManager = await fixture.CustomRoleAsync("inbox-manager", AccessPermissions.InboxManage, AccessPermissions.InboxRead);
        await fixture.GrantAsync(inboxAdmin.UserId, inboxManager);
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("email")));
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("chat")));
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("empty")));
        var ada = Value(await fixture.Inboxes.AddAgentAsync(admin, new InboxId("email"), new UserId("ada")));
        var bob = Value(await fixture.Inboxes.AddAgentAsync(admin, new InboxId("email"), new UserId("bob"), BuiltInRoles.Viewer.Id));
        var chatAgent = Value(await fixture.Inboxes.AddAgentAsync(admin, new InboxId("chat"), new UserId("ada")));
        var manual = await fixture.GrantAsync(new TeamId("billing"), BuiltInRoles.Viewer, fixture.InboxScope("email"));

        // Revoking agent grants needs access:manage on the inbox.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Inboxes.DeleteAsync(inboxAdmin, new InboxId("email"), 1)));
        Assert.AreEqual("ok", Describe(await fixture.Inboxes.DeleteAsync(inboxAdmin, new InboxId("empty"), 1)));
        Assert.AreEqual("refused:Conflict:1", Describe(await fixture.Inboxes.DeleteAsync(admin, new InboxId("email"), 7)));

        Assert.AreEqual("ok", Describe(await fixture.Inboxes.DeleteAsync(admin, new InboxId("email"), 1)));
        Assert.IsNull(await fixture.Grants.GetAsync(fixture.Partition, ada.Grant.Id));
        Assert.IsNull(await fixture.Grants.GetAsync(fixture.Partition, bob.Grant.Id));
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, chatAgent.Grant.Id));
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, manual.Id));
        // Recreating the inbox does not revive the old agents.
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("email")));
        Assert.IsFalse((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(fixture.User("ada"), AccessPermissions.ConversationReply,
            WorkspaceResources.Inbox(fixture.Scope, new InboxId("email"))))).IsPermitted);
    }

    [TestMethod]
    public void WorkspacePermissionsAreRegisteredOnce()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow().AddWorkspaces().AddWorkspaces();
        var registrations = services.Where(descriptor => descriptor.ServiceType == typeof(AccessPermissionRegistration)).ToList();
        Assert.HasCount(1, registrations);
        Assert.AreEqual("TicketFlow.Workspaces", typeof(WorkspacesTicketFlowBuilderExtensions).Namespace);
    }
}
