using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class EngineRegressionTests
{
    [TestMethod]
    public async Task TeamDenyOverridesUserAllowAndApiClientDenyApplies()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User("ada", "billing");
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        await fixture.GrantAsync(new TeamId("billing"), BuiltInRoles.Agent, effect: GrantEffect.Deny);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fixture.User("ada", "sales"), AccessPermissions.ConversationReply));

        var client = fixture.Client();
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Agent);
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Viewer, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip")), GrantEffect.Deny);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(client, AccessPermissions.ConversationRead, fixture.Resource(inbox: "vip")));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(client, AccessPermissions.ConversationReply, fixture.Resource(inbox: "vip")));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(client, AccessPermissions.ConversationRead, fixture.Resource(inbox: "email")));
    }

    [TestMethod]
    public async Task ExpiredDenyReEnablesAllow()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, effect: GrantEffect.Deny, validUntil: fixture.Clock.GetUtcNow().AddMinutes(10));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        Assert.IsFalse(AccessFilterEvaluator.Matches(await fixture.Planner.PlanAsync(new AccessQuery(user, AccessPermissions.ConversationReply, fixture.Scope)), fixture.Resource()));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        Assert.AreEqual(new ScopeFilter(fixture.Scope), await fixture.Planner.PlanAsync(new AccessQuery(user, AccessPermissions.ConversationReply, fixture.Scope)));
    }

    [TestMethod]
    public async Task TeamAndInboxGrantsInAnotherWorkspaceNeverApply()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        var elsewhere = new WorkspaceId("elsewhere");
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new TeamGrantScope(elsewhere, new TeamId("billing")));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new InboxGrantScope(elsewhere, new InboxId("email")));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(team: "billing", inbox: "email")));
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(user, AccessPermissions.ConversationReply, fixture.Scope)));
        var there = TestScopes.Create(workspaceId: "elsewhere");
        Assert.AreEqual("allowed", await fixture.DescribeAsync(TestPrincipals.User(there, "ada"), AccessPermissions.ConversationReply, fixture.Resource(team: "billing", scope: there)));
    }

    public static IEnumerable<object[]> ForeignGrants()
    {
        var scope = TestScopes.Create();
        var partition = AccessPartition.From(scope);
        yield return [new AccessGrant(new GrantId("foreign-partition"), AccessPartition.From(TestScopes.Create(tenantId: "other")), new UserId("ada"), BuiltInRoles.Agent.Id, TenantGrantScope.Instance)];
        yield return [new AccessGrant(new GrantId("foreign-workspace"), partition, new UserId("ada"), BuiltInRoles.Agent.Id, new TeamGrantScope(new WorkspaceId("elsewhere"), new TeamId("t")))];
        yield return [new AccessGrant(new GrantId("foreign-subject"), partition, new UserId("mallory"), BuiltInRoles.Agent.Id, new WorkspaceGrantScope(scope.WorkspaceId))];
    }

    [TestMethod]
    [DynamicData(nameof(ForeignGrants))]
    public async Task StoreContractViolationsFailClosed(AccessGrant foreign)
    {
        using var fixture = AccessFixture.Create(services => services.AddSingleton<IAccessGrantStore>(new FixedGrantStore(foreign)));
        var user = fixture.User();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.AuthorizeAsync(user, AccessPermissions.ConversationRead));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Planner.PlanAsync(new AccessQuery(user, AccessPermissions.ConversationRead, fixture.Scope)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Evaluator.RevealAsync(
            new RevealRequest(user, fixture.Resource(), AccessPermissions.ContactRead, new FieldPath("contact.email"))));
    }

    [TestMethod]
    public async Task RevealAndSensitivePlansFailClosedWhenAuditFails()
    {
        var sink = new SwitchableAuditSink { Fail = true };
        using var fixture = AccessFixture.Create(services => services.AddSingleton<IAccessAuditSink>(sink));
        var supervisor = fixture.User("supervisor");
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Evaluator.RevealAsync(
            new RevealRequest(supervisor, fixture.Resource(), AccessPermissions.ContactRead, new FieldPath("contact.email"))));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Planner.PlanAsync(new AccessQuery(supervisor, AccessPermissions.ContactExport, fixture.Scope)));
        // Non-sensitive plans do not audit and still succeed.
        Assert.AreEqual(new ScopeFilter(fixture.Scope), await fixture.Planner.PlanAsync(new AccessQuery(supervisor, AccessPermissions.ConversationRead, fixture.Scope)));
    }

    [TestMethod]
    public async Task PlannerCoalescesMembershipSiblings()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        foreach (var team in new[] { "t1", "t2" })
            await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId(team)));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i1")));
        foreach (var team in new[] { "t3", "t4" })
            await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId(team)), GrantEffect.Deny);
        var expected = new AndFilter(
            new ScopeFilter(fixture.Scope),
            new OrFilter(new TeamInFilter([new TeamId("t1"), new TeamId("t2")]), new InboxInFilter([new InboxId("i1")])),
            new NotFilter(new TeamInFilter([new TeamId("t3"), new TeamId("t4")])));
        var plan = await fixture.Planner.PlanAsync(new AccessQuery(user, AccessPermissions.ConversationReply, fixture.Scope));
        Assert.AreEqual(expected, plan);
        foreach (var resource in new[] { fixture.Resource(team: "t1"), fixture.Resource(team: "t3", inbox: "i1"), fixture.Resource(inbox: "i1"), fixture.Resource(team: "t9") })
            Assert.AreEqual((await fixture.AuthorizeAsync(user, AccessPermissions.ConversationReply, resource)).IsPermitted, AccessFilterEvaluator.Matches(plan, resource));
    }

    [TestMethod]
    public async Task OversizedPlansFailClosedAndAreAudited()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        var wide = new OrCondition(Enumerable.Range(0, 60).Select(index => (AccessCondition)new AttributeEqualsCondition("tier", $"v{index}")));
        for (var index = 0; index < 80; index++)
            await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, condition: new AndCondition(wide, new AttributeEqualsCondition("region", $"r{index}")));
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(user, AccessPermissions.ConversationReply, fixture.Scope)));
        var audit = fixture.Audit.Snapshot().Single();
        Assert.AreEqual((AccessAuditKind.FilterPlanned, AccessAuditOutcome.Denied, AccessDenialReason.FilterLimitExceeded), (audit.Kind, audit.Outcome, audit.DenialReason));
        // Point checks still work for individual resources.
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(attributes: new() { ["tier"] = "v3", ["region"] = "r7" })));
    }

    [TestMethod]
    public async Task CustomRolesResolveWithOneStoreRoundTrip()
    {
        var roles = new CountingRoleStore();
        using var fixture = AccessFixture.Create(services => services.AddSingleton<IAccessRoleStore>(roles));
        var user = fixture.User();
        foreach (var id in new[] { "a", "b", "c" })
        {
            await roles.CreateAsync(fixture.Scope, new AccessRole(new RoleId(id), id, [AccessPermissions.ConversationRead]));
            await fixture.GrantAsync(user.UserId, new AccessRole(new RoleId(id), id, []));
        }
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationRead));
        Assert.AreEqual((1, 0), (roles.Lists, roles.Gets));
        await fixture.GrantAsync(fixture.User("bob").UserId, BuiltInRoles.Agent);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fixture.User("bob"), AccessPermissions.ConversationRead));
        Assert.AreEqual((1, 0), (roles.Lists, roles.Gets));
    }

    private sealed class CountingRoleStore : IAccessRoleStore
    {
        private readonly InMemoryAccessRoleStore inner = new();

        public int Gets { get; private set; }
        public int Lists { get; private set; }

        public Task<StoredAccessRole?> GetAsync(TicketScope scope, RoleId id, CancellationToken cancellationToken = default)
        {
            Gets++;
            return inner.GetAsync(scope, id, cancellationToken);
        }

        public Task<IReadOnlyList<StoredAccessRole>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
        {
            Lists++;
            return inner.ListAsync(scope, cancellationToken);
        }

        public Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> CreateAsync(TicketScope scope, AccessRole role, CancellationToken cancellationToken = default) => inner.CreateAsync(scope, role, cancellationToken);

        public Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> UpdateAsync(TicketScope scope, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default) => inner.UpdateAsync(scope, role, expectedRevision, cancellationToken);

        public Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> DeleteAsync(TicketScope scope, RoleId id, long expectedRevision, CancellationToken cancellationToken = default) => inner.DeleteAsync(scope, id, expectedRevision, cancellationToken);
    }
}
