using Npgsql;
using TicketFlow.HelpCenter.StoreContracts;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;
using static TicketFlow.HelpCenter.StoreContracts.HelpCenterStoreContract;

namespace TicketFlow.HelpCenter.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresHelpCenterStoreTests
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

        public PostgresHelpCenterStore Store { get; }
        public PostgresHelpCenterStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;

        public static Task<Database> CreateAsync(bool initialize = true) => PostgresTestDatabase.CreateAsync("tf_helpcenter_test_", async storage =>
        {
            var database = new Database(storage);
            if (initialize)
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
    public async Task PortalContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await HelpCenterStoreContract.VerifyPortalsAsync(database.Store, database.Competing);
        await HelpCenterStoreContract.VerifyPortalCapacityAsync(database.Store);
    }

    [TestMethod]
    public async Task CollectionContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await HelpCenterStoreContract.VerifyCollectionsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ArticleContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await HelpCenterStoreContract.VerifyArticlesAsync(database.Store, database.Competing);
        await HelpCenterStoreContract.VerifyPublishedListingAsync(database.Store);
    }

    [TestMethod]
    public async Task SearchContract()
    {
        await using var database = await Database.CreateAsync();
        await HelpCenterStoreContract.VerifySearchAsync(database.Store);
    }

    [TestMethod]
    public async Task FeedbackContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await HelpCenterStoreContract.VerifyFeedbackAsync(database.Store, database.Store, database.Competing);
    }

    [TestMethod]
    public async Task SearchStemsPerLocaleAndFallsBackToSimple()
    {
        await using var database = await Database.CreateAsync();
        var store = database.Store;
        var scope = NewScope();
        var japanese = new HelpCenterLocale("ja");
        var portal = new Portal(new PortalId("portal"), new HelpCenterSlug("help"), "Help", English, [German, japanese], PortalVisibility.Public);
        Assert.AreEqual("applied:1", P(await store.CreatePortalAsync(scope, portal)));
        var article = new Article(new ArticleId("a1"), portal.Id,
        [
            Translation(English, "Resetting passwords", "reset", "When you forgot your password, the reset link helps.", "account-security"),
            Translation(German, "Passwörter zurücksetzen", "zuruecksetzen", "Wenn Sie Ihre Passwörter vergessen haben."),
            Translation(japanese, "パスワード", "pasuwado", "リセット token-xyz"),
        ]);
        Assert.AreEqual("applied:1", A(await store.CreateArticleAsync(scope, Draft(article))));
        await PublishAsync(store, scope, article, 1, 1);

        Assert.HasCount(1, await store.SearchAsync(scope, new HelpCenterSearchQuery(portal.Id, English, "password resets")), "English stemming.");
        Assert.HasCount(1, await store.SearchAsync(scope, new HelpCenterSearchQuery(portal.Id, English, "security")), "Tags are indexed.");
        Assert.HasCount(1, await store.SearchAsync(scope, new HelpCenterSearchQuery(portal.Id, German, "Passwort")), "German stemming.");
        Assert.HasCount(1, await store.SearchAsync(scope, new HelpCenterSearchQuery(portal.Id, japanese, "token")), "Simple configuration fallback.");
        Assert.IsEmpty(await store.SearchAsync(scope, new HelpCenterSearchQuery(portal.Id, English, "password -reset")), "websearch negation.");
        Assert.IsEmpty(await store.SearchAsync(scope, new HelpCenterSearchQuery(portal.Id, English, "the")), "Stop words alone match nothing.");
        var hit = (await store.SearchAsync(scope, new HelpCenterSearchQuery(portal.Id, English, "\"reset link\"")))!.Single();
        Assert.Contains("reset link", hit.Snippet);
        Assert.DoesNotContain("<b>", hit.Snippet);
        Assert.AreEqual("english", await database.ScalarAsync("SELECT ts_config::text FROM tf_helpcenter_published WHERE locale='en'"));
        Assert.AreEqual("simple", await database.ScalarAsync("SELECT ts_config::text FROM tf_helpcenter_published WHERE locale='ja'"));
    }

    [TestMethod]
    public async Task SearchConfigurationsExistOnTheServer()
    {
        await using var database = await Database.CreateAsync();
        foreach (var name in HelpCenterSearchConfiguration.Names)
            Assert.AreEqual(name, await database.ScalarAsync($"SELECT cfgname::text FROM pg_catalog.pg_ts_config WHERE cfgname='{name}'"), name);
    }

    [TestMethod]
    public async Task LongIdentifiersFitBoundedKeys()
    {
        await using var database = await Database.CreateAsync();
        var scope = new Core.TicketScope(TestScopes.LongIdentifier(1), TestScopes.LongIdentifier(2), TestScopes.LongIdentifier(3), TestScopes.LongIdentifier(4), new Core.WorkspaceId(TestScopes.LongIdentifier(5)));
        var portal = new Portal(new PortalId(TestScopes.LongIdentifier(6)), new HelpCenterSlug("help"), "Help", English);
        Assert.AreEqual("applied:1", P(await database.Store.CreatePortalAsync(scope, portal)));
        var article = new Article(new ArticleId(TestScopes.LongIdentifier(7)), portal.Id, [Translation(English, "Long", "long", "Body")]);
        Assert.AreEqual("applied:1", A(await database.Store.CreateArticleAsync(scope, Draft(article))));
        await PublishAsync(database.Store, scope, article, 1, 1);
        Assert.AreEqual(article.Id, (await database.Store.GetPublishedByIdAsync(scope, article.Id, English))!.Article);
    }

    [TestMethod]
    public async Task InitializationIsExplicitIdempotentAndChecksummed()
    {
        await using var database = await Database.CreateAsync(initialize: false);
        Assert.IsInstanceOfType<DBNull>(await database.ScalarAsync("SELECT to_regclass('tf_helpcenter_articles')::text"));
        await database.Store.InitializeAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Primary, PostgresHelpCenterMigrations.Catalog).InspectAsync();
        Assert.AreEqual(1, inspection.CurrentVersion);
        Assert.AreEqual(PostgresHelpCenterMigrations.Catalog.Migrations[0].Checksum, inspection.Applied[0].Checksum);
    }

    [TestMethod]
    public async Task CorruptRowsFailLoudly()
    {
        await using var database = await Database.CreateAsync();
        var scope = NewScope();
        Assert.AreEqual("applied:1", P(await database.Store.CreatePortalAsync(scope, SamplePortal())));
        Assert.AreEqual("applied:1", A(await database.Store.CreateArticleAsync(scope, Draft(SampleArticle("a1")))));
        await using (var command = database.Primary.CreateCommand("""UPDATE tf_helpcenter_articles SET translations='{"v":9,"items":[]}'"""))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetArticleAsync(scope, new ArticleId("a1")));
    }
}
