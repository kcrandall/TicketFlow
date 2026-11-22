using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;

namespace TicketFlow.Surveys.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public void AddSurveysRegistersServicesAndPermissions()
    {
        using var fixture = SurveyFixture.Create();
        var catalog = fixture.Provider.GetRequiredService<IPermissionCatalog>();
        foreach (var definition in SurveyPermissions.Definitions)
            Assert.AreEqual(definition, catalog.Find(definition.Permission));
        Assert.IsTrue(catalog.Contains(SurveyPermissions.ReportView));
    }

    [TestMethod]
    public void ApplicationRegistrationsWinAndRepeatedCallsAreSafe()
    {
        var services = new ServiceCollection();
        var custom = new InMemorySurveyStore(5);
        services.AddSingleton<ISurveyStore>(custom);
        services.AddSingleton<ISurveyInvitationSender>(new RecordingSender());
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddSurveys().AddSurveys().AddInMemorySurveyStores();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.AreSame(custom, provider.GetRequiredService<ISurveyStore>());
        Assert.HasCount(1, services.Where(descriptor => descriptor.ServiceType == typeof(ISurveyService)));
        Assert.HasCount(1, services.Where(descriptor => descriptor.ImplementationInstance is AccessPermissionRegistration registration && registration.Definitions.SequenceEqual(SurveyPermissions.Definitions)));
        using var scope = provider.CreateScope();
        Assert.IsInstanceOfType<SurveyDispatcher>(scope.ServiceProvider.GetRequiredService<ISurveyDispatcher>());
    }

    [TestMethod]
    public void InvalidOptionsFailFast()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => SurveyFixture.Create(options => options.MaximumSendAttempts = 0));
        StringAssert.Contains(exception.Message, "MaximumSendAttempts");
    }
}
