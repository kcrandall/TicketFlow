using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.Tests;

/// <summary>Records observer notifications.</summary>
internal sealed class RecordingObserver : IContactChangeObserver
{
    private readonly List<ContactChange> changes = [];

    public IReadOnlyList<ContactChange> Changes => changes;

    public Task OnChangedAsync(IReadOnlyList<ContactChange> batch, CancellationToken cancellationToken = default)
    {
        changes.AddRange(batch);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory access engine plus the contact service. Seeded principals: <c>owner</c> (Owner), <c>agent</c> (Agent),
/// <c>supervisor</c> (Supervisor), API client <c>crm</c> (custom role with read+create). Grants bypass the administrator.
/// </summary>
internal sealed class ContactFixture : IDisposable
{
    private readonly IServiceScope requestScope;
    private int nextGrant;

    private ContactFixture(ServiceProvider provider, FakeTimeProvider clock, RecordingObserver observer)
    {
        Provider = provider;
        Clock = clock;
        Observer = observer;
        requestScope = provider.CreateScope();
        Contacts = requestScope.ServiceProvider.GetRequiredService<IContactService>();
        Definitions = requestScope.ServiceProvider.GetRequiredService<IContactAttributeDefinitionService>();
        Store = provider.GetRequiredService<InMemoryContactStore>();
        AccessAudit = provider.GetRequiredService<InMemoryAccessAuditSink>();
        Grants = provider.GetRequiredService<IAccessGrantStore>();
        Roles = provider.GetRequiredService<IAccessRoleStore>();
        Policies = provider.GetRequiredService<IAccessFieldPolicyStore>();
        Catalog = provider.GetRequiredService<IPermissionCatalog>();
    }

    public ServiceProvider Provider { get; }
    public FakeTimeProvider Clock { get; }
    public RecordingObserver Observer { get; }
    public IContactService Contacts { get; }
    public IContactAttributeDefinitionService Definitions { get; }
    public InMemoryContactStore Store { get; }
    public InMemoryAccessAuditSink AccessAudit { get; }
    public IAccessGrantStore Grants { get; }
    public IAccessRoleStore Roles { get; }
    public IAccessFieldPolicyStore Policies { get; }
    public IPermissionCatalog Catalog { get; }
    public TicketScope Scope { get; } = TestScopes.Create();

    public UserPrincipal Owner => TestPrincipals.User(Scope, "owner");
    public UserPrincipal Agent => TestPrincipals.User(Scope, "agent");
    public UserPrincipal Supervisor => TestPrincipals.User(Scope, "supervisor");
    public ApiClientPrincipal Crm => TestPrincipals.ApiClient(Scope, "crm");

    public static async Task<ContactFixture> CreateAsync(Action<ContactsOptions>? options = null)
    {
        var clock = TestClock.Create();
        var observer = new RecordingObserver();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IContactChangeObserver>(observer);
        services.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddContacts(options)
            .AddContacts()
            .AddInMemoryContactStores();
        var fixture = new ContactFixture(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }), clock, observer);
        await fixture.GrantAsync(new UserId("owner"), BuiltInRoles.Owner);
        await fixture.GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("supervisor"), BuiltInRoles.Supervisor);
        var ingest = await fixture.CustomRoleAsync("ingest", ContactPermissions.Read, ContactPermissions.Create);
        await fixture.GrantAsync(new ApiClientId("crm"), ingest);
        return fixture;
    }

    public async Task GrantAsync(GrantSubject subject, AccessRole role, GrantEffect effect = GrantEffect.Allow, AccessCondition? condition = null)
    {
        var grant = new AccessGrant(new GrantId($"seed-{nextGrant++:D4}"), AccessPartition.From(Scope), subject, role.Id, new WorkspaceGrantScope(Scope.WorkspaceId), effect, condition);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await Grants.CreateAsync(grant)).Value);
    }

    public async Task<AccessRole> CustomRoleAsync(string id, params Permission[] permissions)
    {
        var role = new AccessRole(new RoleId(id), id, permissions);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessRole>>((await Roles.CreateAsync(Scope, role)).Value);
        return role;
    }

    /// <summary>Masks email and phone for read and export unless the grant uses Supervisor; revealable with contact:reveal.</summary>
    public async Task MaskIdentitiesAsync()
    {
        foreach (var field in new[] { ContactFieldPaths.Email, ContactFieldPaths.Phone })
            await PolicyAsync(new AccessFieldPolicy(field, [ContactPermissions.Read, ContactPermissions.Export], [BuiltInRoles.Supervisor.Id], ContactPermissions.Reveal));
    }

    public async Task PolicyAsync(AccessFieldPolicy policy) =>
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessFieldPolicy>>((await Policies.CreateAsync(Scope, policy)).Value);

    public async Task DefineAsync(ContactAttributeDefinition definition) =>
        Assert.IsInstanceOfType<ContactApplied<StoredContactAttributeDefinition>>((await Definitions.CreateAsync(Owner, definition)).Value);

    public static ContactIdentityAddress Email(string value) => ContactIdentityAddress.Parse(ContactIdentityKind.Email, value);

    public static ContactIdentityAddress Phone(string value) => ContactIdentityAddress.Parse(ContactIdentityKind.Phone, value);

    public ContactIdentity Verified(ContactIdentityAddress address) => new(address, new IdentityVerified("dkim", Clock.GetUtcNow()));

    public ContactIdentity Claimed(ContactIdentityAddress address) => new(address, new IdentityClaimed("form", Clock.GetUtcNow()));

    public async Task<ContactView> CreateAsync(string id, string? name = null, IReadOnlyList<ContactIdentity>? identities = null, string[]? labels = null,
        Dictionary<string, ContactAttributeValue>? attributes = null)
    {
        var result = await Contacts.CreateAsync(Owner, new ContactDraft(new ContactProfile(name), identities, attributes,
            labels?.Select(label => new LabelId(label)).ToList(), Id: new ContactId(id)));
        return Applied(result);
    }

    public static T Applied<T>(ContactResult<T> result) => result switch
    {
        ContactApplied<T> applied => applied.Value,
        ContactRefused refused => throw new AssertFailedException($"Expected applied but was refused: {refused.Reason} {string.Join("; ", refused.Errors)}"),
    };

    public static string Describe<T>(ContactResult<T> result) => result switch
    {
        ContactApplied<T> => "applied",
        ContactRefused refused => $"refused:{refused.Reason}" + (refused.CurrentRevision is { } revision ? $":{revision}" : ""),
    };

    public static string Describe(ContactResolution resolution) => resolution switch
    {
        ContactMatched matched => $"matched:{matched.Strength}:{matched.Contact.Id}",
        ContactCreated created => "created",
        ContactAmbiguous ambiguous => "ambiguous:" + string.Join(",", ambiguous.Candidates),
        ContactRefused refused => $"refused:{refused.Reason}",
    };

    public void Dispose()
    {
        requestScope.Dispose();
        Provider.Dispose();
    }
}
