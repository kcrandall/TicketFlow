using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Contacts.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresContactStoreTests
{
    internal sealed class Database : IAsyncDisposable
    {
        private readonly PostgresTestDatabase storage;

        private Database(PostgresTestDatabase storage)
        {
            this.storage = storage;
            Store = new(storage.First);
            Competing = new(storage.Second);
        }

        public PostgresContactStore Store { get; }
        public PostgresContactStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;
        public NpgsqlDataSource Secondary => storage.Second;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_contacts_test_", async storage =>
        {
            var database = new Database(storage);
            await database.Store.InitializeAsync();
            return database;
        });

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var command = Primary.CreateCommand(sql);
            return await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task ContactContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifyContactsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task MergeRecordContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifyMergeRecordsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task SearchContract()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifySearchAsync(database.Store);
    }

    [TestMethod]
    public async Task ChangeOutboxIsGaplessAcrossClients()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifyChangesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentVerifiedIdentityClaimsAdmitOneWinner()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifyConcurrentVerifiedIdentityAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task DefinitionContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifyDefinitionsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndVersioned()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Primary, PostgresContactMigrations.Catalog).InspectAsync();
        Assert.AreEqual(3, inspection.CurrentVersion);
        Assert.AreEqual("ticketflow.contacts", inspection.Component);
    }

    [TestMethod]
    public async Task LongIdentifiersAndValuesUseBoundedKeys()
    {
        await using var database = await Database.CreateAsync();
        var scope = new TicketScope(TestScopes.LongIdentifier(1), TestScopes.LongIdentifier(2), TestScopes.LongIdentifier(3), TestScopes.LongIdentifier(4),
            new WorkspaceId(TestScopes.LongIdentifier(5)));
        var visitor = ContactIdentityAddress.Parse(ContactIdentityKind.WidgetVisitor, TestScopes.LongIdentifier(6), "widget");
        var contact = ContactStoreContract.NewContact(TestScopes.LongIdentifier(7), TestScopes.LongIdentifier(8), [ContactStoreContract.Verified(visitor)]);
        Assert.AreEqual("applied:1", ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(database.Store, scope, contact)));
        Assert.AreEqual(contact, (await database.Competing.GetAsync(scope, contact.Id))!.Contact);
        Assert.AreEqual(contact.Id, (await database.Competing.FindByIdentityAsync(scope, visitor)).Single().Contact);
        Assert.HasCount(1, (await database.Competing.SearchAsync(scope, ContactSearch.ByName(TestScopes.LongIdentifier(8)[..10]), AllowAllFilter.Instance)).Contacts);
    }

    [TestMethod]
    public async Task BatchAuditIsCommittedWithTheWriteAndSinkPersists()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        var actor = new AccessActor(AccessActorKind.User, "sam");
        var audit = new ContactAuditEvent(Guid.NewGuid(), ContactStoreContract.At, scope, ContactAuditKind.Deleted, actor, new ContactId("a"), justification: "erasure");
        Assert.AreEqual("applied:1", ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(database.Store, scope, ContactStoreContract.NewContact("a"))));
        var failed = await database.Store.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(new ContactId("a"), 7)], audit: audit));
        Assert.AreEqual("conflict:a:1", ContactStoreContract.Describe(failed));
        Assert.AreEqual(0L, await database.ScalarAsync("SELECT count(*) FROM tf_contact_audit"));
        Assert.AreEqual("applied:", ContactStoreContract.Describe(await database.Store.WriteAsync(scope, new ContactWriteBatch(deletes: [new ContactDelete(new ContactId("a"), 1)], audit: audit))));
        Assert.AreEqual("erasure", await database.ScalarAsync($"SELECT justification FROM tf_contact_audit WHERE event_id='{audit.Id}'"));
        await database.Competing.RecordAsync(new ContactAuditEvent(Guid.NewGuid(), ContactStoreContract.At, scope, ContactAuditKind.Exported, actor, count: 10));
        Assert.AreEqual(2L, await database.ScalarAsync("SELECT count(*) FROM tf_contact_audit"));
        Assert.AreEqual(0L, await database.ScalarAsync("SELECT count(*) FROM tf_contact_identities"));
    }

    [TestMethod]
    public async Task UnknownFormatVersionsFailToDecode()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        Assert.AreEqual("applied:1", ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(database.Store, scope, ContactStoreContract.NewContact("a"))));
        await database.ScalarAsync("UPDATE tf_contacts SET attributes='{\"v\":9,\"a\":{}}'::jsonb");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, new ContactId("a")));
    }

    [TestMethod]
    public async Task GeneratedStatementsUseIndexes()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        for (var index = 0; index < 200; index++)
        {
            var identities = new List<ContactIdentity> { ContactStoreContract.Claimed(ContactStoreContract.Email($"user{index:D3}@example.com")) };
            StringAssert.StartsWith(ContactStoreContract.Describe(await ContactStoreContract.CreateAsync(database.Store, index % 2 == 0 ? scope : TestScopes.Create(workspaceId: "noise"),
                ContactStoreContract.NewContact($"c{index:D3}", $"Name {index:D3}", identities, index % 10 == 0 ? ["vip"] : null))), "applied:");
        }
        await database.ScalarAsync("ANALYZE");
        await using var connection = await database.Primary.OpenConnectionAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await off.ExecuteNonQueryAsync();

        async Task<string> ExplainAsync(NpgsqlCommand command)
        {
            command.Connection = connection;
            command.CommandText = "EXPLAIN " + command.CommandText;
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(0));
            return string.Join("\n", lines);
        }

        var cursor = new ContactSearchCursor("name 050", new ContactId("c050"));
        var filter = new OrFilter(new HasLabelFilter(new LabelId("vip")), new NotFilter(new ScopeFilter(TestScopes.Create(workspaceId: "noise"))));
        var namePlan = await ExplainAsync(PostgresContactStore.SearchCommand(scope, ContactSearch.ByName("name", 20).WithAfter(cursor), filter, 1000));
        StringAssert.Contains(namePlan, "tf_contacts_name");
        StringAssert.Contains(namePlan, "Limit");
        var identityPlan = await ExplainAsync(PostgresContactStore.SearchCommand(scope,
            ContactSearch.ByIdentity(ContactIdentityKind.Email, "user", limit: 20).WithAfter(new ContactSearchCursor("user050@example.com", new ContactId("c050"))), filter, 1000));
        StringAssert.Contains(identityPlan, "tf_contact_identities_search");
        var lookupPlan = await ExplainAsync(PostgresContactStore.FindByIdentityCommand(scope, ContactStoreContract.Email("user010@example.com")));
        StringAssert.Contains(lookupPlan, "tf_contact_identities_");
        Assert.DoesNotContain("Seq Scan", lookupPlan);
        var changesPlan = await ExplainAsync(PostgresContactStore.ReadChangesCommand(scope, 50, 100));
        StringAssert.Contains(changesPlan, "tf_contact_changes_pkey");
        Assert.DoesNotContain("Seq Scan", namePlan + identityPlan + changesPlan);
    }

    [TestMethod]
    public async Task DeleteCascadeContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifyDeleteCascadeAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task BoundedScanContract()
    {
        await using var database = await Database.CreateAsync();
        await ContactStoreContract.VerifyBoundedScanAsync(new PostgresContactStore(database.Primary, 3));
    }

    [TestMethod]
    public async Task ExportCompletedAuditKindIsStored()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.RecordAsync(new ContactAuditEvent(Guid.NewGuid(), ContactStoreContract.At, TestScopes.Create(), ContactAuditKind.ExportCompleted,
            new AccessActor(AccessActorKind.User, "sam"), count: 3));
        Assert.AreEqual((short)5, await database.ScalarAsync("SELECT kind FROM tf_contact_audit"));
        Assert.AreEqual(3, (await new PostgresMigrationRunner(database.Primary, PostgresContactMigrations.Catalog).InspectAsync()).CurrentVersion);
    }

    [TestMethod]
    public async Task ConcurrentResolutionsRetryAndConvergeOnPostgres()
    {
        await using var database = await Database.CreateAsync();
        var access = new PostgresAccessStore(database.Primary);
        await access.InitializeAsync();
        var scope = TestScopes.Create();
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await access.CreateAsync(new AccessGrant(new GrantId("owner"), AccessPartition.From(scope),
            new UserId("owner"), BuiltInRoles.Owner.Id, new WorkspaceGrantScope(scope.WorkspaceId)))).Value);
        var owner = TestPrincipals.User(scope, "owner");
        var providers = new List<ServiceProvider>();
        try
        {
            // Independent service providers over independent data sources race on the same verified identity.
            var services = new List<IContactService>();
            foreach (var source in new[] { database.Primary, database.Secondary })
                for (var copy = 0; copy < 3; copy++)
                {
                    var collection = new ServiceCollection();
                    collection.AddTicketFlow().AddAccessControl().AddPostgresAccessStores(source).AddContacts().AddPostgresContactStores(source);
                    var provider = collection.BuildServiceProvider();
                    providers.Add(provider);
                    services.Add(provider.CreateScope().ServiceProvider.GetRequiredService<IContactService>());
                }
            for (var round = 0; round < 5; round++)
            {
                var evidence = new ContactIdentityEvidence(ContactStoreContract.Email($"race{round}@example.com"), new IdentityVerified("dkim", ContactStoreContract.At));
                var results = await Task.WhenAll(services.Select(service => service.ResolveAsync(owner, new ContactResolveRequest([evidence]))));
                var ids = results.Select(result => result.Value switch
                {
                    ContactCreated created => created.Contact.Id,
                    ContactMatched matched => matched.Contact.Id,
                    _ => throw new AssertFailedException($"round {round}: unexpected resolution"),
                }).Distinct().ToList();
                Assert.HasCount(1, ids, $"round {round}");
                Assert.AreEqual(1, results.Count(result => result.Value is ContactCreated), $"round {round}");
            }
        }
        finally
        {
            foreach (var provider in providers)
                await provider.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ServiceRunsEndToEndOnPostgres()
    {
        await using var database = await Database.CreateAsync();
        var access = new PostgresAccessStore(database.Primary);
        await access.InitializeAsync();
        var services = new ServiceCollection();
        services.AddTicketFlow().AddAccessControl().AddPostgresAccessStores(database.Primary).AddContacts().AddPostgresContactStores(database.Primary);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var scope = TestScopes.Create();
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await access.CreateAsync(new AccessGrant(new GrantId("owner"), AccessPartition.From(scope),
            new UserId("owner"), BuiltInRoles.Owner.Id, new WorkspaceGrantScope(scope.WorkspaceId)))).Value);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await access.CreateAsync(new AccessGrant(new GrantId("agent"), AccessPartition.From(scope),
            new UserId("agent"), BuiltInRoles.Agent.Id, new WorkspaceGrantScope(scope.WorkspaceId), condition: new HasLabelCondition(new LabelId("vip"))))).Value);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessFieldPolicy>>((await access.CreateAsync(scope,
            new AccessFieldPolicy(ContactFieldPaths.Email, [ContactPermissions.Read], [], ContactPermissions.Reveal))).Value);
        using var request = provider.CreateScope();
        var contacts = request.ServiceProvider.GetRequiredService<IContactService>();
        var owner = TestPrincipals.User(scope, "owner");
        var agent = TestPrincipals.User(scope, "agent");

        var email = ContactIdentityAddress.Parse(ContactIdentityKind.Email, "ada@example.com");
        var created = (ContactCreated)(await contacts.ResolveAsync(owner, new ContactResolveRequest(
            [new ContactIdentityEvidence(email, new IdentityVerified("dkim", ContactStoreContract.At))], new ContactProfile("Ada")))).Value!;
        var other = (ContactApplied<ContactView>)(await contacts.CreateAsync(owner, new ContactDraft(new ContactProfile("Ada Byron"), Labels: [new LabelId("vip")]))).Value!;
        var page = (ContactSearchPage)(await contacts.SearchAsync(agent, ContactSearch.ByName("ada"))).Value!;
        Assert.AreEqual(other.Value.Id, page.Contacts.Single().Id);
        Assert.IsTrue(((ContactSearchPage)(await contacts.SearchAsync(owner, ContactSearch.ByName("ada"))).Value!).Contacts.Count == 2);

        var merged = (ContactApplied<ContactMergeOutcome>)(await contacts.MergeAsync(owner, new ContactMergeRequest(other.Value.Id, 1, created.Contact.Id, 1))).Value!;
        Assert.IsTrue(merged.Value.Target.Identities.Single().Masked);
        var revealed = (ContactFieldRevealed)(await contacts.RevealAsync(owner, other.Value.Id, ContactFieldPaths.Email, "verify")).Value!;
        Assert.AreEqual(email, revealed.Identities.Single());
        var unmerged = (ContactApplied<ContactUnmergeOutcome>)(await contacts.UnmergeAsync(owner, merged.Value.MergeId)).Value!;
        Assert.IsEmpty(unmerged.Value.Target.Identities);
        Assert.AreEqual(created.Contact.Id, unmerged.Value.Source.Id);
        var changes = await database.Store.ReadChangesAsync(scope, 0, 100);
        CollectionAssert.AreEqual(new[] { ContactChangeKind.Created, ContactChangeKind.Created, ContactChangeKind.Merged, ContactChangeKind.Updated, ContactChangeKind.Unmerged, ContactChangeKind.Updated },
            changes.Select(change => change.Event.Kind).ToArray());
        Assert.AreEqual(2L, await database.ScalarAsync("SELECT count(*) FROM tf_contact_audit"));
    }
}
