using TicketFlow.Core;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class RoleTests
{
    [TestMethod]
    public void BuiltInRolesAreFixedAndCannotBeRedefined()
    {
        CollectionAssert.AreEqual(new[] { "owner", "admin", "supervisor", "agent", "light-agent", "viewer" }, BuiltInRoles.All.Select(role => role.Id.Value).ToArray());
        foreach (var role in BuiltInRoles.All)
        {
            Assert.IsTrue(role.IsBuiltIn);
            Assert.AreSame(role, BuiltInRoles.Find(role.Id));
            Assert.ThrowsExactly<ArgumentException>(() => new AccessRole(role.Id, "Impostor", []));
        }
        Assert.IsTrue(BuiltInRoles.Owner.Includes(new Permission("macro:run")));
        Assert.IsFalse(BuiltInRoles.Admin.Includes(AccessPermissions.AccessManageTenant));
        Assert.IsTrue(BuiltInRoles.Admin.Includes(AccessPermissions.AccessManage));
        Assert.IsTrue(BuiltInRoles.LightAgent.Includes(AccessPermissions.NoteWrite));
        Assert.IsFalse(BuiltInRoles.LightAgent.Includes(AccessPermissions.ConversationReply));
        Assert.IsFalse(BuiltInRoles.Viewer.Includes(AccessPermissions.ConversationReply));
        Assert.IsTrue(BuiltInRoles.Agent.Includes(AccessPermissions.ConversationReply));
        Assert.IsFalse(BuiltInRoles.Agent.Includes(AccessPermissions.ContactExport));
        Assert.IsFalse(BuiltInRoles.Owner.Includes(default));
        Assert.IsTrue(BuiltInRoles.Owner.Permissions is { Count: 0 });
    }

    [TestMethod]
    public void CustomRolesAreBoundedAndValidated()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RoleId("Admin"));
        Assert.ThrowsExactly<ArgumentException>(() => new RoleId(new string('r', 65)));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessRole(new RoleId("x"), " ", []));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessRole(default, "x", []));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessRole(new RoleId("x"), "x", [default]));
        var many = Enumerable.Range(0, AccessRole.MaximumPermissions + 1).Select(index => new Permission($"p{index}:read")).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => new AccessRole(new RoleId("x"), "x", many));
        var bounded = new AccessRole(new RoleId("x"), "x", many.Take(AccessRole.MaximumPermissions).Concat(many.Take(3)));
        Assert.HasCount(AccessRole.MaximumPermissions, bounded.Permissions);
        Assert.AreEqual(new AccessRole(new RoleId("x"), "x", [AccessPermissions.NoteRead, AccessPermissions.NoteWrite]),
            new AccessRole(new RoleId("x"), "x", [AccessPermissions.NoteWrite, AccessPermissions.NoteRead]));
    }

    [TestMethod]
    public async Task CustomRolesAreRevisionedThroughTheAdministrator()
    {
        using var fixture = AccessFixture.Create();
        var admin = fixture.User("root");
        var agent = fixture.User("agent");
        await fixture.GrantAsync(admin.UserId, BuiltInRoles.Admin);
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent);
        var triage = new AccessRole(new RoleId("triage"), "Triage", [AccessPermissions.ConversationRead]);

        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(agent, triage)));
        var created = await fixture.Admin.CreateRoleAsync(admin, triage);
        Assert.AreEqual("applied", AccessFixture.Describe(created));
        Assert.AreEqual(1L, ((TicketValue<AccessChangeApplied<StoredAccessRole>>)created.Value!).Value.Value.Revision);
        Assert.AreEqual("refused:Conflict:1", AccessFixture.Describe(await fixture.Admin.CreateRoleAsync(admin, triage)));

        var wider = new AccessRole(triage.Id, "Triage", [AccessPermissions.ConversationRead, AccessPermissions.ConversationAssign]);
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, wider, 1)));
        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, triage, 1)));
        Assert.AreEqual("refused:Invalid", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, wider, 0)));
        Assert.AreEqual(wider, (await fixture.Roles.GetAsync(fixture.Scope, triage.Id))!.Role);
        Assert.AreEqual("refused:Rejected", AccessFixture.Describe(await fixture.Admin.UpdateRoleAsync(admin, BuiltInRoles.Agent, 1)));
        Assert.AreEqual("refused:Rejected", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(admin, BuiltInRoles.Agent.Id, 1)));

        var unknown = new AccessRole(new RoleId("ghost"), "Ghost", [new Permission("ghost:haunt"), new Permission("alpha:zap")]);
        var invalid = (TicketFailure)(await fixture.Admin.CreateRoleAsync(admin, unknown)).Value!;
        Assert.AreEqual(TicketFailureKind.Invalid, invalid.Kind);
        CollectionAssert.AreEqual(new[] { "Unknown permission 'alpha:zap'.", "Unknown permission 'ghost:haunt'." }, invalid.Errors.ToArray());

        Assert.AreEqual("refused:Conflict:2", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(admin, triage.Id, 1)));
        Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(admin, triage.Id, 2)));
        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.DeleteRoleAsync(admin, triage.Id, 2)));

        // One audit event per administrative call, applied or refused.
        var events = fixture.Audit.Snapshot();
        Assert.HasCount(12, events);
        Assert.AreEqual(3, events.Count(e => e.Outcome == AccessAuditOutcome.Allowed));
        Assert.AreEqual(AccessAuditKind.RoleCreated, events[0].Kind);
        Assert.AreEqual(AccessDenialReason.NoMatchingGrant, events[0].DenialReason);
        Assert.AreEqual(new RoleId("triage"), events[1].RoleId);
    }

    [TestMethod]
    public async Task CustomRolesResolveInTheirWorkspaceAndDeletedRolesFailClosed()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        var triage = await fixture.CustomRoleAsync("triage", AccessPermissions.ContactMerge);
        var muted = await fixture.CustomRoleAsync("muted", AccessPermissions.NoteWrite);
        await fixture.GrantAsync(user.UserId, triage);
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        var deny = await fixture.GrantAsync(user.UserId, muted, effect: GrantEffect.Deny);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ContactMerge));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.NoteWrite));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));

        // Deleting a deny grant's role makes the deny unresolvable: it then denies every permission.
        await fixture.Roles.DeleteAsync(fixture.Scope, muted.Id, 1);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        await fixture.Grants.DeleteAsync(fixture.Partition, deny.Id, 1);

        // Deleting an allow grant's role removes what it granted. Tenant grants never resolve custom roles.
        await fixture.Roles.DeleteAsync(fixture.Scope, triage.Id, 1);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ContactMerge));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationAssign));
        var other = fixture.User("grace");
        await fixture.CustomRoleAsync("assigner", AccessPermissions.ConversationAssign);
        await fixture.GrantAsync(other.UserId, new AccessRole(new RoleId("assigner"), "x", []), TenantGrantScope.Instance);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(other, AccessPermissions.ConversationAssign));
    }
}
