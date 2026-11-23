using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.HelpCenter.Tests;

/// <summary>In-memory access engine plus help center services with a fake clock. Grants are seeded directly into the grant store.</summary>
internal sealed class HelpCenterFixture : IDisposable
{
    private readonly IServiceScope requestScope;
    private int nextGrant;

    private HelpCenterFixture(ServiceProvider provider, FakeTimeProvider clock)
    {
        Provider = provider;
        Clock = clock;
        requestScope = provider.CreateScope();
        var services = requestScope.ServiceProvider;
        Service = services.GetRequiredService<IHelpCenterService>();
        Public = services.GetRequiredService<IHelpCenterPublicReader>();
        Search = services.GetRequiredService<IHelpCenterSearch>();
        Store = services.GetRequiredService<IHelpCenterStore>();
        Grants = services.GetRequiredService<IAccessGrantStore>();
        Roles = services.GetRequiredService<IAccessRoleStore>();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public IHelpCenterService Service { get; }
    public IHelpCenterPublicReader Public { get; }
    public IHelpCenterSearch Search { get; }
    public IHelpCenterStore Store { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public TicketScope Scope { get; } = TestScopes.Create();

    public static HelpCenterFixture Create()
    {
        var clock = TestClock.Create();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddHelpCenter().AddInMemoryHelpCenterStores();
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock);
    }

    public UserPrincipal User(string id) => TestPrincipals.User(Scope, id);

    /// <summary>A user holding <paramref name="permissions"/> through a workspace-scoped custom role.</summary>
    public async Task<UserPrincipal> UserWithAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId("role-" + id), id, permissions);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        var user = User(id);
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), user.UserId, role.Id, new WorkspaceGrantScope(Scope.WorkspaceId));
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await Grants.CreateAsync(grant)).Value);
        return user;
    }

    public async Task<UserPrincipal> EditorAsync(string id = "editor") =>
        await UserWithAsync(id, HelpCenterPermissions.Read, HelpCenterPermissions.Write, HelpCenterPermissions.Publish, HelpCenterPermissions.Manage);

    public static T Value<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> succeeded => succeeded.Value,
        TicketFailure refused => throw new AssertFailedException($"Expected success but was refused: {refused.Kind} {string.Join("; ", refused.Errors)}"),
    };

    public static string Describe<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> => "ok",
        TicketFailure refused => $"refused:{refused.Kind}" + (refused.CurrentRevision is { } revision ? $":{revision}" : ""),
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }
}
