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
}
