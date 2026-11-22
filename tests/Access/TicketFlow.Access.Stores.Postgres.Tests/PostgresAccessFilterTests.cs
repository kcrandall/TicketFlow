using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresAccessFilterTests
{
    private static readonly PostgresAccessFilterColumns Columns = new() { Labels = "labels", Attributes = "attributes" };

    private const string ResourceTable = """
        CREATE TABLE tf_test_resources (
            id text PRIMARY KEY, tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL,
            workspace_id text NOT NULL, team_id text NULL, inbox_id text NULL, assignee_id text NULL, labels text[] NULL, attributes jsonb NULL)
        """;

    private static async Task InsertAsync(NpgsqlDataSource source, IEnumerable<AccessResource> resources)
    {
        foreach (var resource in resources)
        {
            await using var insert = source.CreateCommand("INSERT INTO tf_test_resources VALUES(@id, @tenant, @project, @environment, @app, @workspace, @team, @inbox, @assignee, @labels, @attributes)");
            insert.Parameters.AddWithValue("id", resource.ResourceId!);
            insert.Parameters.AddWithValue("tenant", resource.Scope.TenantId);
            insert.Parameters.AddWithValue("project", resource.Scope.ProjectId);
            insert.Parameters.AddWithValue("environment", resource.Scope.EnvironmentId);
            insert.Parameters.AddWithValue("app", resource.Scope.AppId);
            insert.Parameters.AddWithValue("workspace", resource.Scope.WorkspaceId.Value);
            insert.Parameters.Add(new NpgsqlParameter("team", NpgsqlDbType.Text) { Value = (object?)resource.TeamId?.Value ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("inbox", NpgsqlDbType.Text) { Value = (object?)resource.InboxId?.Value ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("assignee", NpgsqlDbType.Text) { Value = (object?)resource.AssigneeId?.Value ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("labels", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)resource.Labels?.Select(label => label.Value).ToArray() ?? DBNull.Value });
            // Some rows store NULL instead of an empty object: both mean every attribute is unknown.
            var nullAttributes = resource.Attributes.Count == 0 && resource.ResourceId![^1] % 2 == 0;
            insert.Parameters.Add(new NpgsqlParameter("attributes", NpgsqlDbType.Jsonb) { Value = nullAttributes ? DBNull.Value : JsonSerializer.Serialize(resource.Attributes) });
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task<HashSet<string>> SelectAsync(NpgsqlDataSource source, PostgresAccessFilterSql filter)
    {
        await using var command = source.CreateCommand($"SELECT id FROM tf_test_resources WHERE {filter.Sql}");
        foreach (var parameter in filter.Parameters)
            command.Parameters.Add(parameter.Clone());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(99)]
    [DataRow(2027)]
    public async Task SqlFiltersMatchPointChecksOverRandomizedFixtures(int seed)
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_access_filter_");
        var clock = TestClock.Create();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddTicketFlow().AddAccessControl().AddAccessPermissions(new PermissionDefinition("triage:run")).AddPostgresAccessStores(database.First);
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<PostgresAccessStore>();
        await store.InitializeAsync();
        await using (var table = database.First.CreateCommand(ResourceTable))
            await table.ExecuteNonQueryAsync();

        var random = new Random(seed);
        var scope = TestScopes.Create();
        clock.Advance(TimeSpan.FromMinutes(random.Next(24 * 60)));
        await store.CreateAsync(scope, RandomAccessFixture.CustomRole);
        await store.CreateAsync(scope, RandomAccessFixture.DeletedRole);
        await store.DeleteAsync(scope, RandomAccessFixture.DeletedRole.Id, 1);
        foreach (var grant in RandomAccessFixture.Grants(random, scope, clock.GetUtcNow(), 40))
            Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await store.CreateAsync(grant)).Value);
        var resources = RandomAccessFixture.Resources(random, scope, 150);
        await InsertAsync(database.First, resources);

        using var request = provider.CreateScope();
        var evaluator = request.ServiceProvider.GetRequiredService<IAccessEvaluator>();
        var planner = request.ServiceProvider.GetRequiredService<IAccessQueryPlanner>();
        var permitted = 0;
        foreach (var principal in RandomAccessFixture.Principals(random, scope))
        {
            foreach (var permission in RandomAccessFixture.Permissions)
            {
                var filter = await planner.PlanAsync(new AccessQuery(principal, permission, scope));
                var translation = PostgresAccessFilterTranslator.Translate(filter, Columns);
                Assert.IsInstanceOfType<PostgresAccessFilterSql>(translation.Value);
                var selected = await SelectAsync(database.First, translation.OrDenyAll());
                var expected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var resource in resources)
                {
                    var decision = await evaluator.AuthorizeAsync(new AccessRequest(principal, permission, resource));
                    Assert.AreEqual(decision.IsPermitted, AccessFilterEvaluator.Matches(filter, resource));
                    if (decision.IsPermitted)
                        expected.Add(resource.ResourceId!);
                }
                Assert.IsTrue(expected.SetEquals(selected), $"seed {seed}: {permission} for {principal.Value!.GetType().Name}: expected {expected.Count}, SQL {selected.Count}");
                permitted += expected.Count;
            }
        }
        Assert.IsGreaterThan(50, permitted);
    }

    [TestMethod]
    public async Task UntranslatableFiltersDenyAllAndValuesAreParameters()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_access_filter_");
        await using (var table = database.First.CreateCommand(ResourceTable))
            await table.ExecuteNonQueryAsync();
        var scope = TestScopes.Create();
        const string hostile = "x'); DROP TABLE tf_test_resources; --";
        await InsertAsync(database.First,
        [
            new AccessResource(scope, "a", attributes: [new("tier", hostile)]),
            new AccessResource(scope, "b", attributes: [new("tier", "vip")]),
            new AccessResource(scope, "c"),
            new AccessResource(scope, "d", attributes: [new("region", "eu")]),
        ]);
        await using (var nulls = database.First.CreateCommand("UPDATE tf_test_resources SET attributes = NULL WHERE id = 'c'"))
            await nulls.ExecuteNonQueryAsync();

        var negated = new AndFilter(new ScopeFilter(scope), new NotFilter(new AttributeEqualsFilter("tier", "vip")));
        // Rows missing the attribute (NULL column or absent key) are unknown under negation and excluded.
        CollectionAssert.AreEquivalent(new[] { "a" }, (await SelectAsync(database.First, PostgresAccessFilterTranslator.Translate(negated, Columns).OrDenyAll())).ToArray());
        var hostileMatch = PostgresAccessFilterTranslator.Translate(new AttributeInFilter("tier", [hostile]), Columns).OrDenyAll();
        Assert.DoesNotContain("DROP", hostileMatch.Sql);
        CollectionAssert.AreEquivalent(new[] { "a" }, (await SelectAsync(database.First, hostileMatch)).ToArray());

        // Without an attribute column the negated predicate would match everything if dropped; it must deny all instead.
        var untranslatable = PostgresAccessFilterTranslator.Translate(negated, new PostgresAccessFilterColumns());
        Assert.IsInstanceOfType<PostgresAccessFilterUntranslatable>(untranslatable.Value);
        Assert.AreSame(PostgresAccessFilterSql.DenyAll, untranslatable.OrDenyAll());
        Assert.IsEmpty(await SelectAsync(database.First, untranslatable.OrDenyAll()));
        await using var count = database.First.CreateCommand("SELECT count(*) FROM tf_test_resources");
        Assert.AreEqual(4L, (long)(await count.ExecuteScalarAsync())!);
    }
}

[TestClass]
public sealed class PostgresAccessFilterTranslatorTests
{
    [TestMethod]
    public void TranslatesEveryNodeWithBoundParameters()
    {
        var scope = TestScopes.Create();
        var filter = new AndFilter(
            new ScopeFilter(scope),
            new OrFilter(new TeamInFilter([new TeamId("t2"), new TeamId("t1")]), new InboxInFilter([]), new AssigneeIsFilter(new UserId("ada"))),
            new NotFilter(new OrFilter(new HasLabelFilter(new LabelId("vip")), new AttributeEqualsFilter("tier", "gold"), new AttributeInFilter("region", ["eu", "us"]))),
            new OrFilter(AllowAllFilter.Instance, DenyAllFilter.Instance, UnknownFilter.Instance));
        var sql = (PostgresAccessFilterSql)PostgresAccessFilterTranslator.Translate(filter, new() { Labels = "r.labels", Attributes = "r.attributes" }, "p_").Value!;
        // Behavior is verified against PostgreSQL in PostgresAccessFilterTests; here: every value is a bound parameter.
        Assert.HasCount(12, sql.Parameters);
        CollectionAssert.AreEqual(Enumerable.Range(0, 12).Select(index => $"p_{index}").ToArray(), sql.Parameters.Select(parameter => parameter.ParameterName).ToArray());
        foreach (var literal in new[] { scope.TenantId, "t1", "ada", "vip", "tier", "gold", "region", "eu" })
            Assert.DoesNotContain("'" + literal, sql.Sql);
        CollectionAssert.AreEqual(new[] { "t1", "t2" }, (string[])sql.Parameters.Single(parameter => parameter.Value is string[] { Length: 2 } values && values[0] == "t1").Value!);
        CollectionAssert.Contains(sql.Parameters.Select(parameter => parameter.Value).ToArray(), scope.TenantId);
        StringAssert.Contains(sql.Sql, "\"r\".\"labels\"");
    }

    [TestMethod]
    public void InvalidMappingsAndPrefixesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => PostgresAccessFilterTranslator.Translate(AllowAllFilter.Instance, new() { TeamId = "team_id; DROP TABLE x" }));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresAccessFilterTranslator.Translate(AllowAllFilter.Instance, new() { TenantId = "Tenant" }));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresAccessFilterTranslator.Translate(AllowAllFilter.Instance, new() { Labels = "a.b.c" }));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresAccessFilterTranslator.Translate(AllowAllFilter.Instance, parameterPrefix: "1x"));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresAccessFilterTranslator.Translate(AllowAllFilter.Instance, parameterPrefix: "x;"));
        foreach (var node in new AccessFilter[] { new HasLabelFilter(new LabelId("l")), new AttributeEqualsFilter("k", "v"), new AttributeInFilter("k", ["v"]) })
            Assert.IsInstanceOfType<PostgresAccessFilterUntranslatable>(PostgresAccessFilterTranslator.Translate(new NotFilter(node)).Value);
        Assert.IsInstanceOfType<PostgresAccessFilterUntranslatable>(PostgresAccessFilterTranslator.Translate(new TeamInFilter([new TeamId("t")]), new() { TeamId = null }).Value);
        Assert.IsInstanceOfType<PostgresAccessFilterUntranslatable>(PostgresAccessFilterTranslator.Translate(new AssigneeIsFilter(new UserId("u")), new() { AssigneeId = null }).Value);
    }
}
