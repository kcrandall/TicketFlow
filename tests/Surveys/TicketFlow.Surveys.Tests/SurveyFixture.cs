using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Surveys.Tests;

/// <summary>
/// Records sent messages (thread-safe). Throws without delivering while <see cref="Failures"/> is positive, and records then throws
/// (provider accepted, sender failed) while <see cref="AcceptThenFail"/> is positive.
/// </summary>
internal sealed class RecordingSender : ISurveyInvitationSender
{
    private readonly Lock gate = new();

    public List<SurveyInvitationMessage> Sent { get; } = [];
    public int Failures { get; set; }
    public int AcceptThenFail { get; set; }
    public Action<SurveyInvitationMessage>? OnSend { get; set; }

    public Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default)
    {
        if (OnSend is { } custom)
        {
            custom(message);
            return Task.CompletedTask;
        }
        lock (gate)
        {
            if (Failures > 0)
            {
                Failures--;
                throw new InvalidOperationException("transport down");
            }
            Sent.Add(message);
            if (AcceptThenFail > 0)
            {
                AcceptThenFail--;
                throw new TimeoutException("provider accepted, response lost");
            }
        }
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
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        var user = TestPrincipals.User(Scope, id);
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), user.UserId, role.Id, scope ?? new WorkspaceGrantScope(Scope.WorkspaceId));
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await Grants.CreateAsync(grant)).Value);
        return user;
    }

    public Task<UserPrincipal> ManagerAsync(string id = "manager") =>
        UserWithAsync(id, null, SurveyPermissions.Read, SurveyPermissions.Manage, SurveyPermissions.ReportView);

    public ConversationResolvedNotification Resolved(string conversation = "c1", string contact = "k1", string? inbox = "i0", string? team = "t0", string? agent = "u0", string? locale = null) =>
        new(Scope, new ConversationId(conversation), new ContactId(contact), Clock.GetUtcNow(), inbox is null ? null : new InboxId(inbox), team is null ? null : new TeamId(team),
            agent is null ? null : new UserId(agent), locale is null ? null : new SurveyLocale(locale));

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
