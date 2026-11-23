using Npgsql;
using TicketFlow.Access;
using TicketFlow.Channels.LiveChat.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Realtime;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;

namespace TicketFlow.Channels.LiveChat.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresLiveChatStoreTests
{
    private sealed class Database : IAsyncDisposable
    {
        private readonly PostgresTestDatabase storage;

        private Database(PostgresTestDatabase storage)
        {
            this.storage = storage;
            Store = new(storage.First);
            Competing = new(storage.Second);
        }

        public PostgresLiveChatStore Store { get; }
        public PostgresLiveChatStore Competing { get; }
        public PostgresTestDatabase Storage => storage;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_livechat_test_", async storage =>
        {
            var database = new Database(storage);
            await database.Store.InitializeAsync();
            await database.Competing.InitializeAsync();
            return database;
        }, maximumPoolSize: 16);

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task WidgetContract()
    {
        await using var database = await Database.CreateAsync();
        await LiveChatStoreContract.VerifyWidgetsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task SessionContract()
    {
        await using var database = await Database.CreateAsync();
        await LiveChatStoreContract.VerifySessionsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConcurrentLinksAndUpdates()
    {
        await using var database = await Database.CreateAsync();
        await LiveChatStoreContract.VerifyConcurrentLinksAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task OnlyTokenHashesAreStoredAndMigrationsAreIdempotent()
    {
        await using var database = await Database.CreateAsync();
        await database.Store.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Storage.First, PostgresLiveChatMigrations.Catalog).InspectAsync();
        Assert.AreEqual(2, inspection.CurrentVersion);

        var scope = TestScopes.Create(workspaceId: "livechat-hash");
        var token = VisitorSessionTokens.Issue(new VisitorSessionId("vs_hash"));
        var session = new VisitorSession(new VisitorSessionId("vs_hash"), "cfg-chat", new InboxId("chat-inbox"), "v_hash", token.Hash,
            TestClock.DefaultStart, TestClock.DefaultStart, TestClock.DefaultStart.AddDays(1));
        Assert.IsInstanceOfType<LiveChatWriteApplied<StoredVisitorSession>>((await database.Store.CreateAsync(scope, session)).Value);
        await using var command = database.Storage.First.CreateCommand("SELECT count(*) FROM tf_livechat_sessions t WHERE position(convert_to(@secret, 'UTF8') in t.token_hash) > 0 OR t::text LIKE '%' || @secret || '%'");
        command.Parameters.AddWithValue("secret", token.Token.Split('.')[^1]);
        Assert.AreEqual(0L, await command.ExecuteScalarAsync());
        var stored = await database.Competing.GetAsync(AccessPartition.From(scope), session.Id);
        Assert.IsTrue(VisitorSessionTokens.Verify(stored!.Session, VisitorSessionTokens.Parse(token.Token)!.Value.SecretHash, TestClock.DefaultStart));
    }
}
