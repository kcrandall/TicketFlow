using Npgsql;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Surveys.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Surveys.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresSurveyStoreTests
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

        public PostgresSurveyStore Store { get; }
        public PostgresSurveyStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;

        public static Task<Database> CreateAsync(bool initialize = true) => PostgresTestDatabase.CreateAsync("tf_surveys_test_", async storage =>
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
    public async Task SurveyContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await SurveyStoreContract.VerifySurveysAsync(database.Store, database.Competing);
        await SurveyStoreContract.VerifySurveyCapacityAsync(database.Store);
    }

    [TestMethod]
    public async Task InvitationContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await SurveyStoreContract.VerifyInvitationsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ResponseContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await SurveyStoreContract.VerifyResponsesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ListAndAggregateContract()
    {
        await using var database = await Database.CreateAsync();
        await SurveyStoreContract.VerifyListsAndAggregatesAsync(database.Store);
        await SurveyStoreContract.VerifyAggregateTruncationAsync(database.Store);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(42)]
    public async Task ResponseListsEqualPointChecks(int seed)
    {
        await using var database = await Database.CreateAsync();
        await AccessFilteredResponseParity.VerifyAsync(database.Store, seed);
    }

    [TestMethod]
    public async Task OnlyTokenHashesArePersisted()
    {
        await using var database = await Database.CreateAsync();
        var scope = SurveyStoreContract.NewScope();
        var token = SurveyToken.Create();
        await SurveyStoreContract.CreateSentAsync(database.Store, scope, SurveyStoreContract.SampleInvitation(), token, SurveyStoreContract.Now);
        Assert.AreEqual(32, await database.ScalarAsync("SELECT length(token_hash) FROM tf_survey_invitations"));
        Assert.AreEqual(0L, await database.ScalarAsync($"SELECT count(*) FROM tf_survey_invitations WHERE position('{token.Value[5..]}' IN (tf_survey_invitations::text)) > 0"));
    }

    [TestMethod]
    public async Task InitializationIsExplicitIdempotentAndChecksummed()
    {
        await using var database = await Database.CreateAsync(initialize: false);
        Assert.IsInstanceOfType<DBNull>(await database.ScalarAsync("SELECT to_regclass('tf_survey_invitations')::text"));
        await database.Store.InitializeAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Primary, PostgresSurveyMigrations.Catalog).InspectAsync();
        Assert.AreEqual(1, inspection.CurrentVersion);
        Assert.AreEqual(PostgresSurveyMigrations.Catalog.Migrations[0].Checksum, inspection.Applied[0].Checksum);
    }

    [TestMethod]
    public async Task CorruptRowsFailLoudly()
    {
        await using var database = await Database.CreateAsync();
        var scope = SurveyStoreContract.NewScope();
        await database.Store.CreateSurveyAsync(scope, SurveyStoreContract.SampleSurvey());
        await using (var command = database.Primary.CreateCommand("""UPDATE tf_survey_definitions SET questions='{"v":7,"items":[]}'"""))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetSurveyAsync(scope, new SurveyId("csat")));
    }
}
