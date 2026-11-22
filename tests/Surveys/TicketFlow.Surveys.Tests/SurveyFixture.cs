using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Surveys.Tests;

/// <summary>Records sent messages; throws while <see cref="Failures"/> is positive.</summary>
internal sealed class RecordingSender : ISurveyInvitationSender
{
    public List<SurveyInvitationMessage> Sent { get; } = [];
    public int Failures { get; set; }

    public Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default)
    {
        if (Failures > 0)
        {
            Failures--;
            throw new InvalidOperationException("transport down");
        }
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class SurveyFixture : IDisposable
{
    private readonly IServiceScope requestScope;
    private int nextGrant;

    private SurveyFixture(ServiceProvider provider, FakeTimeProvider clock, RecordingSender sender)
    {
        Provider = provider;
        Clock = clock;
        Sender = sender;
        requestScope = provider.CreateScope();
        var services = requestScope.ServiceProvider;
        Service = services.GetRequiredService<ISurveyService>();
        Scheduler = services.GetRequiredService<ISurveyScheduler>();
        Dispatcher = services.GetRequiredService<ISurveyDispatcher>();
        Responder = services.GetRequiredService<ISurveyResponder>();
        Store = services.GetRequiredService<ISurveyStore>();
        Grants = services.GetRequiredService<IAccessGrantStore>();
        Roles = services.GetRequiredService<IAccessRoleStore>();
        Policies = services.GetRequiredService<IAccessFieldPolicyStore>();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public RecordingSender Sender { get; }
    public ISurveyService Service { get; }
    public ISurveyScheduler Scheduler { get; }
    public ISurveyDispatcher Dispatcher { get; }
    public ISurveyResponder Responder { get; }
    public ISurveyStore Store { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public IAccessFieldPolicyStore Policies { get; }
    public TicketScope Scope { get; } = TestScopes.Create();

    public static SurveyFixture Create(Action<SurveyOptions>? configure = null)
    {
        var clock = TestClock.Create();
        var sender = new RecordingSender();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ISurveyInvitationSender>(sender);
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddSurveys(configure).AddInMemorySurveyStores();
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock, sender);
    }

    public async Task<UserPrincipal> UserWithAsync(string id, GrantScope? scope, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId("role-" + id), id, permissions);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        var user = TestPrincipals.User(Scope, id);
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), user.UserId, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId));
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await Grants.CreateAsync(grant)).Value);
        return user;
    }

    public Task<UserPrincipal> ManagerAsync(string id = "manager") =>
        UserWithAsync(id, null, SurveyPermissions.Read, SurveyPermissions.Manage, SurveyPermissions.ReportView);

    public ConversationResolvedNotification Resolved(string conversation = "c1", string contact = "k1", string? inbox = "i0", string? team = "t0", string? agent = "u0", string? locale = null) =>
        new(Scope, new ConversationId(conversation), new ContactId(contact), Clock.GetUtcNow(), inbox is null ? null : new InboxId(inbox), team is null ? null : new TeamId(team),
            agent is null ? null : new UserId(agent), locale is null ? null : new SurveyLocale(locale));

    public static T Value<T>(SurveyResult<T> result) => result switch
    {
        SurveySucceeded<T> succeeded => succeeded.Value,
        SurveyRefused refused => throw new AssertFailedException($"Expected success but was refused: {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public static string Describe<T>(SurveyResult<T> result) => result switch
    {
        SurveySucceeded<T> => "ok",
        SurveyRefused refused => $"refused:{refused.Reason}" + (refused.CurrentRevision is { } revision ? $":{revision}" : ""),
    };

    public static string Describe(SurveyResponseResult result) => result switch
    {
        SurveyResponseAccepted => "accepted",
        SurveyResponseDuplicate => "duplicate",
        SurveyResponseRejected rejected => $"rejected:{rejected.Reason}",
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }
}
