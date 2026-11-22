using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Contacts.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresContactLinkingTests
{
    [TestMethod]
    public async Task LinkingPolicyContractUsesIndependentClients()
    {
        await using var database = await PostgresContactStoreTests.Database.CreateAsync();
        await ContactLinkingStoreContract.VerifyPolicyAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task LinkSuggestionContractUsesIndependentClients()
    {
        await using var database = await PostgresContactStoreTests.Database.CreateAsync();
        await ContactLinkingStoreContract.VerifySuggestionsAsync(database.Store, database.Store, database.Competing);
    }

    [TestMethod]
    public async Task LinkingAuditKindsAndSuggestionIdsPersist()
    {
        await using var database = await PostgresContactStoreTests.Database.CreateAsync();
        var suggestion = Guid.CreateVersion7();
        await database.Store.RecordAsync(new ContactAuditEvent(Guid.NewGuid(), ContactStoreContract.At, TestScopes.Create(), ContactAuditKind.LinkSuggestionRejected,
            new AccessActor(AccessActorKind.User, "sam"), new ContactId("a"), new ContactId("b")) { Suggestion = suggestion });
        Assert.AreEqual((short)10, await database.ScalarAsync("SELECT kind FROM tf_contact_audit"));
        Assert.AreEqual(suggestion, await database.ScalarAsync("SELECT suggestion_id FROM tf_contact_audit"));
    }

    [TestMethod]
    public async Task ConcurrentLinksConvergeOnPostgres()
    {
        await using var database = await PostgresContactStoreTests.Database.CreateAsync();
        var access = new PostgresAccessStore(database.Primary);
        await access.InitializeAsync();
        var scope = TestScopes.Create();
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await access.CreateAsync(new AccessGrant(new GrantId("owner"), AccessPartition.From(scope),
            new UserId("owner"), BuiltInRoles.Owner.Id, new WorkspaceGrantScope(scope.WorkspaceId)))).Value);
        var owner = TestPrincipals.User(scope, "owner");
        var providers = new List<ServiceProvider>();
        try
        {
            var services = new List<IContactLinkingService>();
            foreach (var source in new[] { database.Primary, database.Secondary })
                for (var copy = 0; copy < 2; copy++)
                {
                    var collection = new ServiceCollection();
                    collection.AddTicketFlow().AddAccessControl().AddPostgresAccessStores(source).AddContacts().AddPostgresContactStores(source);
                    var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                    providers.Add(provider);
                    services.Add(provider.CreateScope().ServiceProvider.GetRequiredService<IContactLinkingService>());
                }
            for (var round = 0; round < 5; round++)
            {
                var account = new ExternalAccountRef("acme-portal", $"race-{round}");
                var visitors = Enumerable.Range(0, services.Count).Select(index => ContactIdentityAddress.Parse(ContactIdentityKind.WidgetVisitor, $"v_{round}_{index}")).ToList();
                foreach (var (visitor, index) in visitors.Select((visitor, index) => (visitor, index)))
                    Assert.IsTrue(ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(database.Store, scope,
                        ContactStoreContract.NewContact($"r{round}-{index}", identities: [ContactStoreContract.Claimed(visitor)]))).StartsWith("applied", StringComparison.Ordinal));
                var results = await Task.WhenAll(services.Select((service, index) =>
                    service.LinkAccountAsync(owner, new ContactLinkRequest(account, "acme-login", evidence: visitors[index]))));
                Assert.AreEqual(1, results.Count(result => result.Value is ContactLinked), $"round {round}");
                Assert.AreEqual(services.Count - 1, results.Count(result => result.Value is ContactMergedInto), $"round {round}");
                var winner = (await database.Store.FindByIdentityAsync(scope, account.Address)).Single().Contact;
                Assert.HasCount(services.Count + 1, (await database.Store.GetAsync(scope, winner))!.Contact.Identities, $"round {round}");

                // Concurrent logins of a new account create exactly one contact.
                var login = new ExternalAccountRef("acme-portal", $"login-{round}");
                var resolved = await Task.WhenAll(services.Select(service => service.ResolveCustomerAsync(owner, new CustomerResolveRequest(login, "acme-login"))));
                Assert.HasCount(1, resolved.Select(result => ((CustomerResolved)result.Value!).Contact.Id).Distinct(), $"round {round}");
            }
        }
        finally
        {
            foreach (var provider in providers)
                await provider.DisposeAsync();
        }
    }
}
