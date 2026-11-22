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
        await WorkspaceStoreContract.VerifyInboxChannelBindingsAsync(database.Store, database.Competing);
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
    public async Task MaximumLengthIdentifiersRoundTripEverywhere()
    {
        await using var database = await Database.CreateAsync();
        string Long(int seed) => TestScopes.LongIdentifier(seed);
        var scope = new TicketScope(Long(101), Long(102), Long(103), Long(104), new WorkspaceId(Long(105)));
        var sibling = new TicketScope(Long(101), Long(102), Long(103), Long(104), new WorkspaceId(Long(106)));
        var calendarId = new BusinessCalendarId(Long(110));
        var calendar = new BusinessCalendar(calendarId, "Long", "Europe/London", [new(DayOfWeek.Monday, BusinessHours.From(9, 0, 17, 0))], [CalendarDateOverride.Holiday(new(2026, 12, 25))]);
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredBusinessCalendar>>((await database.Store.CreateAsync(scope, calendar)).Value);
        Assert.AreEqual(calendar, (await database.Competing.GetAsync(scope, calendarId))!.Calendar);
        Assert.AreEqual(calendarId, (await database.Competing.ListCalendarsAsync(scope)).Single().Calendar.Id);
        Assert.IsNull(await database.Competing.GetAsync(sibling, calendarId));

        var inboxIds = new[] { Long(120), Long(121), Long(122) }.Order(StringComparer.Ordinal).ToArray();
        foreach (var (id, index) in inboxIds.Select((id, index) => (id, index)))
        {
            var inbox = new Inbox(new InboxId(id), "Long", new ChannelBinding(new ChannelKind(new string('k', ChannelKind.MaximumLength)), Long(130 + index)), calendarId,
                new SlaPolicyId(Long(140)), new RoutingPolicyId(Long(141)));
            Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await database.Store.CreateAsync(scope, inbox)).Value);
            WorkspaceStoreContract.AssertInbox(inbox, (await database.Competing.GetAsync(scope, inbox.Id))!.Inbox);
        }
        var firstPage = await database.Competing.ListInboxesAsync(scope, new ScopeFilter(scope), new(2));
        Assert.IsTrue(firstPage.HasMore);
        var rest = await database.Competing.ListInboxesAsync(scope, new InboxInFilter(inboxIds.Select(id => new InboxId(id))), new(2, firstPage.Items[^1].Inbox.Id.Value));
        CollectionAssert.AreEqual(inboxIds, firstPage.Items.Concat(rest.Items).Select(item => item.Inbox.Id.Value).ToArray());
        Assert.IsFalse(rest.HasMore);
        Assert.IsEmpty((await database.Competing.ListInboxesAsync(sibling, AllowAllFilter.Instance, new())).Items);

        var member = new UserId(Long(150));
        var teamIds = new[] { Long(160), Long(161), Long(162) }.Order(StringComparer.Ordinal).ToArray();
        foreach (var id in teamIds)
        {
            var team = new Team(new TeamId(id), "Long", "Long team", [member, new UserId(Long(151)), new UserId(Long(152))]);
            Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredTeam>>((await database.Store.CreateAsync(scope, team)).Value);
            Assert.AreEqual(team, (await database.Competing.GetAsync(scope, team.Id))!.Team);
        }
        CollectionAssert.AreEqual(teamIds, (await database.Competing.ListTeamsForUserAsync(scope, member)).Select(team => team.Value).ToArray());
        Assert.IsEmpty(await database.Competing.ListTeamsForUserAsync(sibling, member));
        var teamsFirst = await database.Competing.ListTeamsAsync(scope, new TeamInFilter(teamIds.Select(id => new TeamId(id))), new(1));
        var teamsRest = await database.Competing.ListTeamsAsync(scope, AllowAllFilter.Instance, new(10, teamsFirst.Items[0].Id.Value));
        CollectionAssert.AreEqual(teamIds, teamsFirst.Items.Concat(teamsRest.Items).Select(team => team.Id.Value).ToArray());
        Assert.AreEqual(3, teamsRest.Items[0].MemberCount);

        var updated = new Team(new TeamId(teamIds[0]), "Long", null, [new UserId(Long(153))]);
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredTeam>>((await database.Competing.UpdateAsync(scope, updated, 1)).Value);
        CollectionAssert.AreEqual(teamIds.Skip(1).ToArray(), (await database.Store.ListTeamsForUserAsync(scope, member)).Select(team => team.Value).ToArray());
        Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredTeam>>((await database.Store.DeleteAsync(scope, new TeamId(teamIds[1]), 1)).Value);
        CollectionAssert.AreEqual(new[] { teamIds[2] }, (await database.Store.ListTeamsForUserAsync(scope, member)).Select(team => team.Value).ToArray());
    }

    [TestMethod]
    public async Task UpgradesFromVersionOneAndEnforcesBindingsOnExistingRows()
    {
        await using var database = await Database.CreateAsync(initialize: false);
        await new PostgresMigrationRunner(database.Primary, PostgresWorkspaceMigrations.Catalog).ApplyAsync(1);
        var scope = TestScopes.Create();
        await database.ExecuteAsync("""
            INSERT INTO tf_workspace_inboxes(tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id, format_version, name, channel_kind, channel_config_id, enabled, revision)
            VALUES('tenant', 'project', 'test', 'app', 'workspace', 'legacy', 1, 'Legacy', 'email', 'mailbox', true, 1)
            """);
        await database.Store.InitializeAsync();
        Assert.AreEqual("Legacy", (await database.Store.GetAsync(scope, new InboxId("legacy")))!.Inbox.Name);
        var duplicate = new Inbox(new InboxId("new"), "New", new ChannelBinding(new ChannelKind("email"), "mailbox"));
        Assert.AreEqual(WorkspaceWriteRejection.ChannelBindingInUse, ((WorkspaceWriteRejected)(await database.Competing.CreateAsync(scope, duplicate)).Value!).Reason);
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
        Assert.AreEqual(PostgresWorkspaceMigrations.Catalog.Migrations.Count, inspection.CurrentVersion);
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
