using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Access.StoreContracts;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class FilterParityTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(42)]
    [DataRow(2026)]
    [DataRow(31337)]
    public async Task FilterResultsEqualPointChecksOverRandomizedFixtures(int seed)
    {
        using var fixture = AccessFixture.Create(services => services.AddAccessPermissions(new PermissionDefinition("triage:run")), auditCapacity: 1_000_000);
        var random = new Random(seed);
        fixture.Clock.Advance(TimeSpan.FromMinutes(random.Next(24 * 60)));
        await fixture.CustomRoleAsync(RandomAccessFixture.CustomRole.Id.Value, [.. RandomAccessFixture.CustomRole.Permissions]);
        await fixture.CustomRoleAsync(RandomAccessFixture.DeletedRole.Id.Value, [.. RandomAccessFixture.DeletedRole.Permissions]);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await fixture.Roles.DeleteAsync(fixture.Scope, RandomAccessFixture.DeletedRole.Id, 1)).Value);
        var grants = RandomAccessFixture.Grants(random, fixture.Scope, fixture.Clock.GetUtcNow(), 40);
        foreach (var grant in grants)
            await fixture.SeedAsync(grant);
        var resources = RandomAccessFixture.Resources(random, fixture.Scope, 150);
        int permitted = 0, denied = 0;
        foreach (var principal in RandomAccessFixture.Principals(random, fixture.Scope))
        {
            foreach (var permission in RandomAccessFixture.Permissions.Append(new Permission("unknown:thing")))
            {
                var filter = await fixture.Planner.PlanAsync(new AccessQuery(principal, permission, fixture.Scope));
                foreach (var resource in resources)
                {
                    var decision = await fixture.AuthorizeAsync(principal, permission, resource);
                    Assert.AreEqual(decision.IsPermitted, AccessFilterEvaluator.Matches(filter, resource),
                        $"seed {seed}: {principal.GetType().Name} {permission} {resource.ResourceId} {AccessFixture.Describe(decision)}");
                    if (decision.IsPermitted)
                        permitted++;
                    else
                        denied++;
                }
            }
        }
        Assert.IsGreaterThan(100, permitted, "The fixture should permit a meaningful number of checks.");
        Assert.IsGreaterThan(100, denied);
    }

    [TestMethod]
    public void RandomFixturesCoverEveryGrantShape()
    {
        var scope = TestScopes.Create();
        var grants = new[] { 1, 7, 42, 2026, 31337 }.SelectMany(seed => RandomAccessFixture.Grants(new Random(seed), scope, TestClock.DefaultStart, 40)).ToList();
        Assert.IsTrue(grants.Any(grant => grant.Kind == GrantKind.BreakGlass && grant.IsValidAt(TestClock.DefaultStart)));
        Assert.IsTrue(grants.Any(grant => grant.Kind == GrantKind.BreakGlass && !grant.IsValidAt(TestClock.DefaultStart)));
        Assert.IsTrue(grants.Any(grant => grant.Role == RandomAccessFixture.DeletedRole.Id && grant.Effect == GrantEffect.Deny));
        Assert.IsTrue(grants.Any(grant => grant.Role == RandomAccessFixture.DeletedRole.Id && grant.Effect == GrantEffect.Allow));
        Assert.IsTrue(grants.Any(grant => grant.Role == RandomAccessFixture.CustomRole.Id && grant.Scope is TenantGrantScope));
        foreach (var level in new[] { typeof(TenantGrantScope), typeof(WorkspaceGrantScope), typeof(TeamGrantScope), typeof(InboxGrantScope) })
            Assert.IsTrue(grants.Any(grant => grant.Scope.GetType() == level), level.Name);
        Assert.IsTrue(grants.Any(grant => grant.Subject.Value is TeamId) && grants.Any(grant => grant.Subject.Value is ApiClientId));
        Assert.IsTrue(grants.Any(grant => grant.Effect == GrantEffect.Deny && grant.Condition is not null));
    }

    [TestMethod]
    public async Task PlansFoldToMinimalFilters()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User("ada", "billing");
        var query = new AccessQuery(user, AccessPermissions.ConversationReply, fixture.Scope);
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(query));

        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        Assert.AreEqual(new ScopeFilter(fixture.Scope), await fixture.Planner.PlanAsync(query));

        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip")), GrantEffect.Deny);
        var expected = new AndFilter(new ScopeFilter(fixture.Scope), new NotFilter(new InboxInFilter([new InboxId("vip")])));
        Assert.AreEqual(expected, await fixture.Planner.PlanAsync(query));

        var client = fixture.Client();
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Agent, condition: new AssigneeIsSelfCondition());
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(client, AccessPermissions.ConversationReply, fixture.Scope)));
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Viewer);
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Viewer, effect: GrantEffect.Deny, condition: new NotCondition(new AssigneeIsSelfCondition()));
        var unknownDeny = await fixture.Planner.PlanAsync(new AccessQuery(client, AccessPermissions.ConversationRead, fixture.Scope));
        Assert.AreEqual(new AndFilter(new ScopeFilter(fixture.Scope), UnknownFilter.Instance), unknownDeny);
        Assert.IsFalse(AccessFilterEvaluator.Matches(unknownDeny, fixture.Resource()));
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(TestPrincipals.Automation(fixture.Scope), AccessPermissions.ConversationRead, fixture.Scope)));
    }

    [TestMethod]
    public void KleeneFoldingMatchesEvaluation()
    {
        var resource = new AccessResource(TestScopes.Create(), labels: null);
        var unknown = new HasLabelFilter(new LabelId("vip"));
        Assert.IsNull(AccessFilterEvaluator.Evaluate(unknown, resource));
        Assert.IsNull(AccessFilterEvaluator.Evaluate(new NotFilter(unknown), resource));
        Assert.AreSame(DenyAllFilter.Instance, AccessFilters.And(unknown, DenyAllFilter.Instance));
        Assert.AreSame(AllowAllFilter.Instance, AccessFilters.Or(unknown, AllowAllFilter.Instance));
        Assert.AreSame(UnknownFilter.Instance, AccessFilters.Not(UnknownFilter.Instance));
        Assert.AreSame(unknown, AccessFilters.Not(AccessFilters.Not(unknown)));
        Assert.AreSame(AllowAllFilter.Instance, AccessFilters.And());
        Assert.AreSame(DenyAllFilter.Instance, AccessFilters.Or());
        var flattened = (AndFilter)AccessFilters.And(new AndFilter(unknown, unknown), unknown);
        Assert.HasCount(3, flattened.Operands);
        Assert.IsFalse(AccessFilterEvaluator.Matches(new OrFilter(unknown, DenyAllFilter.Instance), resource));
        Assert.IsTrue(AccessFilterEvaluator.Matches(new OrFilter(unknown, AllowAllFilter.Instance), resource));
        Assert.IsNull(AccessFilterEvaluator.Evaluate(new AttributeInFilter("tier", ["vip"]), resource));
        Assert.IsFalse(AccessFilterEvaluator.Evaluate(new TeamInFilter([new TeamId("t")]), resource));
        Assert.IsFalse(AccessFilterEvaluator.Evaluate(new AssigneeIsFilter(new UserId("u")), resource));
    }
}
