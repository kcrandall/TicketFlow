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
        await SurveyStoreContract.VerifyClaimsAsync(database.Store, database.Competing);
    }

    private sealed class CountingSender : ISurveyInvitationSender
    {
        public System.Collections.Concurrent.ConcurrentBag<SurveyInvitationMessage> Sent { get; } = [];

        public async Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Random.Shared.Next(3), cancellationToken);
            Sent.Add(message);
        }
    }

    [TestMethod]
    public async Task TwoDispatchersOnTwoDataSourcesSendEachInvitationOnce()
    {
        await using var database = await Database.CreateAsync();
        var scope = SurveyStoreContract.NewScope();
        var clock = TestClock.Create(SurveyStoreContract.Now.AddHours(1));
        await database.Store.CreateSurveyAsync(scope, SurveyStoreContract.SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(1))));
        for (var index = 0; index < 120; index++)
            Assert.AreEqual("created", SurveyStoreContract.C(await database.Store.CreateInvitationAsync(scope,
                SurveyStoreContract.SampleInvitation(conversation: $"c{index}", contact: $"k{index}"), TimeSpan.Zero)));
        var sender = new CountingSender();
        var options = Microsoft.Extensions.Options.Options.Create(new SurveyOptions());
        var first = new SurveyDispatcher(database.Store, sender, clock, options);
        var second = new SurveyDispatcher(database.Competing, sender, clock, options);
        async Task<int> DrainAsync(SurveyDispatcher dispatcher)
        {
            var total = 0;
            while ((await dispatcher.DispatchDueAsync(scope, 7)) is { Sent: > 0 } summary)
                total += summary.Sent;
            return total;
        }
        var totals = await Task.WhenAll(Task.Run(() => DrainAsync(first)), Task.Run(() => DrainAsync(second)));
        Assert.AreEqual(120, totals.Sum());
        Assert.HasCount(120, sender.Sent);
        Assert.AreEqual(120, sender.Sent.Select(message => message.Invitation.Id).Distinct().Count(), "Exactly one send per invitation.");
        Assert.IsTrue(totals.All(total => total > 0), "Both dispatchers took part.");
        Assert.AreEqual(120L, await database.ScalarAsync("SELECT count(*) FROM tf_survey_invitations WHERE state=2 AND lease_until IS NULL AND attempts=1"));
    }

    [TestMethod]
    public async Task VersionOneSchemasUpgradeInPlace()
    {
        await using var database = await Database.CreateAsync(initialize: false);
        var runner = new PostgresMigrationRunner(database.Primary, PostgresSurveyMigrations.Catalog);
        await runner.ApplyAsync(1);
        var scope = SurveyStoreContract.NewScope();
        await using (var insert = database.Primary.CreateCommand($"""
            INSERT INTO tf_survey_invitations(tenant_id, project_id, environment_id, app_id, workspace_id, invitation_id, survey_id, kind, conversation_id, contact_id,
                locale, created_at, send_after, state, attempts, token_hash, sent_at, expires_at, format_version, revision)
            VALUES('{scope.TenantId}', '{scope.ProjectId}', '{scope.EnvironmentId}', '{scope.AppId}', '{scope.WorkspaceId.Value}', gen_random_uuid(), 'csat', 1, 'c1', 'k1',
                'en', now(), now(), 2, 1, decode(repeat('ab', 32), 'hex'), now(), now() + interval '1 day', 1, 2)
            """))
            await insert.ExecuteNonQueryAsync();
        await database.Store.InitializeAsync();
        Assert.AreEqual(3, (await runner.InspectAsync()).CurrentVersion);
        var due = await database.Store.ListDueInvitationsAsync(scope, DateTimeOffset.UtcNow.AddDays(2), 10);
        Assert.IsEmpty(due);
        Assert.AreEqual(1L, await database.ScalarAsync("SELECT count(*) FROM tf_survey_invitations WHERE issued_at IS NOT NULL AND lease_until IS NULL"));
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
    public void BuilderExtensionsLiveInTheStoreNamespace() =>
        Assert.AreEqual("TicketFlow.Surveys.Stores.Postgres", typeof(PostgresSurveyTicketFlowBuilderExtensions).Namespace);

    [TestMethod]
    public async Task InitializationIsExplicitIdempotentAndChecksummed()
    {
        await using var database = await Database.CreateAsync(initialize: false);
        Assert.IsInstanceOfType<DBNull>(await database.ScalarAsync("SELECT to_regclass('tf_survey_invitations')::text"));
        await database.Store.InitializeAsync();
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Primary, PostgresSurveyMigrations.Catalog).InspectAsync();
        Assert.AreEqual(3, inspection.CurrentVersion);
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
