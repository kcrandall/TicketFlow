using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

/// <summary>An in-memory access engine with a fake clock. Grants seeded through <see cref="GrantAsync"/> bypass the administrator.</summary>
internal sealed class AccessFixture : IDisposable
{
    private int nextGrant;

    private readonly IServiceScope requestScope;

    private AccessFixture(ServiceProvider provider, FakeTimeProvider clock)
    {
        Provider = provider;
        Clock = clock;
        requestScope = provider.CreateScope();
        Evaluator = requestScope.ServiceProvider.GetRequiredService<IAccessEvaluator>();
        Planner = requestScope.ServiceProvider.GetRequiredService<IAccessQueryPlanner>();
        Admin = requestScope.ServiceProvider.GetRequiredService<IAccessAdministrator>();
        Audit = provider.GetRequiredService<InMemoryAccessAuditSink>();
        Grants = provider.GetRequiredService<IAccessGrantStore>();
        Roles = provider.GetRequiredService<IAccessRoleStore>();
        Policies = provider.GetRequiredService<IAccessFieldPolicyStore>();
        Catalog = provider.GetRequiredService<IPermissionCatalog>();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public IAccessEvaluator Evaluator { get; }
    public IAccessQueryPlanner Planner { get; }
    public IAccessAdministrator Admin { get; }
    public InMemoryAccessAuditSink Audit { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public IAccessFieldPolicyStore Policies { get; }
    public IPermissionCatalog Catalog { get; }
    public TicketScope Scope { get; } = TestScopes.Create();
    public AccessPartition Partition => AccessPartition.From(Scope);
    public WorkspaceGrantScope Workspace => new(Scope.WorkspaceId);

    public static AccessFixture Create(Action<IServiceCollection>? configure = null, Action<AccessControlOptions>? options = null, int auditCapacity = 10_000)
    {
        var clock = TestClock.Create();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new InMemoryAccessAuditSink(auditCapacity));
        configure?.Invoke(services);
        services.AddTicketFlow()
            .AddAccessControl(options)
            .AddAccessPermissions(new PermissionDefinition("macro:run"))
            .AddInMemoryAccessStores();
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock);
    }

    public UserPrincipal User(string id = "ada", params string[] teams) => TestPrincipals.User(Scope, id, teams);

    public ApiClientPrincipal Client(string id = "crm") => TestPrincipals.ApiClient(Scope, id);

    public AccessResource Resource(
        string? team = null,
        string? inbox = null,
        string? assignee = null,
        string[]? labels = null,
        Dictionary<string, string>? attributes = null,
        TicketScope? scope = null,
        string? id = null) =>
        new(scope ?? Scope, id, team is null ? null : new TeamId(team), inbox is null ? null : new InboxId(inbox),
            assignee is null ? null : new UserId(assignee), labels?.Select(label => new LabelId(label)), attributes);

    public async Task<AccessGrant> GrantAsync(
        GrantSubject subject,
        AccessRole role,
        GrantScope? scope = null,
        GrantEffect effect = GrantEffect.Allow,
        AccessCondition? condition = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        AccessPartition? partition = null)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), partition ?? Partition, subject, role.Id, scope ?? Workspace, effect, condition, validFrom, validUntil);
        return await SeedAsync(grant);
    }

    public async Task<AccessGrant> SeedAsync(AccessGrant grant)
    {
        var result = await Grants.CreateAsync(grant);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>(result.Value);
        return grant;
    }

    public async Task<AccessRole> CustomRoleAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId(id), id, permissions);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        return role;
    }

    public Task<AccessDecision> AuthorizeAsync(PrincipalRef principal, Permission permission, AccessResource? resource = null) =>
        Evaluator.AuthorizeAsync(new AccessRequest(principal, permission, resource ?? Resource()));

    public async Task<string> DescribeAsync(PrincipalRef principal, Permission permission, AccessResource? resource = null) =>
        Describe(await AuthorizeAsync(principal, permission, resource));

    public static string Describe(AccessDecision decision) => decision switch
    {
        AccessAllowed allowed => allowed.UsedBreakGlass ? "allowed:break-glass" : "allowed",
        AccessDenied denied => $"denied:{denied.Reason}",
        AccessMasked masked => "masked:" + string.Join(",", masked.Fields.Select(field => field.Field + (field.Revealable ? "+" : ""))),
    };

    public static string Describe<T>(TicketResult<AccessChangeApplied<T>> result) => result switch
    {
        TicketValue<AccessChangeApplied<T>> => "applied",
        TicketFailure refused => $"refused:{refused.Kind}" + (refused.CurrentRevision is { } revision ? $":{revision}" : ""),
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }
}
