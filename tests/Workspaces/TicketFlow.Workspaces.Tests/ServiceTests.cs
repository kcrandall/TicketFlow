using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces.StoreContracts;
using static TicketFlow.Workspaces.Tests.WorkspaceFixture;

namespace TicketFlow.Workspaces.Tests;

[TestClass]
public sealed class ServiceTests
{
    [TestMethod]
    public async Task SettingsRequireReadAndManagePermissions()
    {
        using var fixture = Create();
        var owner = fixture.User("owner");
        var admin = fixture.User("admin");
        var agent = fixture.User("agent");
        var stranger = fixture.User("stranger");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner);
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent);
        var settings = new WorkspaceSettings("Acme", "en", "America/New_York", [new("csat", true)]);

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Settings.GetAsync(stranger)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Settings.GetAsync(agent)));
        // Admin lacks workspace:manage by design; Owner holds it.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Settings.CreateAsync(admin, settings)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Settings.CreateAsync(agent, settings)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Settings.CreateAsync(owner, new WorkspaceSettings("Acme", "en", "Nowhere/Atlantis"))));
        Assert.AreEqual("ok", Describe(await fixture.Settings.CreateAsync(owner, settings)));
        Assert.AreEqual("refused:Conflict:1", Describe(await fixture.Settings.CreateAsync(owner, settings)));
        Assert.AreEqual(settings, Value(await fixture.Settings.GetAsync(agent)).Settings);
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Settings.UpdateAsync(owner, settings, 0)));
        Assert.AreEqual(2L, Value(await fixture.Settings.UpdateAsync(owner, new WorkspaceSettings("Acme 2", "en", "UTC"), 1)).Revision);
        Assert.AreEqual("refused:Conflict:2", Describe(await fixture.Settings.UpdateAsync(owner, settings, 1)));
        // Another workspace's principal never sees these settings.
        var elsewhere = Testing.TestPrincipals.User(Testing.TestScopes.Create(workspaceId: "elsewhere"), "owner");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Settings.GetAsync(elsewhere)));
    }

    [TestMethod]
    public async Task InboxListsAreFilteredByAccessAndPointChecksAgree()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        foreach (var id in new[] { "chat", "email", "sms", "vip" })
            Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox(id)));

        var agent = fixture.User("agent", "billing");
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent, fixture.InboxScope("email"));
        await fixture.GrantAsync(new TeamId("billing"), BuiltInRoles.Viewer, fixture.InboxScope("sms"));
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent, fixture.InboxScope("vip"));
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Viewer, fixture.InboxScope("vip"), GrantEffect.Deny);

        var listed = Value(await fixture.Inboxes.ListAsync(agent)).Items.Select(inbox => inbox.Inbox.Id.Value).ToArray();
        CollectionAssert.AreEqual(new[] { "email", "sms" }, listed);
        foreach (var id in new[] { "chat", "email", "sms", "vip" })
            Assert.AreEqual(listed.Contains(id) ? "ok" : "refused:Forbidden", Describe(await fixture.Inboxes.GetAsync(agent, new InboxId(id))), id);
        CollectionAssert.AreEqual(new[] { "chat", "email", "sms", "vip" }, Value(await fixture.Inboxes.ListAsync(admin)).Items.Select(inbox => inbox.Inbox.Id.Value).ToArray());
        var paged = Value(await fixture.Inboxes.ListAsync(admin, new(3)));
        Assert.IsTrue(paged.HasMore);
        Assert.HasCount(3, paged.Items);

        var stranger = fixture.User("stranger");
        Assert.IsEmpty(Value(await fixture.Inboxes.ListAsync(stranger)).Items);
        // Automation principals hold no grants and see nothing.
        Assert.IsEmpty(Value(await fixture.Inboxes.ListAsync(Testing.TestPrincipals.Automation(fixture.Scope))).Items);
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Inboxes.GetAsync(admin, new InboxId("missing"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Inboxes.GetAsync(stranger, new InboxId("missing"))));
    }

    [TestMethod]
    public async Task InboxManagementScopesAndCalendarReferences()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var lead = fixture.User("lead");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        var inboxManager = await fixture.CustomRoleAsync("inbox-manager", AccessPermissions.InboxManage, AccessPermissions.InboxRead);
        await fixture.GrantAsync(lead.UserId, inboxManager, fixture.InboxScope("email"));

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Inboxes.CreateAsync(lead, WorkspaceStoreContract.SampleInbox("email"))));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("email", calendar: new BusinessCalendarId("office")))));
        Value(await fixture.Calendars.CreateAsync(admin, WorkspaceStoreContract.SampleCalendar("office")));
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("email", calendar: new BusinessCalendarId("office"))));
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("chat")));

        // An inbox-scoped manage grant updates only its inbox and cannot delete.
        Assert.AreEqual(2L, Value(await fixture.Inboxes.UpdateAsync(lead, WorkspaceStoreContract.SampleInbox("email", "Renamed"), 1)).Revision);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Inboxes.UpdateAsync(lead, WorkspaceStoreContract.SampleInbox("chat", "Renamed"), 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Inboxes.DeleteAsync(lead, new InboxId("email"), 2)));
        Assert.AreEqual("refused:Conflict:2", Describe(await fixture.Inboxes.UpdateAsync(lead, WorkspaceStoreContract.SampleInbox("email"), 1)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Inboxes.UpdateAsync(lead, WorkspaceStoreContract.SampleInbox("email", calendar: new BusinessCalendarId("missing")), 2)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Inboxes.UpdateAsync(admin, WorkspaceStoreContract.SampleInbox("missing"), 1)));
        Assert.AreEqual("ok", Describe(await fixture.Inboxes.DeleteAsync(admin, new InboxId("chat"), 1)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Inboxes.DeleteAsync(admin, new InboxId("chat"), 1)));
    }

    [TestMethod]
    public async Task InboxAgentsAreInboxScopedGrantsWrittenThroughTheAdministrator()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var supervisor = fixture.User("supervisor");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("email")));
        Value(await fixture.Inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("chat")));
        var ada = fixture.User("ada");
        var email = new InboxId("email");
        var inEmail = WorkspaceResources.Inbox(fixture.Scope, email);

        Assert.IsFalse((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(ada, AccessPermissions.ConversationReply, inEmail))).IsPermitted);
        // Supervisors lack inbox:manage; unknown inboxes are NotFound only after authorization.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Inboxes.AddAgentAsync(supervisor, email, ada.UserId)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Inboxes.AddAgentAsync(admin, new InboxId("missing"), ada.UserId)));

        var auditBefore = fixture.Audit.Snapshot().Count;
        var grant = Value(await fixture.Inboxes.AddAgentAsync(admin, email, ada.UserId));
        Assert.AreEqual(InboxAgentGrants.For(fixture.Scope.WorkspaceId, email, ada.UserId, BuiltInRoles.Agent.Id), grant.Grant.Id);
        Assert.AreEqual(new InboxGrantScope(fixture.Scope.WorkspaceId, email), grant.Grant.Scope);
        Assert.IsTrue(fixture.Audit.Snapshot().Skip(auditBefore).Any(e => e.Kind == AccessAuditKind.GrantCreated && e.GrantId == grant.Grant.Id));
        Assert.AreEqual("refused:Conflict:1", Describe(await fixture.Inboxes.AddAgentAsync(admin, email, ada.UserId)));

        Assert.IsTrue((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(ada, AccessPermissions.ConversationReply, inEmail))).IsPermitted);
        Assert.IsFalse((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(ada, AccessPermissions.ConversationReply, WorkspaceResources.Inbox(fixture.Scope, new InboxId("chat"))))).IsPermitted);
        CollectionAssert.AreEqual(new[] { "email" }, Value(await fixture.Inboxes.ListAsync(ada)).Items.Select(inbox => inbox.Inbox.Id.Value).ToArray());

        // The administrator's escalation rule applies: an admin cannot grant Owner on the inbox.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Inboxes.AddAgentAsync(admin, email, new UserId("bob"), BuiltInRoles.Owner.Id)));

        Assert.AreEqual("refused:NotFound", Describe(await fixture.Inboxes.RemoveAgentAsync(admin, email, ada.UserId, BuiltInRoles.Viewer.Id)));
        Assert.AreEqual("ok", Describe(await fixture.Inboxes.RemoveAgentAsync(admin, email, ada.UserId)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Inboxes.RemoveAgentAsync(admin, email, ada.UserId)));
        Assert.IsFalse((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(ada, AccessPermissions.ConversationReply, inEmail))).IsPermitted);
    }

    [TestMethod]
    public async Task TeamListsAndMembershipResolveIntoPrincipals()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        var ada = new UserId("ada");
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("billing"), "Billing", null, [ada])));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("sales"), "Sales")));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("support"), "Support", null, [ada])));
        await fixture.GrantAsync(new TeamId("billing"), BuiltInRoles.Agent, fixture.TeamScope("billing"));

        var principal = await fixture.Resolver.ResolveUserPrincipalAsync(fixture.Scope, ada);
        CollectionAssert.AreEquivalent(new[] { new TeamId("billing"), new TeamId("support") }, principal.Teams.ToArray());
        // The team grant applies through the resolved snapshot.
        Assert.IsTrue((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(principal, AccessPermissions.ConversationReply, new AccessResource(fixture.Scope, team: new TeamId("billing"))))).IsPermitted);
        Assert.IsFalse((await fixture.Evaluator.AuthorizeAsync(new AccessRequest(new UserPrincipal(fixture.Scope, ada), AccessPermissions.ConversationReply, new AccessResource(fixture.Scope, team: new TeamId("billing"))))).IsPermitted);
        CollectionAssert.AreEqual(new[] { "billing" }, Value(await fixture.Teams.ListAsync(principal)).Items.Select(team => team.Id.Value).ToArray());
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.GetAsync(principal, new TeamId("sales"))));
        Assert.AreEqual(1, Value(await fixture.Teams.GetAsync(principal, new TeamId("billing"))).Team.Members.Count);
        CollectionAssert.AreEqual(new[] { "billing", "sales", "support" }, Value(await fixture.Teams.ListAsync(admin)).Items.Select(team => team.Id.Value).ToArray());

        // Membership changes are revisioned; no-op changes keep the revision.
        var removed = Value(await fixture.Teams.RemoveMemberAsync(admin, new TeamId("support"), ada, 1));
        Assert.AreEqual(2L, removed.Revision);
        Assert.AreEqual(2L, Value(await fixture.Teams.RemoveMemberAsync(admin, new TeamId("support"), ada, 2)).Revision);
        Assert.AreEqual("refused:Conflict:2", Describe(await fixture.Teams.AddMemberAsync(admin, new TeamId("support"), ada, 1)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Teams.AddMemberAsync(admin, new TeamId("missing"), ada, 1)));
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(admin, new TeamId("sales"), ada, 1)).Revision);
        CollectionAssert.AreEquivalent(new[] { new TeamId("billing"), new TeamId("sales") }, (await fixture.Resolver.ResolveUserPrincipalAsync(fixture.Scope, ada)).Teams.ToArray());
        // The earlier snapshot is unchanged.
        CollectionAssert.AreEquivalent(new[] { new TeamId("billing"), new TeamId("support") }, principal.Teams.ToArray());
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.DeleteAsync(principal, new TeamId("sales"), 2)));
        Assert.AreEqual("ok", Describe(await fixture.Teams.DeleteAsync(admin, new TeamId("sales"), 2)));
        Assert.AreEqual(new[] { new TeamId("billing") }.Single(), (await fixture.Resolver.GetTeamsAsync(fixture.Scope, ada)).Single());
    }

    [TestMethod]
    public async Task TeamMembershipCannotEscalatePrivileges()
    {
        using var fixture = Create();
        var owner = fixture.User("owner");
        var supervisor = fixture.User("supervisor");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner);
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        Value(await fixture.Teams.CreateAsync(owner, new Team(new TeamId("admins"), "Admins")));
        Value(await fixture.Teams.CreateAsync(owner, new Team(new TeamId("agents"), "Agents")));
        await fixture.GrantAsync(new TeamId("admins"), BuiltInRoles.Admin);
        await fixture.GrantAsync(new TeamId("agents"), BuiltInRoles.Agent, fixture.TeamScope("agents"));
        // Deny grants never confer anything and do not block additions.
        await fixture.GrantAsync(new TeamId("agents"), BuiltInRoles.Viewer, effect: GrantEffect.Deny);

        // A supervisor (team:manage, but not Admin's permissions) cannot add itself or others to a team holding Admin.
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(supervisor, new TeamId("admins"), supervisor.UserId, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.UpdateAsync(supervisor, new Team(new TeamId("admins"), "Admins", null, [new UserId("mallory")]), 1)));
        // Grants may name a team before it exists: creating it with members is checked too.
        await fixture.GrantAsync(new TeamId("admins-2"), BuiltInRoles.Admin);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.CreateAsync(supervisor, new Team(new TeamId("admins-2"), "Pre-granted", null, [supervisor.UserId]))));
        Assert.AreEqual("ok", Describe(await fixture.Teams.CreateAsync(supervisor, new Team(new TeamId("admins-2"), "Pre-granted"))));
        // Adding to a team whose grants the supervisor already holds is fine; renaming without adding members needs no check.
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(supervisor, new TeamId("agents"), new UserId("bob"), 1)).Revision);
        Assert.AreEqual(2L, Value(await fixture.Teams.UpdateAsync(supervisor, new Team(new TeamId("admins"), "Administrators"), 1)).Revision);
        Assert.AreEqual(3L, Value(await fixture.Teams.AddMemberAsync(owner, new TeamId("admins"), new UserId("carol"), 2)).Revision);
        // Removing members never needs the escalation check.
        Assert.AreEqual(4L, Value(await fixture.Teams.RemoveMemberAsync(supervisor, new TeamId("admins"), new UserId("carol"), 3)).Revision);

    }

    [TestMethod]
    public async Task TeamScopedManagersAndMembershipLimits()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var lead = fixture.User("lead");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        var teamLead = await fixture.CustomRoleAsync("team-lead", AccessPermissions.TeamManage, AccessPermissions.TeamRead);
        await fixture.GrantAsync(lead.UserId, teamLead, fixture.TeamScope("billing"));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("billing"), "Billing")));
        Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId("sales"), "Sales")));

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.CreateAsync(lead, new Team(new TeamId("new"), "New"))));
        Assert.AreEqual(2L, Value(await fixture.Teams.AddMemberAsync(lead, new TeamId("billing"), new UserId("ada"), 1)).Revision);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Teams.AddMemberAsync(lead, new TeamId("sales"), new UserId("ada"), 1)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Teams.AddMemberAsync(lead, new TeamId("billing"), new UserId("ada"), 0)));

        for (var index = 0; index < WorkspaceLimits.MaximumTeamsPerUser - 1; index++)
            Value(await fixture.Teams.CreateAsync(admin, new Team(new TeamId($"t-{index:D3}"), "T", null, [new UserId("ada")])));
        Assert.AreEqual("refused:Rejected", Describe(await fixture.Teams.AddMemberAsync(admin, new TeamId("sales"), new UserId("ada"), 1)));
        var principal = await fixture.Resolver.ResolveUserPrincipalAsync(fixture.Scope, new UserId("ada"));
        Assert.HasCount(UserPrincipal.MaximumTeamMemberships, principal.Teams);
    }

    [TestMethod]
    public async Task CalendarsRequireReadAndCalendarManage()
    {
        using var fixture = Create();
        var admin = fixture.User("admin");
        var agent = fixture.User("agent");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent);
        var office = WorkspaceStoreContract.SampleCalendar("office");

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Calendars.CreateAsync(agent, office)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Calendars.CreateAsync(admin, WorkspaceStoreContract.SampleCalendar("bad", "Nowhere/Atlantis"))));
        Value(await fixture.Calendars.CreateAsync(admin, office));
        Assert.AreEqual(office, Value(await fixture.Calendars.GetAsync(agent, office.Id)).Calendar);
        Assert.HasCount(1, Value(await fixture.Calendars.ListAsync(agent)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Calendars.ListAsync(fixture.User("stranger"))));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Calendars.GetAsync(agent, new BusinessCalendarId("missing"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Calendars.UpdateAsync(agent, office, 1)));
        Assert.AreEqual(2L, Value(await fixture.Calendars.UpdateAsync(admin, WorkspaceStoreContract.SampleCalendar("office", "Europe/Paris"), 1)).Revision);
        Assert.AreEqual("refused:Conflict:2", Describe(await fixture.Calendars.DeleteAsync(admin, office.Id, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Calendars.DeleteAsync(agent, office.Id, 2)));
        Assert.AreEqual("ok", Describe(await fixture.Calendars.DeleteAsync(admin, office.Id, 2)));
    }
}
