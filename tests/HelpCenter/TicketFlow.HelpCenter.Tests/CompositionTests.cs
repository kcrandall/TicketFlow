using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;

namespace TicketFlow.HelpCenter.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public void AddHelpCenterRegistersServicesAndPermissions()
    {
        using var fixture = HelpCenterFixture.Create();
        var catalog = fixture.Provider.GetRequiredService<IPermissionCatalog>();
        foreach (var definition in HelpCenterPermissions.Definitions)
            Assert.AreEqual(definition, catalog.Find(definition.Permission));
        Assert.IsInstanceOfType<SafeMarkdownRenderer>(fixture.Provider.GetRequiredService<IArticleRenderer>());
        Assert.AreSame<object>(fixture.Provider.GetRequiredService<IHelpCenterStore>(), fixture.Provider.GetRequiredService<IArticleFeedbackStore>());
    }

    [TestMethod]
    public void BuilderExtensionsLiveInTheFeatureNamespace() =>
        Assert.AreEqual("TicketFlow.HelpCenter", typeof(HelpCenterTicketFlowBuilderExtensions).Namespace);

    [TestMethod]
    public void ApplicationRegistrationsWinAndRepeatedCallsAreSafe()
    {
        var services = new ServiceCollection();
        var custom = new InMemoryHelpCenterStore(5);
        services.AddSingleton<IHelpCenterStore>(custom);
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddHelpCenter().AddHelpCenter().AddInMemoryHelpCenterStores().AddInMemoryHelpCenterStores();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.AreSame(custom, provider.GetRequiredService<IHelpCenterStore>());
        Assert.HasCount(1, services.Where(descriptor => descriptor.ServiceType == typeof(IHelpCenterService)));
        Assert.HasCount(1, services.Where(descriptor => descriptor.ServiceType == typeof(AccessPermissionRegistration)
            && descriptor.ImplementationInstance is AccessPermissionRegistration registration && registration.Definitions.SequenceEqual(HelpCenterPermissions.Definitions)));
        using var scope = provider.CreateScope();
        Assert.AreSame<object>(scope.ServiceProvider.GetRequiredService<IHelpCenterPublicReader>(), scope.ServiceProvider.GetRequiredService<IHelpCenterSearch>());
        Assert.IsNotNull(scope.ServiceProvider.GetRequiredService<IPermissionCatalog>());
    }
}
