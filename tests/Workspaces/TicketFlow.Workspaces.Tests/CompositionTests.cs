using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;

namespace TicketFlow.Workspaces.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public void AddWorkspacesRegistersServicesAndPermissions()
    {
        using var fixture = WorkspaceFixture.Create();
        var catalog = fixture.Provider.GetRequiredService<IPermissionCatalog>();
        foreach (var definition in WorkspacePermissions.Definitions)
            Assert.AreEqual(definition, catalog.Find(definition.Permission));
        CollectionAssert.AreEqual(
            new[] { "calendar:manage", "inbox:manage", "inbox:read", "team:manage", "team:read", "workspace:manage", "workspace:read" },
            WorkspacePermissions.Definitions.Select(definition => definition.Permission.Value).ToArray());
        foreach (var role in BuiltInRoles.All)
        {
            Assert.IsTrue(role.Includes(WorkspacePermissions.InboxRead), role.Name);
            Assert.IsTrue(role.Includes(WorkspacePermissions.TeamRead), role.Name);
            Assert.IsTrue(role.Includes(WorkspacePermissions.WorkspaceRead), role.Name);
        }
        Assert.IsTrue(BuiltInRoles.Admin.Includes(WorkspacePermissions.CalendarManage));
        Assert.IsFalse(BuiltInRoles.Supervisor.Includes(WorkspacePermissions.CalendarManage));
        Assert.IsFalse(BuiltInRoles.Agent.Includes(WorkspacePermissions.InboxManage));
    }

    [TestMethod]
    public void ApplicationRegistrationsWinAndRepeatedCallsAreSafe()
    {
        var services = new ServiceCollection();
        var custom = new InMemoryInboxStore(5);
        services.AddSingleton<IInboxStore>(custom);
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddWorkspaces().AddWorkspaces().AddInMemoryWorkspaceStores().AddInMemoryWorkspaceStores();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.AreSame(custom, provider.GetRequiredService<IInboxStore>());
        Assert.HasCount(1, services.Where(descriptor => descriptor.ServiceType == typeof(IInboxService)));
        using var scope = provider.CreateScope();
        Assert.IsInstanceOfType<InboxService>(scope.ServiceProvider.GetRequiredService<IInboxService>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IPermissionCatalog>());
    }
}
