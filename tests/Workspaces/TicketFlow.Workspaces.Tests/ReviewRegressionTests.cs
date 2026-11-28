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

    [TestMethod]
    public async Task RemovingMemberOrDeletingTeamWithUnheldDenyGrantIsRefused()
    {
        using var fixture = Create();
        var owner = fixture.User("owner");
        var admin = fixture.User("admin");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner);
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);

        var restricted = new TeamId("restricted");
        var conditioned = new TeamId("conditioned");
        Value(await fixture.Teams.CreateAsync(admin, new Team(restricted, "Restricted", null, [new UserId("bob")])));
        Value(await fixture.Teams.CreateAsync(admin, new Team(conditioned, "Conditioned", null, [new UserId("carl")])));
        await fixture.GrantAsync(restricted, BuiltInRoles.Owner, effect: GrantEffect.Deny);
        await fixture.GrantAsync(new UserId("carl"), BuiltInRoles.Owner, effect: GrantEffect.Deny, condition: new TeamInPrincipalTeamsCondition());

        // Admin holds team:manage and access:manage, but not workspace:manage (covered by Owner deny).
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.RemoveMemberAsync(admin, restricted, new UserId("bob"), 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.UpdateAsync(admin, new Team(restricted, "Restricted", null, []), 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.DeleteAsync(admin, restricted, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.RemoveMemberAsync(admin, conditioned, new UserId("carl"), 1)));

        // Workspace-only Owner does not hold access:manage-tenant (RequiresTenantAllowOnly) and cannot lift Deny(Owner).
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.RemoveMemberAsync(owner, restricted, new UserId("bob"), 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.DeleteAsync(owner, restricted, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.RemoveMemberAsync(owner, conditioned, new UserId("carl"), 1)));

        // Tenant Owner holds all denied permissions (including access:manage-tenant) and succeeds.
        var tenantOwner = fixture.User("tenant-owner");
        await fixture.GrantAsync(tenantOwner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        Assert.AreEqual(2L, Value(await fixture.Teams.RemoveMemberAsync(tenantOwner, restricted, new UserId("bob"), 1)).Revision);
        Assert.AreEqual("ok", Describe(await fixture.Teams.DeleteAsync(tenantOwner, restricted, 2)));
        Assert.AreEqual(2L, Value(await fixture.Teams.RemoveMemberAsync(tenantOwner, conditioned, new UserId("carl"), 1)).Revision);
    }

    [TestMethod]
    public async Task ConditionalLiveDenyGrantsBlockTeamGrantPolicyMemberRemoval()
    {
        using var fixture = Create();
        var now = fixture.Clock.GetUtcNow();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner);

        var teamAssignee = new TeamId("cond-assignee");
        var teamPrincipal = new TeamId("cond-team");
        var teamWindow = new TeamId("cond-window");
        var teamUserDeny = new TeamId("cond-user-deny");
        var admin = fixture.User("admin", teamAssignee.Value, teamPrincipal.Value, teamWindow.Value, teamUserDeny.Value);
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);

        var offWindow = new TimeWindowCondition(
            TimeOnly.FromTimeSpan(now.AddHours(2).UtcDateTime.TimeOfDay),
            TimeOnly.FromTimeSpan(now.AddHours(4).UtcDateTime.TimeOfDay));
        Assert.IsFalse(offWindow.Contains(now));

        foreach (var teamId in new[] { teamAssignee, teamPrincipal, teamWindow, teamUserDeny })
            Value(await fixture.Teams.CreateAsync(owner, new Team(teamId, teamId.Value, null, [admin.UserId])));

        await fixture.GrantAsync(teamAssignee, BuiltInRoles.Agent, effect: GrantEffect.Deny, condition: new AssigneeIsSelfCondition());
        await fixture.GrantAsync(teamPrincipal, BuiltInRoles.Agent, effect: GrantEffect.Deny, condition: new TeamInPrincipalTeamsCondition());
        await fixture.GrantAsync(teamWindow, BuiltInRoles.Agent, effect: GrantEffect.Deny, condition: offWindow);
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Agent, effect: GrantEffect.Deny,
            condition: new AndCondition(new TeamInPrincipalTeamsCondition(), offWindow));

        // Admin cannot remove themselves from any team to lift conditional live Deny grants (assignee, principal-teams, or off-window time-window).
        foreach (var teamId in new[] { teamAssignee, teamPrincipal, teamWindow, teamUserDeny })
        {
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.RemoveMemberAsync(admin, teamId, admin.UserId, 1)), teamId.Value);
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.UpdateAsync(admin, new Team(teamId, teamId.Value, null, []), 1)), teamId.Value);
        }
        foreach (var teamId in new[] { teamAssignee, teamPrincipal, teamWindow })
            Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.DeleteAsync(admin, teamId, 1)), teamId.Value);

        // Unrestricted owner can remove admin from all of these teams.
        foreach (var teamId in new[] { teamAssignee, teamPrincipal, teamWindow, teamUserDeny })
            Assert.AreEqual(2L, Value(await fixture.Teams.RemoveMemberAsync(owner, teamId, admin.UserId, 1)).Revision, teamId.Value);
    }

    [TestMethod]
    public async Task AddingMembersToTeamMappedToFederatedRoleEnforcesConferral()
    {
        using var fixture = Create(options =>
        {
            options.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
            options.RoleFederation.MapTeamsAsExternalRoles = true;
            options.RoleFederation.AllowAdministrativeRoles = true;
            options.RoleFederation.StaticMappings["elevated-admins"] = BuiltInRoles.Admin.Id;
        });

        var mappingStore = fixture.Provider.GetRequiredService<IRoleFederationMappingStore>();
        Assert.IsTrue((await mappingStore.UpsertAsync(
            new RoleFederationMappingId("team-tenant-owners"),
            new RoleFederationMapping("local", "tenant-owners", BuiltInRoles.Owner.Id, fixture.Partition, TenantGrantScope.Instance),
            isEnabled: true,
            actorPrincipalId: "seed",
            expectedVersion: 0)).IsSuccess);

        var tenantOwner = fixture.User("tenant-owner");
        var wsOwner = fixture.User("ws-owner");
        var admin = fixture.User("admin");
        var teamManager = fixture.User("team-manager");
        await fixture.GrantAsync(tenantOwner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(wsOwner.UserId, BuiltInRoles.Owner);
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        var teamMgrRole = await fixture.CustomRoleAsync("team-mgr-only", AccessPermissions.TeamManage, AccessPermissions.TeamRead);
        await fixture.GrantAsync(teamManager.UserId, teamMgrRole);

        var elevatedAdmins = new TeamId("elevated-admins");
        var tenantOwners = new TeamId("tenant-owners");

        // Low-privilege team manager CANNOT create the mapped team with initial members (self or another user).
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.CreateAsync(teamManager, new Team(elevatedAdmins, "Elevated Admins", null, [teamManager.UserId]))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.CreateAsync(teamManager, new Team(elevatedAdmins, "Elevated Admins", null, [new UserId("bob")]))));

        // Creating the teams without members succeeds, then adding members enforces team-federated role conferral.
        Value(await fixture.Teams.CreateAsync(admin, new Team(elevatedAdmins, "Elevated Admins")));
        Value(await fixture.Teams.CreateAsync(tenantOwner, new Team(tenantOwners, "Tenant Owners")));

        // Low-privilege team manager CANNOT add themselves or another user via AddMemberAsync or UpdateAsync.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(teamManager, elevatedAdmins, teamManager.UserId, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(teamManager, elevatedAdmins, new UserId("bob"), 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.UpdateAsync(teamManager, new Team(elevatedAdmins, "Elevated Admins", null, [teamManager.UserId]), 1)));

        // Admin holds all permissions of Admin and CAN add members to the statically mapped Admin team.
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(admin, elevatedAdmins, new UserId("bob"), 1)).Revision);

        // Team mapped to TenantGrantScope Owner: teamManager, admin, AND workspace-only Owner are all refused; only Tenant Owner succeeds.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(teamManager, tenantOwners, teamManager.UserId, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(admin, tenantOwners, new UserId("bob"), 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(wsOwner, tenantOwners, wsOwner.UserId, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.UpdateAsync(wsOwner, new Team(tenantOwners, "Tenant Owners", null, [new UserId("bob")]), 1)));
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(tenantOwner, tenantOwners, new UserId("bob"), 1)).Revision);
    }
}
