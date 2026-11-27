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
        var malloryDeny = await fixture.GrantAsync(new UserId("mallory"), settings, effect: GrantEffect.Deny);

        var narrowed = new AccessRole(settings.Id, "Settings", [AccessPermissions.ConversationRead]);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, narrowed, 1)));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(admin, settings.Id, 1)));
        Assert.AreEqual("refused:Conflict:1", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(owner, settings)));
        // A custom role referenced by a live Deny grant cannot be narrowed or deleted until the Deny grant is revoked or expired.
        var narrowRefused = (TicketFailure)(await fixture.Admin.UpdateRoleAsync(owner, narrowed, 1)).Value!;
        Assert.AreEqual(TicketFailureKind.Conflict, narrowRefused.Kind);
        Assert.AreEqual("role-in-use", narrowRefused.Code);
        var deleteRefused = (TicketFailure)(await fixture.Admin.DeleteRoleAsync(owner, settings.Id, 1)).Value!;
        Assert.AreEqual(TicketFailureKind.Conflict, deleteRefused.Kind);
        Assert.AreEqual("role-in-use", deleteRefused.Code);

        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, malloryDeny.Id, 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(owner, narrowed, 1)));
        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(owner, settings.Id, 1)));
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(owner, new AccessRole(new RoleId("ghost"), "x", []), 1)));
    }

    [TestMethod]
    public async Task CustomRoleReferencedByScopedLiveDenyGrantCannotBeShrunkOrDeleted()
    {
        var (fixture, owner, admin, _) = await CreateAsync();
        using var _ = fixture;
        var teamScope = new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing"));
        var inboxScope = new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip"));

        var teamDenyRole = new AccessRole(new RoleId("team-deny-role"), "Team Deny Role", [AccessPermissions.ConversationDelete, AccessPermissions.ConversationRead]);
        var inboxDenyRole = new AccessRole(new RoleId("inbox-deny-role"), "Inbox Deny Role", [AccessPermissions.ConversationDelete, AccessPermissions.ConversationRead]);
        var expiredDenyRole = new AccessRole(new RoleId("expired-deny-role"), "Expired Deny Role", [AccessPermissions.ConversationDelete, AccessPermissions.ConversationRead]);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(owner, teamDenyRole)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(owner, inboxDenyRole)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(owner, expiredDenyRole)));

        await fixture.GrantAsync(admin.UserId, teamDenyRole, teamScope, GrantEffect.Deny);
        await fixture.GrantAsync(admin.UserId, inboxDenyRole, inboxScope, GrantEffect.Deny);
        var now = fixture.Clock.GetUtcNow();
        await fixture.SeedAsync(new AccessGrant(new GrantId("expired-team-deny"), fixture.Partition, admin.UserId, expiredDenyRole.Id, teamScope, GrantEffect.Deny,
            validFrom: now.AddDays(-2), validUntil: now.AddMinutes(-5)));

        // Actor restricted by a live TeamGrantScope Deny grant referencing R cannot neutralize it via UpdateRoleAsync (removing permissions) or DeleteRoleAsync (+ CreateRoleAsync).
        var shrunkTeam = new AccessRole(teamDenyRole.Id, teamDenyRole.Name, [AccessPermissions.ConversationRead]);
        var teamUpdateRefusal = (TicketFailure)(await fixture.Admin.UpdateRoleAsync(admin, shrunkTeam, 1)).Value!;
        Assert.AreEqual(TicketFailureKind.Conflict, teamUpdateRefusal.Kind);
        Assert.AreEqual("role-in-use", teamUpdateRefusal.Code);
        var teamDeleteRefusal = (TicketFailure)(await fixture.Admin.DeleteRoleAsync(admin, teamDenyRole.Id, 1)).Value!;
        Assert.AreEqual(TicketFailureKind.Conflict, teamDeleteRefusal.Kind);
        Assert.AreEqual("role-in-use", teamDeleteRefusal.Code);

        // Renaming or widening the role without removing any permissions is still allowed.
        var widenedTeam = new AccessRole(teamDenyRole.Id, "Renamed Team Deny Role", [AccessPermissions.ConversationDelete, AccessPermissions.ConversationRead, AccessPermissions.ContactRead]);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, widenedTeam, 1)));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(admin, AccessPermissions.ConversationDelete, fixture.Resource(team: "billing")));

        // Actor restricted by a live InboxGrantScope Deny grant referencing R2 also cannot neutralize it via UpdateRoleAsync or DeleteRoleAsync.
        var shrunkInbox = new AccessRole(inboxDenyRole.Id, inboxDenyRole.Name, []);
        var inboxUpdateRefusal = (TicketFailure)(await fixture.Admin.UpdateRoleAsync(admin, shrunkInbox, 1)).Value!;
        Assert.AreEqual(TicketFailureKind.Conflict, inboxUpdateRefusal.Kind);
        Assert.AreEqual("role-in-use", inboxUpdateRefusal.Code);
        var inboxDeleteRefusal = (TicketFailure)(await fixture.Admin.DeleteRoleAsync(admin, inboxDenyRole.Id, 1)).Value!;
        Assert.AreEqual(TicketFailureKind.Conflict, inboxDeleteRefusal.Kind);
        Assert.AreEqual("role-in-use", inboxDeleteRefusal.Code);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(admin, AccessPermissions.ConversationDelete, fixture.Resource(inbox: "vip")));

        // Expired Deny grants do not block shrinking or deleting a custom role.
        var shrunkExpired = new AccessRole(expiredDenyRole.Id, expiredDenyRole.Name, [AccessPermissions.ConversationRead]);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, shrunkExpired, 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(admin, expiredDenyRole.Id, 2)));
    }

    [TestMethod]
    public async Task ConditionalLiveDenyGrantsFailClosedDuringAdministrativeAntiEscalation()
    {
        var (fixture, owner, _, _) = await CreateAsync();
        using var _ = fixture;
        var now = fixture.Clock.GetUtcNow();
        var admin = fixture.User("cond-admin", "ops");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);

        var offWindow = new TimeWindowCondition(
            TimeOnly.FromTimeSpan(now.AddHours(2).UtcDateTime.TimeOfDay),
            TimeOnly.FromTimeSpan(now.AddHours(4).UtcDateTime.TimeOfDay));
        Assert.IsFalse(offWindow.Contains(now));

        var denyAssignee = new AccessGrant(new GrantId("deny-assignee"), fixture.Partition, admin.UserId, BuiltInRoles.Agent.Id, fixture.Workspace, GrantEffect.Deny,
            condition: new AssigneeIsSelfCondition());
        var denyTeam = new AccessGrant(new GrantId("deny-team"), fixture.Partition, admin.UserId, BuiltInRoles.Agent.Id, fixture.Workspace, GrantEffect.Deny,
            condition: new TeamInPrincipalTeamsCondition());
        var denyWindow = new AccessGrant(new GrantId("deny-window"), fixture.Partition, admin.UserId, BuiltInRoles.Agent.Id, fixture.Workspace, GrantEffect.Deny,
            condition: offWindow);
        foreach (var grant in new[] { denyAssignee, denyTeam, denyWindow })
            await fixture.SeedAsync(grant);

        // Normal runtime authorization on concrete resources still evaluates conditions accurately (not broken by conservative admin mode).
        Assert.AreEqual("allowed", await fixture.DescribeAsync(admin, AccessPermissions.ConversationReply, fixture.Resource()),
            "Concrete unassigned, no-team resource outside the time window remains allowed at runtime.");
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(admin, AccessPermissions.ConversationReply, fixture.Resource(assignee: admin.UserId.Value)));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(admin, AccessPermissions.ConversationReply, fixture.Resource(team: "ops")));

        // Administrative anti-escalation fails closed: admin cannot revoke, weaken, or pass CheckCanRevokeAsync for any of the conditional live Deny grants.
        foreach (var grant in new[] { denyAssignee, denyTeam, denyWindow })
        {
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, grant.Id, 1)), grant.Id.Value);
            var toAllow = new AccessGrant(grant.Id, fixture.Partition, admin.UserId, BuiltInRoles.Viewer.Id, fixture.Workspace, GrantEffect.Allow);
            var shortened = new AccessGrant(grant.Id, fixture.Partition, admin.UserId, BuiltInRoles.Agent.Id, fixture.Workspace, GrantEffect.Deny,
                condition: grant.Condition, validUntil: now.AddSeconds(5));
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, toAllow, 1)), grant.Id.Value);
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, shortened, 1)), grant.Id.Value);
            Assert.IsInstanceOfType<TicketFailure>((await fixture.Admin.CheckCanRevokeAsync(admin, [grant])).Value, grant.Id.Value);
            Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, grant.Id), grant.Id.Value);
        }

        // Unrestricted owner can still update and revoke all three conditional live Deny grants.
        foreach (var grant in new[] { denyAssignee, denyTeam, denyWindow })
        {
            var shortened = new AccessGrant(grant.Id, fixture.Partition, admin.UserId, BuiltInRoles.Agent.Id, fixture.Workspace, GrantEffect.Deny,
                condition: grant.Condition, validUntil: now.AddHours(1));
            Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(owner, shortened, 1)), grant.Id.Value);
            Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, grant.Id, 2)), grant.Id.Value);
        }
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
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(workspaceOwner, tenantViewer.Id, 1)));
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

    [TestMethod]
    public async Task CrossWorkspaceAndUnauthorizedGrantMutationsReturnOpaqueNotFoundWithoutExfiltratingAuditMetadata()
    {
        var (fixture, owner, admin, agent) = await CreateAsync();
        using var _ = fixture;
        var otherWorkspace = new WorkspaceId("ws-other");
        var now = fixture.Clock.GetUtcNow();

        var otherStd = new AccessGrant(new GrantId("g-other-std"), fixture.Partition, new UserId("bob"), BuiltInRoles.Viewer.Id,
            new TeamGrantScope(otherWorkspace, new TeamId("secret-team")), justification: "SECRET-OTHER-WS-JUSTIFICATION");
        await fixture.SeedAsync(otherStd);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await fixture.Grants.UpdateAsync(
            new AccessGrant(otherStd.Id, fixture.Partition, new UserId("bob"), BuiltInRoles.Agent.Id,
                new TeamGrantScope(otherWorkspace, new TeamId("secret-team")), justification: "SECRET-OTHER-WS-JUSTIFICATION"), 1)).Value);

        var otherBg = new AccessGrant(new GrantId("g-other-bg"), fixture.Partition, new UserId("bob"), BuiltInRoles.Supervisor.Id,
            new InboxGrantScope(otherWorkspace, new InboxId("secret-inbox")),
            validFrom: now, validUntil: now.AddHours(1), justification: "Incident 999 SECRET-BG-CROSS-WS", kind: GrantKind.BreakGlass);
        await fixture.SeedAsync(otherBg);

        var tenantGrant = new AccessGrant(new GrantId("g-tenant-only"), fixture.Partition, new UserId("carol"), BuiltInRoles.Owner.Id,
            TenantGrantScope.Instance, justification: "SECRET-TENANT-JUSTIFICATION");
        await fixture.SeedAsync(tenantGrant);

        var missingId = new GrantId("g-truly-missing");
        var auditBefore = fixture.Audit.Snapshot().Count;

        // Workspace-A admin probing workspace-B, tenant, and missing grants must receive identical opaque NotFound (never Forbidden/Conflict/Invalid).
        foreach (var probeId in new[] { otherStd.Id, otherBg.Id, tenantGrant.Id, missingId })
        {
            foreach (var rev in new[] { 1L, 2L, 999L })
            {
                var upd = (TicketFailure)(await fixture.Admin.UpdateGrantAsync(admin,
                    new AccessGrant(probeId, fixture.Partition, new UserId("x"), BuiltInRoles.Viewer.Id, fixture.Workspace), rev)).Value!;
                Assert.AreEqual(TicketFailureKind.NotFound, upd.Kind, $"UpdateGrantAsync leaked oracle for {probeId} rev {rev}: {upd.Kind}");
                Assert.IsNull(upd.CurrentRevision);

                var revRes = (TicketFailure)(await fixture.Admin.RevokeGrantAsync(admin, probeId, rev)).Value!;
                Assert.AreEqual(TicketFailureKind.NotFound, revRes.Kind, $"RevokeGrantAsync leaked oracle for {probeId} rev {rev}: {revRes.Kind}");
                Assert.IsNull(revRes.CurrentRevision);
            }
        }

        // Audit events recorded in workspace A for cross-scope / unauthorized revoke attempts must NOT leak RoleId, Justification, TeamId, InboxId, or DenialReason.
        var revokeAudits = fixture.Audit.Snapshot().Skip(auditBefore).Where(e => e.Kind == AccessAuditKind.GrantRevoked).ToList();
        Assert.HasCount(12, revokeAudits);
        foreach (var entry in revokeAudits)
        {
            Assert.AreEqual(AccessAuditOutcome.Denied, entry.Outcome);
            Assert.AreEqual(AccessPermissions.AccessManage, entry.Permission);
            Assert.IsNull(entry.RoleId, $"Audit leaked foreign RoleId '{entry.RoleId}' for grant {entry.GrantId}.");
            Assert.IsNull(entry.Justification, $"Audit leaked foreign Justification '{entry.Justification}' for grant {entry.GrantId}.");
            Assert.IsNull(entry.TeamId, $"Audit leaked foreign TeamId '{entry.TeamId}' for grant {entry.GrantId}.");
            Assert.IsNull(entry.InboxId, $"Audit leaked foreign InboxId '{entry.InboxId}' for grant {entry.GrantId}.");
            Assert.IsNull(entry.DenialReason, $"Audit leaked DenialReason '{entry.DenialReason}' for grant {entry.GrantId}.");
        }

        // Batch RevokeGrantsAsync must also return opaque NotFound without revision oracle or audit metadata exfiltration.
        var batchAuditBefore = fixture.Audit.Snapshot().Count;
        var batch = await fixture.Admin.RevokeGrantsAsync(admin, [(otherStd.Id, 999L), (otherBg.Id, 999L), (tenantGrant.Id, 999L), (missingId, 999L)]);
        foreach (var item in batch)
        {
            var failure = (TicketFailure)item.Value!;
            Assert.AreEqual(TicketFailureKind.NotFound, failure.Kind);
            Assert.IsNull(failure.CurrentRevision);
        }
        var batchAudits = fixture.Audit.Snapshot().Skip(batchAuditBefore).ToList();
        Assert.HasCount(4, batchAudits);
        foreach (var entry in batchAudits)
        {
            Assert.IsNull(entry.RoleId);
            Assert.IsNull(entry.Justification);
            Assert.IsNull(entry.TeamId);
            Assert.IsNull(entry.InboxId);
            Assert.IsNull(entry.DenialReason);
        }

        // Unauthorized agent receives Forbidden for both existing and nonexistent grants (no existence oracle).
        var ownGrant = new AccessGrant(new GrantId("g-own"), fixture.Partition, new UserId("bob"), BuiltInRoles.Viewer.Id, fixture.Workspace);
        await fixture.SeedAsync(ownGrant);
        foreach (var probeId in new[] { ownGrant.Id, new GrantId("g-does-not-exist"), otherStd.Id })
        {
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(agent,
                new AccessGrant(probeId, fixture.Partition, new UserId("x"), BuiltInRoles.Viewer.Id, fixture.Workspace), 999)));
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(agent, probeId, 999)));
        }
        var agentBatch = await fixture.Admin.RevokeGrantsAsync(agent, [(ownGrant.Id, 999L), (new GrantId("g-does-not-exist"), 999L)]);
        foreach (var item in agentBatch)
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(item));

        // Tenant owner still has full authority over tenant grants.
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, tenantGrant.Id, 1)));
    }

    [TestMethod]
    public async Task EvaluatorAndPlannerRequireTenantScopeAndStandardGrantsForAdministrativePermissions()
    {
        using var fixture = AccessFixture.Create();
        var tenantOwner = fixture.User("tenant-owner");
        await fixture.GrantAsync(tenantOwner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var wsOwner = fixture.User("ws-owner");
        await fixture.GrantAsync(wsOwner.UserId, BuiltInRoles.Owner, fixture.Workspace);

        // Tenant-scoped Owner passes both AccessManage and AccessManageTenant in evaluator, batch context and planner.
        Assert.AreEqual("allowed", await fixture.DescribeAsync(tenantOwner, AccessPermissions.AccessManage));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(tenantOwner, AccessPermissions.AccessManageTenant));
        Assert.AreNotSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(tenantOwner, AccessPermissions.AccessManageTenant, fixture.Scope)));

        // Workspace-scoped Owner passes AccessManage, but is DENIED AccessManageTenant in evaluator, batch context and planner.
        Assert.AreEqual("allowed", await fixture.DescribeAsync(wsOwner, AccessPermissions.AccessManage));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(wsOwner, AccessPermissions.AccessManageTenant));
        var wsBatch = await fixture.Evaluator.LoadDecisionContextAsync(wsOwner, fixture.Scope, [AccessPermissions.AccessManage, AccessPermissions.AccessManageTenant]);
        Assert.IsTrue((await wsBatch.DecideAsync(AccessPermissions.AccessManage, fixture.Resource())).IsPermitted);
        Assert.IsFalse((await wsBatch.DecideAsync(AccessPermissions.AccessManageTenant, fixture.Resource())).IsPermitted);
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(wsOwner, AccessPermissions.AccessManageTenant, fixture.Scope)));
        var wsHeld = await fixture.Planner.HeldAnywhereAsync(wsOwner, fixture.Scope, [AccessPermissions.AccessManage, AccessPermissions.AccessManageTenant]);
        Assert.Contains(AccessPermissions.AccessManage, wsHeld);
        Assert.DoesNotContain(AccessPermissions.AccessManageTenant, wsHeld);

        // Break-glass grants NEVER satisfy administrative permissions in evaluator or planner.
        var bgUser = fixture.User("bg-admin");
        var bgRole = await fixture.CustomRoleAsync("bg-all-admin", AccessPermissions.AccessManage, AccessPermissions.AccessManageTenant, AccessPermissions.AccessBreakGlass, AccessPermissions.ConversationRead);
        var now = fixture.Clock.GetUtcNow();
        await fixture.SeedAsync(new AccessGrant(new GrantId("g-bg-admin"), fixture.Partition, bgUser.UserId, bgRole.Id, fixture.Workspace,
            validFrom: now, validUntil: now.AddHours(1), justification: "Incident 123 testing admin break-glass block", kind: GrantKind.BreakGlass));
        Assert.AreEqual("allowed:break-glass", await fixture.DescribeAsync(bgUser, AccessPermissions.ConversationRead));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(bgUser, AccessPermissions.AccessManage));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(bgUser, AccessPermissions.AccessManageTenant));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(bgUser, AccessPermissions.AccessBreakGlass));
        var bgHeld = await fixture.Planner.HeldAnywhereAsync(bgUser, fixture.Scope, [AccessPermissions.ConversationRead, AccessPermissions.AccessManage, AccessPermissions.AccessManageTenant, AccessPermissions.AccessBreakGlass]);
        Assert.Contains(AccessPermissions.ConversationRead, bgHeld);
        Assert.DoesNotContain(AccessPermissions.AccessManage, bgHeld);
        Assert.DoesNotContain(AccessPermissions.AccessManageTenant, bgHeld);
        Assert.DoesNotContain(AccessPermissions.AccessBreakGlass, bgHeld);
    }

    [TestMethod]
    public async Task RevokingOrUpdatingActiveDenyGrantRequiresHoldingDeniedPermissions()
    {
        var (fixture, owner, admin, _) = await CreateAsync();
        using var _ = fixture;
        var now = fixture.Clock.GetUtcNow();
        var wsMgrRole = await fixture.CustomRoleAsync("ws-mgr", AccessPermissions.WorkspaceManage);
        var bob = new UserId("bob");

        // Active deny grants covering permissions admin does NOT hold (workspace:manage / missing role -> all catalog).
        var denyOwner = new AccessGrant(new GrantId("deny-owner"), fixture.Partition, bob, BuiltInRoles.Owner.Id, fixture.Workspace, GrantEffect.Deny);
        var denyCustom = new AccessGrant(new GrantId("deny-custom"), fixture.Partition, bob, wsMgrRole.Id, fixture.Workspace, GrantEffect.Deny);
        var denyMissing = new AccessGrant(new GrantId("deny-missing"), fixture.Partition, bob, new RoleId("deleted-role"), fixture.Workspace, GrantEffect.Deny);
        var denyExpired = new AccessGrant(new GrantId("deny-expired"), fixture.Partition, bob, BuiltInRoles.Owner.Id, fixture.Workspace, GrantEffect.Deny,
            validFrom: now.AddDays(-2), validUntil: now.AddMinutes(-5));
        var denyViewer = new AccessGrant(new GrantId("deny-viewer"), fixture.Partition, bob, BuiltInRoles.Viewer.Id, fixture.Workspace, GrantEffect.Deny);
        var denyViewerBatch = new AccessGrant(new GrantId("deny-viewer-batch"), fixture.Partition, bob, BuiltInRoles.Viewer.Id, fixture.Workspace, GrantEffect.Deny);

        foreach (var g in new[] { denyOwner, denyCustom, denyMissing, denyExpired, denyViewer, denyViewerBatch })
            await fixture.SeedAsync(g);

        // Direct revoke: admin cannot revoke active denies for roles containing unheld permissions or missing roles.
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, denyOwner.Id, 1)));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, denyCustom.Id, 1)));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, denyMissing.Id, 1)));
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, denyOwner.Id));

        // Direct revoke: admin CAN revoke expired deny grants and active deny grants whose permissions admin holds.
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, denyExpired.Id, 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, denyViewer.Id, 1)));

        // Batch revoke: admin is refused on unheld active denies while allowed on held active deny in the same batch.
        var batch = await fixture.Admin.RevokeGrantsAsync(admin, [(denyOwner.Id, 1L), (denyMissing.Id, 1L), (denyViewerBatch.Id, 1L)]);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(batch[0]));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(batch[1]));
        Assert.AreEqual("applied", AccessFixture.Describe(batch[2]));
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, denyOwner.Id));
        Assert.IsNull(await fixture.Grants.GetAsync(fixture.Partition, denyViewerBatch.Id));

        // Self-deny: an admin who is explicitly denied a role's permissions cannot revoke their own active deny grant.
        var selfDeny = new AccessGrant(new GrantId("self-deny"), fixture.Partition, admin.UserId, BuiltInRoles.Agent.Id, fixture.Workspace, GrantEffect.Deny);
        await fixture.SeedAsync(selfDeny);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(admin, selfDeny.Id, 1)));
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, selfDeny.Id));

        // UpdateGrantAsync: admin cannot weaken an active deny grant by switching to Allow, shortening ValidUntil, narrowing scope, or changing its role.
        var toAllow = new AccessGrant(denyOwner.Id, fixture.Partition, bob, BuiltInRoles.Viewer.Id, fixture.Workspace, GrantEffect.Allow);
        var shortened = new AccessGrant(denyOwner.Id, fixture.Partition, bob, BuiltInRoles.Owner.Id, fixture.Workspace, GrantEffect.Deny, validUntil: now.AddSeconds(1));
        var narrowedScope = new AccessGrant(denyOwner.Id, fixture.Partition, bob, BuiltInRoles.Owner.Id, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("t1")), GrantEffect.Deny);
        var weakerRole = new AccessGrant(denyOwner.Id, fixture.Partition, bob, BuiltInRoles.Viewer.Id, fixture.Workspace, GrantEffect.Deny);
        foreach (var attempt in new[] { toAllow, shortened, narrowedScope, weakerRole })
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(admin, attempt, 1)));

        // Tenant owner holds all permissions and CAN update and revoke all of these active deny grants.
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(owner, shortened, 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, denyOwner.Id, 2)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, denyCustom.Id, 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(owner, denyMissing.Id, 1)));
    }

    [TestMethod]
    public async Task PrimaryAdministrativeGatesEvaluateResourceConditionsConservatively()
    {
        var (fixture, owner, _, _) = await CreateAsync();
        using var _ = fixture;
        var now = fixture.Clock.GetUtcNow();
        var viewerGrant = new AccessGrant(new GrantId("g-target"), fixture.Partition, new UserId("bob"), BuiltInRoles.Viewer.Id, fixture.Workspace);
        await fixture.SeedAsync(viewerGrant);
        var newRole = new AccessRole(new RoleId("probe-role"), "Probe", [AccessPermissions.ConversationRead]);
        var newGrant = new AccessGrant(new GrantId("g-probe"), fixture.Partition, new UserId("charlie"), BuiltInRoles.Viewer.Id, fixture.Workspace);

        // Conditional Deny on AssigneeIsSelfCondition / TeamInPrincipalTeamsCondition must fail closed on abstract admin targets.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("deny-assignee", new AssigneeIsSelfCondition()),
            ("deny-team", new TeamInPrincipalTeamsCondition()),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin, effect: GrantEffect.Deny, condition: condition);

            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(actor, newRole)), name);
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(actor, newGrant)), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)(await fixture.Admin.ListRolesAsync(actor)).Value!).Kind, name);
            Assert.IsInstanceOfType<TicketFailure>((await fixture.Admin.CheckCanRevokeAsync(actor, [viewerGrant])).Value, name);
        }

        // Conditional Allow with Not(AssigneeIsSelfCondition) / Not(TeamInPrincipalTeamsCondition) must NOT authorize abstract admin targets.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("allow-not-assignee", new NotCondition(new AssigneeIsSelfCondition())),
            ("allow-not-team", new NotCondition(new TeamInPrincipalTeamsCondition())),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin, condition: condition);

            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(actor, newRole)), name);
            Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(actor, newGrant)), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)(await fixture.Admin.ListRolesAsync(actor)).Value!).Kind, name);
            Assert.IsInstanceOfType<TicketFailure>((await fixture.Admin.CheckCanRevokeAsync(actor, [viewerGrant])).Value, name);
        }

        // TimeWindowCondition on primary admin gates still evaluates live against the clock (while RequireHeld stays ConservativeAll).
        var activeWindow = new TimeWindowCondition(
            TimeOnly.FromTimeSpan(now.AddHours(-1).UtcDateTime.TimeOfDay),
            TimeOnly.FromTimeSpan(now.AddHours(1).UtcDateTime.TimeOfDay));
        var timedAdmin = fixture.User("timed-admin");
        await fixture.GrantAsync(timedAdmin.UserId, BuiltInRoles.Admin, condition: activeWindow);
        Assert.IsTrue((await fixture.Admin.ListRolesAsync(timedAdmin)).IsSuccess, "Active time window authorizes primary admin gate.");
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(timedAdmin, newRole)),
            "Time-windowed permission cannot satisfy anti-escalation RequireHeld.");
        await fixture.GrantAsync(timedAdmin.UserId, BuiltInRoles.Viewer);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(timedAdmin, newRole)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(owner, newRole.Id, 1)));
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)(await fixture.Admin.ListRolesAsync(timedAdmin)).Value!).Kind);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(timedAdmin, newRole)));
    }
}
