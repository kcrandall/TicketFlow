using TicketFlow.Testing;

namespace TicketFlow.Core.Tests;

[TestClass]
public sealed class PrincipalTests
{
    // Exhaustive by construction: adding a union case breaks this switch at compile time because there is no discard arm.
    private static string Describe(PrincipalRef principal) => principal switch
    {
        UserPrincipal user => "user:" + user.UserId.Value,
        ApiClientPrincipal client => "api:" + client.ClientId.Value,
        AutomationPrincipal automation => "automation:" + automation.AutomationId,
        SystemPrincipal system => "system:" + system.Component,
    };

    [TestMethod]
    public void ExhaustiveSwitchCoversEveryPrincipalKind()
    {
        var described = TestPrincipals.AllKinds().Select(Describe).ToArray();
        CollectionAssert.AreEqual(new[] { "user:user", "api:api-client", "automation:automation", "system:system" }, described);
    }

    [TestMethod]
    public void EveryPrincipalKindCarriesItsFullScope()
    {
        var scope = TestScopes.Create(workspaceId: "w-7");
        foreach (var principal in TestPrincipals.AllKinds(scope))
            Assert.AreSame(scope, principal.Scope);
    }

    [TestMethod]
    public void PrincipalRefIsClosedHierarchy()
    {
        Assert.IsTrue(typeof(PrincipalRef).IsAbstract);
    }

    [TestMethod]
    public void PrincipalsRejectMissingScopeAndUninitializedIdentifiers()
    {
        var scope = TestScopes.Create();
        Assert.AreEqual("scope", Assert.ThrowsExactly<ArgumentNullException>(() => new UserPrincipal(null!, new UserId("u"))).ParamName);
        Assert.AreEqual("userId", Assert.ThrowsExactly<ArgumentException>(() => new UserPrincipal(scope, default)).ParamName);
        Assert.AreEqual("clientId", Assert.ThrowsExactly<ArgumentException>(() => new ApiClientPrincipal(scope, default)).ParamName);
        Assert.AreEqual("scope", Assert.ThrowsExactly<ArgumentNullException>(() => new ApiClientPrincipal(null!, new ApiClientId("c"))).ParamName);
        Assert.AreEqual("automationId", Assert.ThrowsExactly<ArgumentException>(() => new AutomationPrincipal(scope, " ")).ParamName);
        Assert.AreEqual("component", Assert.ThrowsExactly<ArgumentException>(() => new SystemPrincipal(scope, "a\u0007")).ParamName);
    }

    [TestMethod]
    public void TeamSnapshotIsBoundedDeduplicatedAndValueEqual()
    {
        var scope = TestScopes.Create();
        var user = TestPrincipals.User(scope, "u", "b", "a", "a");
        Assert.HasCount(2, user.Teams);
        Assert.IsTrue(user.Teams.Contains(new TeamId("a")));
        Assert.AreEqual(user, TestPrincipals.User(scope, "u", "a", "b"));
        Assert.AreEqual(user.GetHashCode(), TestPrincipals.User(scope, "u", "a", "b").GetHashCode());
        Assert.AreNotEqual(user, TestPrincipals.User(scope, "u", "a"));
        Assert.AreNotEqual(user, TestPrincipals.User(TestScopes.Create(workspaceId: "other"), "u", "a", "b"));
        Assert.IsEmpty(new UserPrincipal(scope, new UserId("u")).Teams);

        var maximum = Enumerable.Range(0, UserPrincipal.MaximumTeamMemberships).Select(i => new TeamId("t" + i)).ToList();
        Assert.HasCount(UserPrincipal.MaximumTeamMemberships, new UserPrincipal(scope, new UserId("u"), maximum.Concat(maximum)).Teams);
        maximum.Add(new TeamId("one-too-many"));
        Assert.AreEqual("teams", Assert.ThrowsExactly<ArgumentException>(() => new UserPrincipal(scope, new UserId("u"), maximum)).ParamName);
        Assert.AreEqual("teams", Assert.ThrowsExactly<ArgumentException>(() => new UserPrincipal(scope, new UserId("u"), [default])).ParamName);
    }

    [TestMethod]
    public void TeamSnapshotIsDetachedFromCallerCollection()
    {
        var teams = new List<TeamId> { new("a") };
        var user = new UserPrincipal(TestScopes.Create(), new UserId("u"), teams);
        teams.Add(new TeamId("b"));
        Assert.HasCount(1, user.Teams);
        Assert.IsTrue(((ICollection<TeamId>)user.Teams).IsReadOnly);
    }

    [TestMethod]
    public void PrincipalsExposeNoCredentialShapedMembers()
    {
        foreach (var type in new[] { typeof(UserPrincipal), typeof(ApiClientPrincipal), typeof(AutomationPrincipal), typeof(SystemPrincipal) })
        {
            foreach (var property in type.GetProperties())
                Assert.IsFalse(
                    new[] { "secret", "token", "password", "credential", "key", "claim" }.Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)),
                    $"{type.Name}.{property.Name}");
        }
    }

    [TestMethod]
    public void PrincipalToStringIsRedacted()
    {
        foreach (var principal in TestPrincipals.AllKinds(TestScopes.Create(tenantId: "secret-tenant")))
        {
            var text = principal.ToString();
            StringAssert.Contains(text, "<redacted>");
            Assert.DoesNotContain("secret-tenant", text);
        }
    }
}
