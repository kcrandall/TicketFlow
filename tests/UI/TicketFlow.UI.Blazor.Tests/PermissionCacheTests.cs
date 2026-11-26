using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.UI.Blazor.Infrastructure;

namespace TicketFlow.UI.Blazor.Tests;

[TestClass]
public sealed class PermissionCacheTests
{
    [TestMethod]
    public void SensitiveSnapshotsExpireAndDependOnTeamMembership()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var cache = new UiSensitivePermissionCache(time);
        var plain = UiSensitivePermissionCache.Key(new UserPrincipal(UiTestSupport.Scope, new UserId("agent")));
        var inTeam = UiSensitivePermissionCache.Key(new UserPrincipal(UiTestSupport.Scope, new UserId("agent"), [new TeamId("tier1")]));
        var withExternalRole = UiSensitivePermissionCache.Key(new UserPrincipal(UiTestSupport.Scope, new UserId("agent"), externalRoles: ["idf:support-agents"]));
        Assert.AreNotEqual(plain, inTeam, "A team change must not reuse a snapshot taken for other grants.");
        Assert.AreNotEqual(plain, withExternalRole, "An external role change must not reuse a cached snapshot.");

        cache.Set(plain, new HashSet<Permission> { AccessPermissions.AccessManage }, TimeSpan.FromSeconds(60));
        Assert.IsTrue(cache.TryGet(plain, out var held));
        Assert.Contains(AccessPermissions.AccessManage, held);
        Assert.IsFalse(cache.TryGet(inTeam, out _));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.IsFalse(cache.TryGet(plain, out _), "Snapshots expire so revoked grants disappear from navigation.");

        cache.Set(plain, new HashSet<Permission>(), TimeSpan.Zero);
        Assert.IsFalse(cache.TryGet(plain, out _), "A zero duration disables caching.");
    }

    [TestMethod]
    public void CacheKeysIsolateTenantsWorkspacesIdentityProvidersAndDelimiters()
    {
        var scopeA = new TicketScope("tenant-a", "proj", "prod", "app", new WorkspaceId("ws-1"));
        var scopeB = new TicketScope("tenant-b", "proj", "prod", "app", new WorkspaceId("ws-1"));
        var scopeWs2 = new TicketScope("tenant-a", "proj", "prod", "app", new WorkspaceId("ws-2"));

        var tenantAKey = UiSensitivePermissionCache.Key(new UserPrincipal(scopeA, new UserId("alice")));
        var tenantBKey = UiSensitivePermissionCache.Key(new UserPrincipal(scopeB, new UserId("alice")));
        var ws2Key = UiSensitivePermissionCache.Key(new UserPrincipal(scopeWs2, new UserId("alice")));
        Assert.AreNotEqual(tenantAKey, tenantBKey, "Different tenants must never share cache keys.");
        Assert.AreNotEqual(tenantAKey, ws2Key, "Different workspaces must never share cache keys.");

        var idpA = UiSensitivePermissionCache.Key(new UserPrincipal(scopeA, new UserId("alice"), identityProvider: "identityflow"));
        var idpB = UiSensitivePermissionCache.Key(new UserPrincipal(scopeA, new UserId("alice"), identityProvider: "okta"));
        var idpUpper = UiSensitivePermissionCache.Key(new UserPrincipal(scopeA, new UserId("alice"), identityProvider: "IDENTITYFLOW"));
        Assert.AreNotEqual(tenantAKey, idpA);
        Assert.AreNotEqual(idpA, idpB);
        Assert.AreEqual(idpA, idpUpper);

        // Delimiter injection in userId / team must never collide
        var injected = UiSensitivePermissionCache.Key(new UserPrincipal(scopeA, new UserId("alice|tier1")));
        var split = UiSensitivePermissionCache.Key(new UserPrincipal(scopeA, new UserId("alice"), [new TeamId("tier1")]));
        Assert.AreNotEqual(injected, split);
    }
}
