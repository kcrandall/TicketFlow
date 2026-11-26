using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class PermissionCatalogTests
{
    [TestMethod]
    public void PermissionsParseResourceAndAction()
    {
        var permission = new Permission("conversation:reply");
        Assert.AreEqual("conversation", permission.Resource);
        Assert.AreEqual("reply", permission.Action);
        Assert.AreEqual(permission, new Permission("conversation", "reply"));
        Assert.IsTrue(Permission.TryParse("custom.attr:read-sensitive_v2", out var custom));
        Assert.AreEqual("custom.attr:read-sensitive_v2", custom.ToString());
        Assert.AreEqual(new string('a', 64) + ":" + new string('b', 64), new Permission(new string('a', 64) + ":" + new string('b', 64)).Value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("conversation")]
    [DataRow(":reply")]
    [DataRow("conversation:")]
    [DataRow("Conversation:reply")]
    [DataRow("conversation:reply:extra")]
    [DataRow("conver sation:reply")]
    [DataRow("1conversation:reply")]
    [DataRow("conversation:-reply")]
    [DataRow("conversation:réply")]
    [DataRow("conversation:reply\n")]
    public void MalformedPermissionsAreRejected(string value)
    {
        Assert.IsFalse(Permission.TryParse(value, out var parsed));
        Assert.IsFalse(parsed.IsInitialized);
        Assert.ThrowsExactly<ArgumentException>(() => new Permission(value));
    }

    [TestMethod]
    public void OversizedSegmentsAndNullAreRejected()
    {
        Assert.IsFalse(Permission.TryParse(new string('a', 65) + ":read", out _));
        Assert.IsFalse(Permission.TryParse("read:" + new string('a', 65), out _));
        Assert.IsFalse(Permission.TryParse(null, out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(Permission).Value);
    }

    [TestMethod]
    public void CatalogContainsBuiltInsAndRejectsUnknownPermissions()
    {
        var catalog = PermissionCatalog.Create();
        foreach (var definition in AccessPermissions.Definitions)
            Assert.IsTrue(catalog.Contains(definition.Permission));
        Assert.IsFalse(catalog.Contains(new Permission("macro:run")));
        Assert.IsFalse(catalog.Contains(default));
        Assert.IsNull(catalog.Find(default));
        Assert.IsTrue(catalog.Find(AccessPermissions.ContactExport)!.IsSensitive);
        Assert.IsFalse(catalog.Find(AccessPermissions.ConversationRead)!.IsSensitive);
        CollectionAssert.AreEqual(catalog.Definitions.Select(definition => definition.Permission.Value).Order(StringComparer.Ordinal).ToArray(),
            catalog.Definitions.Select(definition => definition.Permission.Value).ToArray());
    }

    [TestMethod]
    public void DuplicateRegistrationIsDeterministic()
    {
        var macro = new PermissionDefinition("macro:run", false, "Run macros.");
        var catalog = PermissionCatalog.Create([macro, new PermissionDefinition("macro:run", false, "Run macros."), macro]);
        Assert.AreEqual(macro, catalog.Find(macro.Permission));
        Assert.AreEqual(AccessPermissions.Definitions.Count + 1, catalog.Definitions.Count);

        var conflicts = new[]
        {
            new PermissionDefinition("zeta:run"), new PermissionDefinition("zeta:run", true),
            new PermissionDefinition("alpha:run"), new PermissionDefinition("alpha:run", false, "Different."),
        };
        var first = Assert.ThrowsExactly<ArgumentException>(() => PermissionCatalog.Create(conflicts));
        var second = Assert.ThrowsExactly<ArgumentException>(() => PermissionCatalog.Create(conflicts.Reverse()));
        StringAssert.Contains(first.Message, "'alpha:run'", StringComparison.Ordinal);
        Assert.AreEqual(first.Message, second.Message);
        Assert.ThrowsExactly<ArgumentException>(() => PermissionCatalog.Create([new PermissionDefinition(AccessPermissions.ContactExport, isSensitive: false)]));
    }

    [TestMethod]
    public void DefinitionsValidateDescriptions()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PermissionDefinition(default(Permission)));
        Assert.ThrowsExactly<ArgumentException>(() => new PermissionDefinition("macro:run", description: new string('d', 257)));
        Assert.ThrowsExactly<ArgumentException>(() => new PermissionDefinition("macro:run", description: "bad\u0000"));
    }

    [TestMethod]
    public void FeatureRegistrationsReachTheCatalogThroughDependencyInjection()
    {
        using var fixture = AccessFixture.Create(services => services.AddAccessPermissions(new PermissionDefinition("survey:send", true)));
        Assert.IsTrue(fixture.Catalog.Contains(new Permission("macro:run")));
        Assert.IsTrue(fixture.Catalog.Find(new Permission("survey:send"))!.IsSensitive);

        using var conflicting = new ServiceCollection()
            .AddAccessPermissions(new PermissionDefinition("survey:send"))
            .AddAccessPermissions(new PermissionDefinition("survey:send", true))
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .BuildServiceProviderWithAccess();
        Assert.ThrowsExactly<ArgumentException>(() => conflicting.GetRequiredService<IPermissionCatalog>());
    }

    [TestMethod]
    public async Task UnknownPermissionsAreDeniedEvenForOwners()
    {
        using var fixture = AccessFixture.Create();
        var owner = fixture.User();
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(owner, new Permission("macro:run")));
        Assert.AreEqual("denied:UnknownPermission", await fixture.DescribeAsync(owner, new Permission("macro:delete")));
        Assert.AreEqual("denied:UnknownPermission", await fixture.DescribeAsync(owner, default));
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(owner, new Permission("macro:delete"), fixture.Scope)));
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static ServiceProvider BuildServiceProviderWithAccess(this IServiceCollection services)
    {
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores();
        return services.BuildServiceProvider();
    }
}
