using Npgsql;
using TicketFlow.Access.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Access.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresAccessStoreTests
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

        public PostgresAccessStore Store { get; }
        public PostgresAccessStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;
        public NpgsqlDataSource Secondary => storage.Second;
        public string Schema => storage.Schema;

        public static Task<Database> CreateAsync(bool initialize = true) => PostgresTestDatabase.CreateAsync("tf_access_test_", async storage =>
        {
            var database = new Database(storage);
            if (initialize)
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
    public async Task RoleContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AccessStoreContract.VerifyRolesAsync(database.Store, database.Competing);
        await AccessStoreContract.VerifyRoleCapacityAsync(database.Store);
    }

    [TestMethod]
    public async Task GrantContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AccessStoreContract.VerifyGrantsAsync(database.Store, database.Competing);
        await AccessStoreContract.VerifyGrantTargetsAsync(database.Store, database.Competing);
        await AccessStoreContract.VerifyGrantCapacityAsync(database.Store);
    }

    [TestMethod]
    public async Task FieldPolicyContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await AccessStoreContract.VerifyFieldPoliciesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task InitializationIsExplicitConcurrentAndChecksumVerified()
    {
        await using var database = await Database.CreateAsync(initialize: false);
        var scope = TestScopes.Create();
        await Assert.ThrowsExactlyAsync<PostgresException>(() => database.Store.GetAsync(scope, new RoleId("triage")));
        await Task.WhenAll(database.Store.InitializeAsync(), database.Competing.InitializeAsync(), database.Store.InitializeAsync());
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Primary, PostgresAccessMigrations.Catalog).InspectAsync();
        Assert.AreEqual(PostgresAccessMigrations.Catalog.Migrations.Count, inspection.CurrentVersion);
        Assert.AreEqual(PostgresAccessMigrations.Catalog.Migrations[0].Checksum, inspection.Applied[0].Checksum);
        Assert.IsNull(await database.Store.GetAsync(scope, new RoleId("triage")));

        // Only bounded hash-key indexes exist (no raw composite text keys).
        Assert.AreEqual("tf_access_audit_scope,tf_access_field_policies_scope,tf_access_grants_subject,tf_access_grants_target,tf_access_roles_scope",
            await database.ScalarAsync($"SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname='{database.Schema}' AND indexname LIKE 'tf_access_%' AND indexname NOT LIKE '%pkey'"));
        Assert.AreEqual(0L, await database.ScalarAsync($"""
            SELECT count(*) FROM pg_index i JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(i.indkey)
            WHERE n.nspname = '{database.Schema}' AND t.relname LIKE 'tf_access_%' AND a.atttypid = 'text'::regtype
            """));

        var changed = new PostgresMigrationCatalog(PostgresAccessMigrations.Catalog.Component,
            [new(1, "initial-schema", PostgresAccessMigrations.Catalog.Migrations[0].Sql + "\n-- changed")]);
        var failure = await Assert.ThrowsExactlyAsync<MigrationProtocolException>(() => new PostgresMigrationRunner(database.Secondary, changed).ApplyAsync());
        Assert.AreEqual(MigrationProtocolFailure.ChecksumMismatch, failure.Reason);
    }

    [TestMethod]
    public async Task MaximumLengthIdentifiersFitTheBoundedKeys()
    {
        await using var database = await Database.CreateAsync();
        var scope = new TicketScope(TestScopes.LongIdentifier(1), TestScopes.LongIdentifier(2), TestScopes.LongIdentifier(3), TestScopes.LongIdentifier(4), new WorkspaceId(TestScopes.LongIdentifier(5)));
        var partition = AccessPartition.From(scope);
        var role = new AccessRole(new RoleId("triage"), "Triage", [AccessPermissions.ConversationRead]);
        Assert.AreEqual("applied:1", AccessStoreContract.Describe(await database.Store.CreateAsync(scope, role), stored => stored.Revision));
        GrantSubject subject = new UserId(TestScopes.LongIdentifier(6));
        var grant = new AccessGrant(new GrantId(TestScopes.LongIdentifier(7)), partition, subject, role.Id,
            new TeamGrantScope(scope.WorkspaceId, new TeamId(TestScopes.LongIdentifier(8))));
        Assert.AreEqual("applied:1", AccessStoreContract.Describe(await database.Store.CreateAsync(grant), stored => stored.Revision));
        Assert.AreEqual(grant, (await database.Competing.ListForSubjectsAsync(partition, scope.WorkspaceId, [subject])).Single().Grant);
        var policy = new AccessFieldPolicy(new FieldPath("attribute." + new string('a', 64) + "." + new string('b', 64)), [AccessPermissions.ContactRead]);
        Assert.AreEqual("applied:1", AccessStoreContract.Describe(await database.Store.CreateAsync(scope, policy), stored => stored.Revision));
        Assert.AreEqual(role, (await database.Competing.GetAsync(scope, role.Id))!.Role);
        Assert.AreEqual(policy, (await database.Competing.GetAsync(scope, policy.Field))!.Policy);
        var auditEvent = new AccessAuditEvent(Guid.NewGuid(), TestClock.DefaultStart, scope, AccessAuditKind.Decision, AccessAuditOutcome.Allowed,
            new AccessActor(AccessActorKind.User, TestScopes.LongIdentifier(6)), resourceId: TestScopes.LongIdentifier(9));
        await database.Store.RecordAsync(auditEvent);
        Assert.AreEqual(auditEvent, (await database.Competing.ReadAuditAsync(scope)).Single());
    }

    [TestMethod]
    public async Task ConcurrentCreatesRespectThePerSubjectCapacity()
    {
        await using var database = await Database.CreateAsync();
        await AccessStoreContract.VerifyConcurrentGrantCapacityAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentGrantUpdatesAreFenced()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        var partition = AccessPartition.From(scope);
        var grant = new AccessGrant(new GrantId("contended"), partition, new UserId("ada"), BuiltInRoles.Viewer.Id, new WorkspaceGrantScope(scope.WorkspaceId));
        await database.Store.CreateAsync(grant);
        var roles = BuiltInRoles.All.Take(5).ToArray();
        var writers = Enumerable.Range(0, 10).Select(index =>
        {
            var store = index % 2 == 0 ? database.Store : database.Competing;
            return store.UpdateAsync(new AccessGrant(grant.Id, partition, new UserId("ada"), roles[index % roles.Length].Id, grant.Scope), 1);
        });
        var outcomes = (await Task.WhenAll(writers)).Select(result => AccessStoreContract.Describe(result, stored => stored.Revision)).ToArray();
        Assert.AreEqual(1, outcomes.Count(outcome => string.Equals(outcome, "applied:2", StringComparison.Ordinal)));
        Assert.AreEqual(9, outcomes.Count(outcome => string.Equals(outcome, "conflict:2", StringComparison.Ordinal)));
        Assert.AreEqual(2L, (await database.Competing.GetAsync(partition, grant.Id))!.Revision);
    }

    [TestMethod]
    public async Task AuditEventsRoundTripPerScope()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        var start = TestClock.DefaultStart;
        var first = new AccessAuditEvent(Guid.CreateVersion7(start), start, scope, AccessAuditKind.Reveal, AccessAuditOutcome.Allowed,
            new AccessActor(AccessActorKind.User, "ada"), AccessPermissions.ContactRead, "contact-1", new TeamId("billing"), new InboxId("email"),
            new GrantId("g1"), new RoleId("triage"), new FieldPath("contact.email"), true, null, "Customer asked to verify identity");
        var second = new AccessAuditEvent(Guid.CreateVersion7(start.AddSeconds(1)), start.AddSeconds(1), scope, AccessAuditKind.Decision, AccessAuditOutcome.Denied,
            new AccessActor(AccessActorKind.System, "exporter"), AccessPermissions.ContactExport, denialReason: AccessDenialReason.UnsupportedPrincipal);
        var elsewhere = new AccessAuditEvent(Guid.NewGuid(), start, TestScopes.Create(workspaceId: "elsewhere"), AccessAuditKind.Decision, AccessAuditOutcome.Allowed,
            new AccessActor(AccessActorKind.User, "ada"));
        await database.Store.RecordAsync(second);
        await database.Competing.RecordAsync(first);
        await database.Store.RecordAsync(elsewhere);
        await Assert.ThrowsExactlyAsync<PostgresException>(() => database.Store.RecordAsync(first));

        CollectionAssert.AreEqual(new[] { first, second }, (await database.Competing.ReadAuditAsync(scope)).ToArray());
        CollectionAssert.AreEqual(new[] { second }, (await database.Competing.ReadAuditAsync(scope, after: first.Id)).ToArray());
        CollectionAssert.AreEqual(new[] { first }, (await database.Competing.ReadAuditAsync(scope, limit: 1)).ToArray());
        Assert.HasCount(1, await database.Store.ReadAuditAsync(elsewhere.Scope));
        Assert.IsEmpty(await database.Store.ReadAuditAsync(TestScopes.Create(tenantId: "other")));
    }

    [TestMethod]
    public async Task CorruptRowsFailClosed()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        var partition = AccessPartition.From(scope);
        await database.Store.CreateAsync(AccessStoreContract.SampleGrant("g", partition, new UserId("ada"), new WorkspaceGrantScope(scope.WorkspaceId)));
        await database.ScalarAsync("UPDATE tf_access_grants SET condition='{\"v\":9,\"c\":{\"op\":\"assignee-self\"}}'");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(partition, new GrantId("g")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.ListForSubjectsAsync(partition, scope.WorkspaceId, [(GrantSubject)new UserId("ada")]));

        await database.Store.CreateAsync(scope, new AccessRole(new RoleId("triage"), "Triage", [AccessPermissions.ConversationRead]));
        await database.ScalarAsync("UPDATE tf_access_roles SET permissions=ARRAY['not a permission']");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, new RoleId("triage")));
    }
}
