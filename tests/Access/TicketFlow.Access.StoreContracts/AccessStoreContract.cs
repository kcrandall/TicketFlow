using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.StoreContracts;

/// <summary>
/// Behavioral contract every access store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the
/// same storage (for example two data sources), so fencing is verified across clients.
/// </summary>
public static class AccessStoreContract
{
    public static string Describe<T>(StoreWriteResult<T, AccessWriteRejection> result, Func<T, long> revision) => result switch
    {
        StoreApplied<T> applied => $"applied:{revision(applied.Value)}",
        StoreNotFound => "not-found",
        StoreConflict conflict => $"conflict:{conflict.CurrentRevision}",
        StoreRejected<AccessWriteRejection> rejected => $"rejected:{rejected.Reason}",
    };

    private static string Role(StoreWriteResult<StoredAccessRole, AccessWriteRejection> result) => Describe(result, stored => stored.Revision);
    private static string Grant(StoreWriteResult<StoredAccessGrant, AccessWriteRejection> result) => Describe(result, stored => stored.Revision);
    private static string Policy(StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection> result) => Describe(result, stored => stored.Revision);

    public static async Task VerifyRolesAsync(IAccessRoleStore primary, IAccessRoleStore competing)
    {
        var scope = TestScopes.Create();
        var triage = new AccessRole(new RoleId("triage"), "Triage", [AccessPermissions.ConversationRead, AccessPermissions.ConversationAssign]);
        Assert.AreEqual("applied:1", Role(await primary.CreateAsync(scope, triage)));
        Assert.AreEqual("conflict:1", Role(await competing.CreateAsync(scope, triage)));
        Assert.AreEqual(triage, (await competing.GetAsync(scope, triage.Id))!.Role);

        var renamed = new AccessRole(triage.Id, "Triage desk", [AccessPermissions.ConversationRead]);
        Assert.AreEqual("applied:2", Role(await competing.UpdateAsync(scope, renamed, 1)));
        Assert.AreEqual("conflict:2", Role(await primary.UpdateAsync(scope, triage, 1)));
        var stored = (await primary.GetAsync(scope, triage.Id))!;
        Assert.AreEqual(renamed, stored.Role);
        Assert.AreEqual(2L, stored.Revision);
        Assert.AreEqual(scope, stored.Scope);
        Assert.AreEqual("not-found", Role(await primary.UpdateAsync(scope, new AccessRole(new RoleId("missing"), "Missing", []), 1)));

        foreach (var builtIn in BuiltInRoles.All)
        {
            Assert.AreEqual("rejected:BuiltInRoleImmutable", Role(await primary.CreateAsync(scope, builtIn)));
            Assert.AreEqual("rejected:BuiltInRoleImmutable", Role(await primary.UpdateAsync(scope, builtIn, 1)));
            Assert.AreEqual("rejected:BuiltInRoleImmutable", Role(await primary.DeleteAsync(scope, builtIn.Id, 1)));
        }

        var alpha = new AccessRole(new RoleId("alpha"), "Alpha", []);
        Assert.AreEqual("applied:1", Role(await primary.CreateAsync(scope, alpha)));
        CollectionAssert.AreEqual(new[] { "alpha", "triage" }, (await competing.ListAsync(scope)).Select(role => role.Role.Id.Value).ToArray());

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, triage.Id));
            Assert.IsEmpty(await primary.ListAsync(other));
            Assert.AreEqual("not-found", Role(await primary.UpdateAsync(other, renamed, 2)));
            Assert.AreEqual("not-found", Role(await primary.DeleteAsync(other, triage.Id, 2)));
        }

        Assert.AreEqual("conflict:2", Role(await primary.DeleteAsync(scope, triage.Id, 1)));
        Assert.AreEqual("applied:2", Role(await competing.DeleteAsync(scope, triage.Id, 2)));
        Assert.IsNull(await primary.GetAsync(scope, triage.Id));
        Assert.AreEqual("not-found", Role(await primary.DeleteAsync(scope, triage.Id, 2)));
        Assert.AreEqual("applied:1", Role(await primary.CreateAsync(scope, triage)));
    }

    public static async Task VerifyRoleCapacityAsync(IAccessRoleStore store)
    {
        var scope = TestScopes.Create(workspaceId: "role-capacity");
        for (var index = 0; index < AccessLimits.MaximumCustomRolesPerWorkspace; index++)
            Assert.AreEqual("applied:1", Role(await store.CreateAsync(scope, new AccessRole(new RoleId($"role-{index}"), "Role", []))));
        Assert.AreEqual("rejected:CapacityExceeded", Role(await store.CreateAsync(scope, new AccessRole(new RoleId("overflow"), "Role", []))));
        Assert.AreEqual("conflict:1", Role(await store.CreateAsync(scope, new AccessRole(new RoleId("role-0"), "Role", []))));
        Assert.AreEqual("applied:1", Role(await store.CreateAsync(TestScopes.Create(workspaceId: "other-capacity"), new AccessRole(new RoleId("overflow"), "Role", []))));
    }

    public static AccessGrant SampleGrant(string id, AccessPartition partition, GrantSubject subject, GrantScope scope) => new(
        new GrantId(id), partition, subject, BuiltInRoles.Agent.Id, scope, GrantEffect.Allow,
        new AndCondition(new TeamInPrincipalTeamsCondition(), new NotCondition(new AttributeInCondition("tier", "vip", "enterprise")),
            new OrCondition(new AssigneeIsSelfCondition(), new HasLabelCondition(new LabelId("escalated")), new TimeWindowCondition(new(22, 0), new(6, 0)))),
        TestClock.DefaultStart.AddDays(-1), TestClock.DefaultStart.AddDays(30), "Coverage for the night shift");

    /// <summary>Listing by target returns exactly the grants with that scope (any subject), in identifier order, paged.</summary>
    public static async Task VerifyGrantTargetsAsync(IAccessGrantStore primary, IAccessGrantStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "targets");
        var partition = AccessPartition.From(scope);
        var inbox = new InboxGrantScope(scope.WorkspaceId, new InboxId("email"));
        var team = new TeamGrantScope(scope.WorkspaceId, new TeamId("email"));
        var workspace = new WorkspaceGrantScope(scope.WorkspaceId);
        AccessGrant Make(string id, GrantSubject subject, GrantScope grantScope, GrantEffect effect = GrantEffect.Allow) =>
            new(new GrantId(id), partition, subject, BuiltInRoles.Agent.Id, grantScope, effect);
        AccessGrant[] seeded =
        [
            Make("t-c", new UserId("ada"), inbox), Make("t-a", new TeamId("billing"), inbox, GrantEffect.Deny), Make("t-b", new ApiClientId("crm"), inbox),
            Make("t-team", new UserId("ada"), team), Make("t-ws", new UserId("ada"), workspace), Make("t-tenant", new UserId("ada"), TenantGrantScope.Instance),
            Make("t-other-inbox", new UserId("ada"), new InboxGrantScope(scope.WorkspaceId, new InboxId("chat"))),
            Make("t-other-ws", new UserId("ada"), new InboxGrantScope(new WorkspaceId("elsewhere"), new InboxId("email"))),
        ];
        foreach (var grant in seeded)
            Assert.AreEqual("applied:1", Grant(await primary.CreateAsync(grant)));
        Assert.AreEqual("applied:1", Grant(await primary.CreateAsync(new AccessGrant(new GrantId("t-c"), AccessPartition.From(TestScopes.Create(tenantId: "other-tenant")), new UserId("ada"), BuiltInRoles.Agent.Id, inbox))));

        CollectionAssert.AreEqual(new[] { "t-a", "t-b", "t-c" }, (await competing.ListForTargetAsync(partition, inbox, 100)).Select(entry => entry.Grant.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "t-a", "t-b" }, (await competing.ListForTargetAsync(partition, inbox, 2)).Select(entry => entry.Grant.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "t-c" }, (await competing.ListForTargetAsync(partition, inbox, 2, new GrantId("t-b"))).Select(entry => entry.Grant.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "t-team" }, (await competing.ListForTargetAsync(partition, team, 100)).Select(entry => entry.Grant.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "t-ws" }, (await competing.ListForTargetAsync(partition, workspace, 100)).Select(entry => entry.Grant.Id.Value).ToArray());
        Assert.AreEqual(seeded[1], (await competing.ListForTargetAsync(partition, inbox, 1)).Single().Grant);
        Assert.IsEmpty(await competing.ListForTargetAsync(AccessPartition.From(TestScopes.Create(projectId: "other-project")), inbox, 100));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => competing.ListForTargetAsync(partition, TenantGrantScope.Instance, 100));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => competing.ListForTargetAsync(partition, inbox, AccessLimits.MaximumTargetPage + 1));
        Assert.AreEqual("applied:1", Grant(await competing.DeleteAsync(partition, new GrantId("t-b"), 1)));
        CollectionAssert.AreEqual(new[] { "t-a", "t-c" }, (await primary.ListForTargetAsync(partition, inbox, 100)).Select(entry => entry.Grant.Id.Value).ToArray());
    }

    public static async Task VerifyGrantsAsync(IAccessGrantStore primary, IAccessGrantStore competing)
    {
        var scope = TestScopes.Create();
        var partition = AccessPartition.From(scope);
        GrantSubject user = new UserId("ada");
        GrantSubject team = new TeamId("billing");
        GrantSubject client = new ApiClientId("crm");
        var workspaceGrant = SampleGrant("g-workspace", partition, user, new WorkspaceGrantScope(scope.WorkspaceId));
        var tenantGrant = new AccessGrant(new GrantId("g-tenant"), partition, user, BuiltInRoles.Viewer.Id, TenantGrantScope.Instance);
        var teamGrant = new AccessGrant(new GrantId("g-team"), partition, team, BuiltInRoles.Agent.Id, new TeamGrantScope(scope.WorkspaceId, new TeamId("billing")), GrantEffect.Deny);
        var inboxGrant = new AccessGrant(new GrantId("g-inbox"), partition, client, BuiltInRoles.Viewer.Id, new InboxGrantScope(scope.WorkspaceId, new InboxId("email")));
        var otherWorkspace = new AccessGrant(new GrantId("g-elsewhere"), partition, user, BuiltInRoles.Agent.Id, new WorkspaceGrantScope(new WorkspaceId("elsewhere")));
        var breakGlass = new AccessGrant(new GrantId("g-break-glass"), partition, user, BuiltInRoles.Supervisor.Id, new WorkspaceGrantScope(scope.WorkspaceId),
            validFrom: TestClock.DefaultStart, validUntil: TestClock.DefaultStart.AddHours(1), justification: "Incident 42 escalation", kind: GrantKind.BreakGlass);
        var foreignTeam = new AccessGrant(new GrantId("g-foreign-team"), partition, team, BuiltInRoles.Agent.Id, new TeamGrantScope(new WorkspaceId("elsewhere"), new TeamId("billing")));
        var foreignInbox = new AccessGrant(new GrantId("g-foreign-inbox"), partition, client, BuiltInRoles.Agent.Id, new InboxGrantScope(new WorkspaceId("elsewhere"), new InboxId("email")));
        foreach (var grant in new[] { workspaceGrant, tenantGrant, teamGrant, inboxGrant, otherWorkspace, breakGlass, foreignTeam, foreignInbox })
            Assert.AreEqual("applied:1", Grant(await primary.CreateAsync(grant)));
        Assert.AreEqual("conflict:1", Grant(await competing.CreateAsync(workspaceGrant)));

        var read = (await competing.GetAsync(partition, workspaceGrant.Id))!;
        Assert.AreEqual(workspaceGrant, read.Grant);
        Assert.AreEqual(1L, read.Revision);
        Assert.AreEqual(breakGlass, (await competing.GetAsync(partition, breakGlass.Id))!.Grant);
        Assert.AreEqual(teamGrant, (await competing.GetAsync(partition, teamGrant.Id))!.Grant);

        var listed = await competing.ListForSubjectsAsync(partition, scope.WorkspaceId, [user, team]);
        CollectionAssert.AreEqual(new[] { "g-break-glass", "g-team", "g-tenant", "g-workspace" }, listed.Select(entry => entry.Grant.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "g-elsewhere", "g-tenant" }, (await competing.ListForSubjectsAsync(partition, new WorkspaceId("elsewhere"), [user])).Select(entry => entry.Grant.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "g-inbox" }, (await competing.ListForSubjectsAsync(partition, scope.WorkspaceId, [client])).Select(entry => entry.Grant.Id.Value).ToArray());
        // Team and inbox grants name their workspace and never list in another one.
        CollectionAssert.AreEqual(new[] { "g-foreign-inbox", "g-foreign-team" }, (await competing.ListForSubjectsAsync(partition, new WorkspaceId("elsewhere"), [team, client])).Select(entry => entry.Grant.Id.Value).ToArray());
        Assert.AreEqual(foreignTeam, (await competing.GetAsync(partition, foreignTeam.Id))!.Grant);
        Assert.IsEmpty(await competing.ListForSubjectsAsync(partition, scope.WorkspaceId, [(GrantSubject)new UserId("nobody")]));
        Assert.IsEmpty(await competing.ListForSubjectsAsync(partition, scope.WorkspaceId, []));
        // Same identifier text, different subject kind, never matches.
        Assert.IsEmpty(await competing.ListForSubjectsAsync(partition, scope.WorkspaceId, [(GrantSubject)new TeamId("ada")]));

        // ListAllForSubjectAsync returns grants across all workspaces and tenant scope in ordinal grant_id order.
        CollectionAssert.AreEqual(
            new[] { "g-break-glass", "g-elsewhere", "g-tenant", "g-workspace" },
            (await competing.ListAllForSubjectAsync(partition, user, scope.WorkspaceId)).Select(entry => entry.Grant.Id.Value).ToArray());
        CollectionAssert.AreEqual(
            new[] { "g-foreign-inbox", "g-inbox" },
            (await competing.ListAllForSubjectAsync(partition, client, scope.WorkspaceId)).Select(entry => entry.Grant.Id.Value).ToArray());
        Assert.IsEmpty(await competing.ListAllForSubjectAsync(partition, new TeamId("ada"), scope.WorkspaceId));
        Assert.IsEmpty(await competing.ListAllForSubjectAsync(AccessPartition.From(TestScopes.Create(tenantId: "other")), user, scope.WorkspaceId));

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1).Take(4))
        {
            var otherPartition = AccessPartition.From(other);
            Assert.IsNull(await primary.GetAsync(otherPartition, workspaceGrant.Id));
            Assert.IsEmpty(await primary.ListForSubjectsAsync(otherPartition, scope.WorkspaceId, [user, team, client]));
            Assert.AreEqual("not-found", Grant(await primary.DeleteAsync(otherPartition, workspaceGrant.Id, 1)));
        }

        var narrowed = new AccessGrant(workspaceGrant.Id, partition, client, BuiltInRoles.Viewer.Id, new InboxGrantScope(scope.WorkspaceId, new InboxId("chat")));
        Assert.AreEqual("applied:2", Grant(await competing.UpdateAsync(narrowed, 1)));
        Assert.AreEqual("conflict:2", Grant(await primary.UpdateAsync(workspaceGrant, 1)));
        Assert.AreEqual(narrowed, (await primary.GetAsync(partition, workspaceGrant.Id))!.Grant);
        Assert.AreEqual("not-found", Grant(await primary.UpdateAsync(SampleGrant("missing", partition, user, TenantGrantScope.Instance), 1)));

        // Concurrent updates with the same expected revision: exactly one applies.
        var first = new AccessGrant(tenantGrant.Id, partition, user, BuiltInRoles.Agent.Id, TenantGrantScope.Instance);
        var second = new AccessGrant(tenantGrant.Id, partition, user, BuiltInRoles.Supervisor.Id, TenantGrantScope.Instance);
        var outcomes = await Task.WhenAll(primary.UpdateAsync(first, 1), competing.UpdateAsync(second, 1));
        CollectionAssert.AreEquivalent(new[] { "applied:2", "conflict:2" }, outcomes.Select(Grant).ToArray());
        var winner = (await primary.GetAsync(partition, tenantGrant.Id))!.Grant;
        Assert.IsTrue(winner == first || winner == second);

        Assert.AreEqual("conflict:1", Grant(await primary.DeleteAsync(partition, inboxGrant.Id, 2)));
        var removed = await competing.DeleteAsync(partition, inboxGrant.Id, 1);
        Assert.AreEqual("applied:1", Grant(removed));
        Assert.AreEqual(inboxGrant, ((StoreApplied<StoredAccessGrant>)removed.Value!).Value.Grant);
        Assert.IsNull(await primary.GetAsync(partition, inboxGrant.Id));
        Assert.AreEqual("not-found", Grant(await primary.DeleteAsync(partition, inboxGrant.Id, 1)));
    }

    /// <summary>Concurrent creates for a subject one below its limit: exactly one is applied, the rest are rejected.</summary>
    public static async Task VerifyConcurrentGrantCapacityAsync(IAccessGrantStore primary, IAccessGrantStore competing, int writers = 8)
    {
        var partition = AccessPartition.From(TestScopes.Create(tenantId: "concurrent-capacity"));
        var workspace = new WorkspaceGrantScope(new WorkspaceId("workspace"));
        GrantSubject subject = new UserId("busy");
        for (var index = 0; index < AccessLimits.MaximumGrantsPerSubject - 1; index++)
            Assert.AreEqual("applied:1", Grant(await primary.CreateAsync(new AccessGrant(new GrantId($"g{index}"), partition, subject, BuiltInRoles.Viewer.Id, workspace))));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, writers).Select(index =>
            (index % 2 == 0 ? primary : competing).CreateAsync(new AccessGrant(new GrantId($"race-{index}"), partition, subject, BuiltInRoles.Viewer.Id, workspace))));
        var described = outcomes.Select(Grant).ToArray();
        Assert.AreEqual(1, described.Count(outcome => string.Equals(outcome, "applied:1", StringComparison.Ordinal)), string.Join(",", described));
        Assert.AreEqual(writers - 1, described.Count(outcome => string.Equals(outcome, "rejected:CapacityExceeded", StringComparison.Ordinal)));
        Assert.HasCount(AccessLimits.MaximumGrantsPerSubject, await competing.ListForSubjectsAsync(partition, new WorkspaceId("workspace"), [subject]));
    }

    public static async Task VerifyGrantCapacityAsync(IAccessGrantStore store)
    {
        var partition = AccessPartition.From(TestScopes.Create(tenantId: "grant-capacity"));
        var workspace = new WorkspaceGrantScope(new WorkspaceId("workspace"));
        GrantSubject subject = new UserId("busy");
        for (var index = 0; index < AccessLimits.MaximumGrantsPerSubject; index++)
            Assert.AreEqual("applied:1", Grant(await store.CreateAsync(new AccessGrant(new GrantId($"g{index}"), partition, subject, BuiltInRoles.Viewer.Id, workspace))));
        Assert.AreEqual("rejected:CapacityExceeded", Grant(await store.CreateAsync(new AccessGrant(new GrantId("overflow"), partition, subject, BuiltInRoles.Viewer.Id, workspace))));
        Assert.AreEqual("conflict:1", Grant(await store.CreateAsync(new AccessGrant(new GrantId("g0"), partition, subject, BuiltInRoles.Viewer.Id, workspace))));

        GrantSubject other = new UserId("other");
        Assert.AreEqual("applied:1", Grant(await store.CreateAsync(new AccessGrant(new GrantId("other"), partition, other, BuiltInRoles.Viewer.Id, workspace))));
        // Moving a grant onto a full subject is rejected; updating within the same subject is not.
        Assert.AreEqual("rejected:CapacityExceeded", Grant(await store.UpdateAsync(new AccessGrant(new GrantId("other"), partition, subject, BuiltInRoles.Viewer.Id, workspace), 1)));
        Assert.AreEqual("applied:2", Grant(await store.UpdateAsync(new AccessGrant(new GrantId("g0"), partition, subject, BuiltInRoles.Agent.Id, workspace), 1)));
        Assert.HasCount(AccessLimits.MaximumGrantsPerSubject, await store.ListForSubjectsAsync(partition, new WorkspaceId("workspace"), [subject]));
    }

    public static async Task VerifyFieldPoliciesAsync(IAccessFieldPolicyStore primary, IAccessFieldPolicyStore competing)
    {
        var scope = TestScopes.Create();
        var email = new AccessFieldPolicy(new FieldPath("contact.email"), [AccessPermissions.ContactRead, AccessPermissions.ConversationRead],
            [BuiltInRoles.Admin.Id], AccessPermissions.ContactReveal);
        var phone = new AccessFieldPolicy(new FieldPath("contact.phone"), [AccessPermissions.ContactRead]);
        Assert.AreEqual("applied:1", Policy(await primary.CreateAsync(scope, phone)));
        Assert.AreEqual("applied:1", Policy(await primary.CreateAsync(scope, email)));
        Assert.AreEqual("conflict:1", Policy(await competing.CreateAsync(scope, email)));
        Assert.AreEqual(email, (await competing.GetAsync(scope, email.Field))!.Policy);
        Assert.AreEqual(phone, (await competing.GetAsync(scope, phone.Field))!.Policy);
        CollectionAssert.AreEqual(new[] { "contact.email", "contact.phone" }, (await competing.ListAsync(scope)).Select(policy => policy.Policy.Field.Value).ToArray());

        var relaxed = new AccessFieldPolicy(email.Field, [AccessPermissions.ContactRead], [BuiltInRoles.Admin.Id, BuiltInRoles.Supervisor.Id]);
        Assert.AreEqual("applied:2", Policy(await competing.UpdateAsync(scope, relaxed, 1)));
        Assert.AreEqual("conflict:2", Policy(await primary.UpdateAsync(scope, email, 1)));
        Assert.AreEqual(relaxed, (await primary.GetAsync(scope, email.Field))!.Policy);
        Assert.AreEqual("not-found", Policy(await primary.UpdateAsync(scope, new AccessFieldPolicy(new FieldPath("contact.address"), [AccessPermissions.ContactRead]), 1)));

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, email.Field));
            Assert.IsEmpty(await primary.ListAsync(other));
            Assert.AreEqual("not-found", Policy(await primary.DeleteAsync(other, email.Field, 2)));
        }

        Assert.AreEqual("conflict:2", Policy(await primary.DeleteAsync(scope, email.Field, 1)));
        Assert.AreEqual("applied:2", Policy(await competing.DeleteAsync(scope, email.Field, 2)));
        Assert.IsNull(await primary.GetAsync(scope, email.Field));
        Assert.AreEqual("not-found", Policy(await primary.DeleteAsync(scope, email.Field, 2)));
    }
}
