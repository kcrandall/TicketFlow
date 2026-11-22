using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Testing;
using TicketFlow.Workspaces.StoreContracts;

namespace TicketFlow.Workspaces.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresWorkspaceStoreTests
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

        public PostgresWorkspaceStore Store { get; }
        public PostgresWorkspaceStore Competing { get; }
        public NpgsqlDataSource Primary => storage.First;

        public static Task<Database> CreateAsync(bool initialize = true) => PostgresTestDatabase.CreateAsync("tf_workspaces_test_", async storage =>
        {
            var database = new Database(storage);
            if (initialize)
                await database.Store.InitializeAsync();
            return database;
        }, maximumPoolSize: 16);

        public async Task<int> ExecuteAsync(string sql)
        {
            await using var command = Primary.CreateCommand(sql);
            return await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task SettingsContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await WorkspaceStoreContract.VerifySettingsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task InboxContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await WorkspaceStoreContract.VerifyInboxesAsync(database.Store, database.Competing);
        await WorkspaceStoreContract.VerifyInboxListsAsync(database.Store);
        await WorkspaceStoreContract.VerifyInboxCapacityAsync(database.Store);
    }

    [TestMethod]
    public async Task TeamContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await WorkspaceStoreContract.VerifyTeamsAsync(database.Store, database.Competing);
        await WorkspaceStoreContract.VerifyTeamListsAsync(database.Store);
        await WorkspaceStoreContract.VerifyTeamMembershipLimitAsync(database.Store);
        await WorkspaceStoreContract.VerifyConcurrentTeamMembershipLimitAsync(database.Store, database.Competing);
        await WorkspaceStoreContract.VerifyTeamCapacityAsync(database.Store);
    }

    [TestMethod]
    public async Task CalendarContractUsesIndependentClients()
    {
        await using var database = await Database.CreateAsync();
        await WorkspaceStoreContract.VerifyCalendarsAsync(database.Store, database.Competing);
        await WorkspaceStoreContract.VerifyCalendarCapacityAsync(database.Store);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(42)]
    [DataRow(31337)]
    public async Task SqlListsEqualPointChecksOverRandomizedGrants(int seed)
    {
        await using var database = await Database.CreateAsync();
        await AccessFilteredListParity.VerifyAsync(database.Store, database.Competing, seed);
    }

    [TestMethod]
    public async Task InitializationIsExplicitConcurrentAndChecksumVerified()
    {
        await using var database = await Database.CreateAsync(initialize: false);
        var scope = TestScopes.Create();
        await Assert.ThrowsExactlyAsync<PostgresException>(() => database.Store.GetAsync(scope));
        await Task.WhenAll(database.Store.InitializeAsync(), database.Competing.InitializeAsync(), database.Store.InitializeAsync());
        await database.Competing.InitializeAsync();
        var inspection = await new PostgresMigrationRunner(database.Primary, PostgresWorkspaceMigrations.Catalog).InspectAsync();
        Assert.AreEqual(1, inspection.CurrentVersion);
        Assert.AreEqual(PostgresWorkspaceMigrations.Catalog.Migrations[0].Checksum, inspection.Applied[0].Checksum);
        Assert.IsNull(await database.Store.GetAsync(scope));

        await database.ExecuteAsync("UPDATE tf_store_migrations SET checksum = repeat('0', 64) WHERE component = 'ticketflow.workspaces'");
        var failure = await Assert.ThrowsExactlyAsync<MigrationProtocolException>(() => database.Store.InitializeAsync());
        Assert.AreEqual(MigrationProtocolFailure.ChecksumMismatch, failure.Reason);
    }

    [TestMethod]
    public async Task CorruptRowsFailClosed()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        await database.Store.CreateAsync(scope, WorkspaceStoreContract.SampleInbox("email"));
        await database.Store.CreateAsync(scope, new Team(new TeamId("billing"), "Billing", null, [new UserId("ada")]));
        await database.Store.CreateAsync(scope, WorkspaceStoreContract.SampleCalendar("office"));
        await database.Store.CreateAsync(scope, new WorkspaceSettings("Acme", "en", "UTC"));

        await database.ExecuteAsync("UPDATE tf_workspace_inboxes SET channel_kind = 'Not A Kind'");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, new InboxId("email")));
        await database.ExecuteAsync("UPDATE tf_workspace_teams SET member_count = 2");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, new TeamId("billing")));
        await database.ExecuteAsync("UPDATE tf_workspace_calendars SET schedule = '{\"v\":2,\"week\":[],\"overrides\":[]}'");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, new BusinessCalendarId("office")));
        await database.ExecuteAsync("UPDATE tf_workspace_settings SET enabled_features = ARRAY['a'], disabled_features = ARRAY['a']");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope));
    }

    [TestMethod]
    public async Task HostileValuesStayParameters()
    {
        await using var database = await Database.CreateAsync();
        var hostile = "x'); DROP TABLE tf_workspace_inboxes; --";
        var scope = TestScopes.Create(tenantId: hostile, workspaceId: hostile);
        var inbox = new Inbox(new InboxId(hostile), hostile, new ChannelBinding(new ChannelKind("email"), hostile), greetingMessage: hostile);
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await database.Store.CreateAsync(scope, inbox)).Value);
        var filter = new AndFilter([new ScopeFilter(scope), new InboxInFilter([new InboxId(hostile)]), new NotFilter(new AttributeEqualsFilter("tier", hostile))]);
        // The attribute is unknown for inboxes, so ¬(tier = x) is unknown and excludes the row; the inbox filter alone includes it.
        Assert.IsEmpty((await database.Store.ListInboxesAsync(scope, filter, new())).Items);
        Assert.HasCount(1, (await database.Store.ListInboxesAsync(scope, new AndFilter([new ScopeFilter(scope), new InboxInFilter([new InboxId(hostile)])]), new(1, "a"))).Items);
        Assert.AreEqual(hostile, (await database.Competing.GetAsync(scope, new InboxId(hostile)))!.Inbox.GreetingMessage);
    }

    [TestMethod]
    public async Task ServicesRunOverPostgresStores()
    {
        await using var database = await Database.CreateAsync();
        var clock = TestClock.Create();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddWorkspaces().AddPostgresWorkspaceStores(database.Primary);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.AreSame(provider.GetRequiredService<PostgresWorkspaceStore>(), provider.GetRequiredService<ITeamStore>());
        await using var request = provider.CreateAsyncScope();
        var scope = TestScopes.Create();
        var admin = TestPrincipals.User(scope, "admin");
        await provider.GetRequiredService<IAccessGrantStore>().CreateAsync(new AccessGrant(new GrantId("g-admin"), AccessPartition.From(scope), admin.UserId, BuiltInRoles.Admin.Id, new WorkspaceGrantScope(scope.WorkspaceId)));
        var teams = request.ServiceProvider.GetRequiredService<ITeamService>();
        var created = await teams.CreateAsync(admin, new Team(new TeamId("billing"), "Billing", null, [new UserId("ada")]));
        Assert.IsInstanceOfType<WorkspaceSucceeded<StoredTeam>>(created.Value);
        var principal = await request.ServiceProvider.GetRequiredService<ITeamMembershipResolver>().ResolveUserPrincipalAsync(scope, new UserId("ada"));
        CollectionAssert.AreEqual(new[] { new TeamId("billing") }, principal.Teams.ToArray());
        var inboxes = request.ServiceProvider.GetRequiredService<IInboxService>();
        Assert.IsInstanceOfType<WorkspaceSucceeded<StoredInbox>>((await inboxes.CreateAsync(admin, WorkspaceStoreContract.SampleInbox("email"))).Value);
        Assert.IsInstanceOfType<WorkspaceSucceeded<StoredAccessGrant>>((await inboxes.AddAgentAsync(admin, new InboxId("email"), new UserId("ada"))).Value);
        var listed = (WorkspaceSucceeded<WorkspacePage<StoredInbox>>)(await inboxes.ListAsync(principal)).Value!;
        CollectionAssert.AreEqual(new[] { "email" }, listed.Value.Items.Select(item => item.Inbox.Id.Value).ToArray());
    }
}
