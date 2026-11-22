using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Workspaces.Tests;

/// <summary>In-memory access engine plus workspace services with a fake clock. Grants seeded through <see cref="GrantAsync"/> bypass the administrator.</summary>
internal sealed class WorkspaceFixture : IDisposable
{
    private readonly IServiceScope requestScope;
    private int nextGrant;

    private WorkspaceFixture(ServiceProvider provider, FakeTimeProvider clock)
    {
        Provider = provider;
        Clock = clock;
        requestScope = provider.CreateScope();
        var services = requestScope.ServiceProvider;
        Settings = services.GetRequiredService<IWorkspaceSettingsService>();
        Inboxes = services.GetRequiredService<IInboxService>();
        Teams = services.GetRequiredService<ITeamService>();
        Calendars = services.GetRequiredService<IBusinessCalendarService>();
        Resolver = services.GetRequiredService<ITeamMembershipResolver>();
        Evaluator = services.GetRequiredService<IAccessEvaluator>();
        Grants = services.GetRequiredService<IAccessGrantStore>();
        Roles = services.GetRequiredService<IAccessRoleStore>();
        Audit = services.GetRequiredService<InMemoryAccessAuditSink>();
        InboxStore = services.GetRequiredService<IInboxStore>();
        TeamStore = services.GetRequiredService<ITeamStore>();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public IWorkspaceSettingsService Settings { get; }
    public IInboxService Inboxes { get; }
    public ITeamService Teams { get; }
    public IBusinessCalendarService Calendars { get; }
    public ITeamMembershipResolver Resolver { get; }
    public IAccessEvaluator Evaluator { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public InMemoryAccessAuditSink Audit { get; }
    public IInboxStore InboxStore { get; }
    public ITeamStore TeamStore { get; }
    public TicketScope Scope { get; } = TestScopes.Create();
    public AccessPartition Partition => AccessPartition.From(Scope);
    public WorkspaceGrantScope Workspace => new(Scope.WorkspaceId);

    public static WorkspaceFixture Create()
    {
        var clock = TestClock.Create();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddWorkspaces()
            .AddInMemoryWorkspaceStores();
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock);
    }

    public UserPrincipal User(string id = "ada", params string[] teams) => TestPrincipals.User(Scope, id, teams);

    public async Task<AccessGrant> GrantAsync(GrantSubject subject, AccessRole role, GrantScope? scope = null, GrantEffect effect = GrantEffect.Allow, AccessCondition? condition = null)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), Partition, subject, role.Id, scope ?? Workspace, effect, condition);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await Grants.CreateAsync(grant)).Value);
        return grant;
    }

    public async Task<AccessRole> CustomRoleAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId(id), id, permissions);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        return role;
    }

    public InboxGrantScope InboxScope(string inbox) => new(Scope.WorkspaceId, new InboxId(inbox));

    public TeamGrantScope TeamScope(string team) => new(Scope.WorkspaceId, new TeamId(team));

    public static string Describe<T>(WorkspaceResult<T> result) => result switch
    {
        WorkspaceSucceeded<T> => "ok",
        WorkspaceRefused refused => $"refused:{refused.Reason}" + (refused.CurrentRevision is { } revision ? $":{revision}" : ""),
    };

    public static T Value<T>(WorkspaceResult<T> result) => result switch
    {
        WorkspaceSucceeded<T> succeeded => succeeded.Value,
        WorkspaceRefused refused => throw new AssertFailedException($"Expected success but was refused: {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }
}
