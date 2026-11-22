using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Conversations.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresConversationStoreTests
{
    internal sealed class Database : IAsyncDisposable
    {
        private readonly PostgresTestDatabase storage;

        private Database(PostgresTestDatabase storage, FakeTimeProvider clock)
        {
            this.storage = storage;
            Clock = clock;
            Store = new(storage.First, clock);
            Competing = new(storage.Second, clock);
        }

        public FakeTimeProvider Clock { get; }
        public PostgresConversationStore Store { get; }
        public PostgresConversationStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_conversations_test_", async storage =>
        {
            var database = new Database(storage, TestClock.Create());
            await database.Store.InitializeAsync();
            return database;
        }, maximumPoolSize: 16);

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var command = Primary.CreateCommand(sql);
            return await command.ExecuteScalarAsync();
        }

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task WriteContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyWritesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task TimelineAndMessageContract()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyTimelineAndMessagesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task DeliveryContract()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyDeliveryAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ListAndFilterContract()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyListAndFiltersAsync(database.Store);
    }

    [TestMethod]
    public async Task DeleteContract()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyDeleteAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentAppendsAcrossClientsAreGapless()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyConcurrentAppendsAsync(database.Store, database.Competing, writers: 16);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateInboundAdmitsOne()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyConcurrentDuplicateInboundAsync(database.Store, database.Competing, writers: 10);
    }

    [TestMethod]
    public async Task OutboxContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyOutboxAsync(database.Store, database.Store, database.Competing, database.Clock);
    }

    [TestMethod]
    public async Task DefinitionContract()
    {
        await using var database = await Database.CreateAsync();
        await ConversationStoreContract.VerifyDefinitionsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentClaimersNeverShareALiveLease()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        await database.Store.RegisterConsumerAsync("routing");
        for (var index = 0; index < 40; index++)
            await ConversationStoreContract.CreateAsync(database.Store, scope, ConversationStoreContract.NewConversation($"c{index:D2}"));
        var stores = new[] { database.Store, database.Competing };
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => stores[index % 2].ClaimAsync("routing", 10, TimeSpan.FromMinutes(1)))));
        var sequences = claims.SelectMany(batch => batch).Select(claim => claim.Entry.Sequence).ToList();
        Assert.AreEqual(40, sequences.Count);
        Assert.AreEqual(40, sequences.Distinct().Count());
    }

    [TestMethod]
    public async Task ServiceRepliesConcurrentlyWithGaplessSequences()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        await ConversationStoreContract.CreateAsync(database.Store, scope, ConversationStoreContract.NewConversation("svc"));
        var agent = TestPrincipals.User(scope, "agent");
        var service = new ConversationService(database.Store, database.Store, new AllowAll(), new AllowAll(), new NoPolicies(), new Workspaces.InMemoryInboxStore(),
            new Workspaces.InMemoryTeamStore(), new Contacts.InMemoryContactStore(), Microsoft.Extensions.Options.Options.Create(new ConversationsOptions { MaximumWriteAttempts = 64 }),
            database.Clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            Task.Run(() => service.ReplyAsync(agent, new ConversationId("svc"), new OutgoingMessageDraft($"reply {index}")))));
        Assert.IsTrue(results.All(result => result.Value is ConversationApplied), string.Join(",", results.Select(result => result.Value?.GetType().Name)));
        var timeline = await database.Competing.ReadTimelineAsync(scope, new ConversationId("svc"), 0, 100, includeNotes: true);
        CollectionAssert.AreEqual(Enumerable.Range(1, 20).Select(index => (long)index).ToArray(), timeline.Select(item => item.Sequence).ToArray());
        Assert.AreEqual(21L, await database.ScalarAsync("SELECT count(*) FROM tf_conversation_outbox"));
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndVersioned()
    {
        await using var database = await Database.CreateAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Primary, PostgresConversationMigrations.Catalog).InspectAsync();
        Assert.AreEqual(2, inspection.CurrentVersion);
        Assert.AreEqual("ticketflow.conversations", inspection.Component);
    }

    [TestMethod]
    public async Task LongIdentifiersUseBoundedKeys()
    {
        await using var database = await Database.CreateAsync();
        var scope = new TicketScope(TestScopes.LongIdentifier(1), TestScopes.LongIdentifier(2), TestScopes.LongIdentifier(3), TestScopes.LongIdentifier(4),
            new WorkspaceId(TestScopes.LongIdentifier(5)));
        var conversation = ConversationStoreContract.NewConversation(TestScopes.LongIdentifier(6), inbox: TestScopes.LongIdentifier(7), contact: TestScopes.LongIdentifier(8),
            team: TestScopes.LongIdentifier(9), assignee: TestScopes.LongIdentifier(10), thread: new ChannelThreadRef("email", new string('t', ChannelThreadRef.MaximumThreadIdLength)));
        var stored = await ConversationStoreContract.CreateAsync(database.Store, scope, conversation,
            ConversationStoreContract.Incoming(TestScopes.LongIdentifier(11), externalId: new string('e', ConversationMessageLimits.MaximumExternalIdLength), contact: TestScopes.LongIdentifier(8)));
        Assert.AreEqual(stored.Conversation, (await database.Competing.GetAsync(scope, conversation.Id))!.Conversation);
        Assert.AreEqual(1, (await database.Competing.ListAsync(scope, new ConversationListQuery(inbox: conversation.Inbox), AllowAllFilter.Instance)).Conversations.Count);
        Assert.IsNotNull(await database.Competing.FindByThreadAsync(scope, conversation.Inbox, conversation.ChannelThread!));
    }

    [TestMethod]
    public async Task UnknownFormatVersionsFailToDecode()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        await ConversationStoreContract.CreateAsync(database.Store, scope, ConversationStoreContract.NewConversation("a"), ConversationStoreContract.Incoming("m1"));
        await database.ScalarAsync("UPDATE tf_conversation_events SET payload=replace(payload, '\"v\":1', '\"v\":9')");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.ReadTimelineAsync(scope, new ConversationId("a"), 0, 10, true));
        await database.ScalarAsync("UPDATE tf_conversations SET attributes='{\"v\":9,\"a\":{}}'::jsonb");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, new ConversationId("a")));
    }

    [TestMethod]
    public async Task ListPlansUseIndexes()
    {
        await using var database = await Database.CreateAsync();
        await using var connection = await database.Primary.OpenConnectionAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await off.ExecuteNonQueryAsync();
        await using var explain = new NpgsqlCommand("""
            EXPLAIN SELECT c.conversation_id FROM tf_conversations c
            WHERE c.scope_key = tf_key_v1('t','p','e','a','w') AND c.inbox_id = 'email'
            ORDER BY c.last_activity_at DESC, c.conversation_id DESC LIMIT 26
            """, connection);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        Assert.IsTrue(plan.Any(line => line.Contains("tf_conversations_inbox", StringComparison.Ordinal)), string.Join("\n", plan));
    }

    private sealed class AllowAll : IAccessEvaluator, IAccessQueryPlanner
    {
        public Task<AccessDecision> AuthorizeAsync(AccessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<AccessDecision>(new AccessAllowed(request.Permission));

        public Task<RevealResult> RevealAsync(RevealRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccessFilter> PlanAsync(AccessQuery query, CancellationToken cancellationToken = default) => Task.FromResult<AccessFilter>(AllowAllFilter.Instance);
    }

    private sealed class NoPolicies : IAccessFieldPolicyStore
    {
        public Task<StoredAccessFieldPolicy?> GetAsync(TicketScope scope, FieldPath field, CancellationToken cancellationToken = default) => Task.FromResult<StoredAccessFieldPolicy?>(null);

        public Task<IReadOnlyList<StoredAccessFieldPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredAccessFieldPolicy>>([]);

        public Task<AccessWriteResult<StoredAccessFieldPolicy>> CreateAsync(TicketScope scope, AccessFieldPolicy policy, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccessWriteResult<StoredAccessFieldPolicy>> UpdateAsync(TicketScope scope, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccessWriteResult<StoredAccessFieldPolicy>> DeleteAsync(TicketScope scope, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
