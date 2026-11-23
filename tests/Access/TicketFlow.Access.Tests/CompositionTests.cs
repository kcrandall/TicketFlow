using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Core;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class CompositionTests
{
    private sealed class DenyEverything : IAccessEvaluator
    {
        public Task<AccessDecision> AuthorizeAsync(AccessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<AccessDecision>(new AccessDenied(request.Permission, AccessDenialReason.NoMatchingGrant));

        public Task<RevealResult> RevealAsync(RevealRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<RevealResult>(new RevealRefused(request.Field, RevealRefusal.RevealDenied, Guid.NewGuid()));
    }

    [TestMethod]
    public void AccessControlRegistersTheEngineWithoutSelectingStores()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow().AddAccessControl();
        foreach (var engine in new[] { typeof(IAccessEvaluator), typeof(IAccessQueryPlanner), typeof(IAccessAdministrator) })
            Assert.AreEqual(ServiceLifetime.Scoped, services.Single(descriptor => descriptor.ServiceType == engine).Lifetime);
        Assert.AreEqual(ServiceLifetime.Singleton, services.Single(descriptor => descriptor.ServiceType == typeof(IPermissionCatalog)).Lifetime);
        Assert.IsFalse(services.Any(descriptor => descriptor.ServiceType == typeof(IAccessGrantStore)));
        var count = services.Count;
        services.AddTicketFlow().AddAccessControl().AddAccessControl();
        Assert.AreEqual(count, services.Count);

        services.AddTicketFlow().AddInMemoryAccessStores();
        var withStores = services.Count;
        services.AddTicketFlow().AddInMemoryAccessStores();
        Assert.AreEqual(withStores, services.Count);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var request = provider.CreateScope();
        Assert.IsInstanceOfType<AccessEvaluator>(request.ServiceProvider.GetRequiredService<IAccessEvaluator>());
        Assert.IsInstanceOfType<AccessQueryPlanner>(request.ServiceProvider.GetRequiredService<IAccessQueryPlanner>());
        Assert.IsInstanceOfType<AccessAdministrator>(request.ServiceProvider.GetRequiredService<IAccessAdministrator>());
        Assert.AreSame(provider.GetRequiredService<InMemoryAccessAuditSink>(), provider.GetRequiredService<IAccessAuditSink>());
    }

    [TestMethod]
    public async Task ApplicationRegistrationsArePreserved()
    {
        var store = new InMemoryAccessGrantStore();
        var services = new ServiceCollection();
        services.AddSingleton<IAccessEvaluator, DenyEverything>();
        services.AddSingleton<IAccessGrantStore>(store);
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores();
        using var provider = services.BuildServiceProvider();
        using var request = provider.CreateScope();
        Assert.IsInstanceOfType<DenyEverything>(request.ServiceProvider.GetRequiredService<IAccessEvaluator>());
        Assert.AreSame(store, provider.GetRequiredService<IAccessGrantStore>());

        var scope = Testing.TestScopes.Create();
        await store.CreateAsync(new AccessGrant(new GrantId("g"), AccessPartition.From(scope), new UserId("ada"), BuiltInRoles.Agent.Id, new WorkspaceGrantScope(scope.WorkspaceId)));
        var plan = await request.ServiceProvider.GetRequiredService<IAccessQueryPlanner>().PlanAsync(new AccessQuery(Testing.TestPrincipals.User(scope, "ada"), AccessPermissions.ConversationRead, scope));
        Assert.AreEqual(new ScopeFilter(scope), plan);
    }

    [TestMethod]
    public void OptionsAreValidated()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow().AddAccessControl(options => options.MaximumBreakGlassDuration = TimeSpan.FromHours(25)).AddInMemoryAccessStores();
        using var provider = services.BuildServiceProvider();
        using var request = provider.CreateScope();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => request.ServiceProvider.GetRequiredService<IAccessAdministrator>());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AccessControlOptions { MinimumBreakGlassJustificationLength = 0 }.Validate());
        new AccessControlOptions().Validate();
    }

    [TestMethod]
    public void NullArgumentsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => AccessTicketFlowBuilderExtensions.AddAccessControl(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => AccessTicketFlowBuilderExtensions.AddInMemoryAccessStores(null!));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessRequest(null!, AccessPermissions.ConversationRead, new AccessResource(Testing.TestScopes.Create())));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessResource(Testing.TestScopes.Create(), team: default(TeamId)));
        Assert.ThrowsExactly<ArgumentException>(() => new AccessResource(Testing.TestScopes.Create(), attributes: [new("Bad Key", "v")]));
        Assert.ThrowsExactly<ArgumentException>(() => new FieldPath("contact"));
        Assert.ThrowsExactly<ArgumentException>(() => new FieldPath("contact..email"));
        Assert.AreEqual("contact.email", new FieldPath("contact.email").Value);
    }
}
