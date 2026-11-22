using TicketFlow.Testing;

namespace TicketFlow.Stores.Migrations.Postgres.Tests;

[TestClass]
public sealed class PostgresMigrationRunnerTests
{
    private static PostgresMigrationCatalog Catalog(params string[] steps) =>
        new("ticketflow.synthetic", steps.Select((sql, index) => new PostgresMigration(index + 1, $"step-{index + 1}", sql)));

    [TestMethod]
    public void CatalogsAreContiguousUniqueAndChecksummed()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PostgresMigrationCatalog("x", []));
        Assert.ThrowsExactly<ArgumentException>(() => new PostgresMigrationCatalog("x", [new(2, "two", "SELECT 1")]));
        Assert.ThrowsExactly<ArgumentException>(() => new PostgresMigrationCatalog("x", [new(1, "same", "SELECT 1"), new(2, "same", "SELECT 2")]));
        Assert.ThrowsExactly<ArgumentException>(() => new PostgresMigrationCatalog("Bad Name", [new(1, "one", "SELECT 1")]));
        Assert.AreNotEqual(new PostgresMigration(1, "one", "SELECT 1").Checksum, new PostgresMigration(1, "one", "SELECT 2").Checksum);
        Assert.AreEqual(64, new PostgresMigration(1, "one", "SELECT 1").Checksum.Length);
    }

    [TestMethod]
    public async Task ApplyIsExplicitIdempotentAndAppendOnly()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_migrations_test_");
        var first = Catalog("CREATE TABLE synthetic (id integer PRIMARY KEY)");
        var runner = new PostgresMigrationRunner(database.First, first);
        Assert.AreEqual(0, (await runner.InspectAsync()).CurrentVersion);
        await using (var journal = database.First.CreateCommand("SELECT to_regclass('tf_store_migrations') IS NULL"))
            Assert.IsTrue((bool)(await journal.ExecuteScalarAsync())!, "Inspection must not create the journal.");

        await Task.WhenAll(runner.ApplyAsync(), new PostgresMigrationRunner(database.Second, first).ApplyAsync());
        Assert.AreEqual(1, (await runner.ApplyAsync()).CurrentVersion);

        var second = Catalog(first.Migrations[0].Sql, "ALTER TABLE synthetic ADD COLUMN name text");
        Assert.HasCount(1, (await new PostgresMigrationRunner(database.First, second).PlanAsync()).Pending);
        Assert.AreEqual(2, (await new PostgresMigrationRunner(database.Second, second).ApplyAsync()).CurrentVersion);

        var older = new PostgresMigrationRunner(database.First, first);
        Assert.AreEqual(MigrationProtocolFailure.UnsupportedVersion, (await Assert.ThrowsExactlyAsync<MigrationProtocolException>(() => older.ApplyAsync())).Reason);
        var edited = Catalog(first.Migrations[0].Sql + " -- edited", second.Migrations[1].Sql);
        Assert.AreEqual(MigrationProtocolFailure.ChecksumMismatch, (await Assert.ThrowsExactlyAsync<MigrationProtocolException>(() => new PostgresMigrationRunner(database.First, edited).ApplyAsync())).Reason);
    }

    [TestMethod]
    public async Task FailedStepsRollBackTogether()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_migrations_test_");
        var broken = Catalog("CREATE TABLE half (id integer)", "SELECT * FROM missing_table");
        await Assert.ThrowsExactlyAsync<Npgsql.PostgresException>(() => new PostgresMigrationRunner(database.First, broken).ApplyAsync());
        await using var exists = database.First.CreateCommand("SELECT to_regclass('half') IS NULL");
        Assert.IsTrue((bool)(await exists.ExecuteScalarAsync())!);
        Assert.AreEqual(0, (await new PostgresMigrationRunner(database.First, broken).InspectAsync()).CurrentVersion);
    }

    [TestMethod]
    public async Task BoundedKeysAreFixedSizeAndFramed()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("tf_migrations_test_");
        await new PostgresMigrationRunner(database.First, Catalog(PostgresBoundedKeys.Version1Sql)).ApplyAsync();
        await using var command = database.First.CreateCommand(
            "SELECT octet_length(tf_key_v1(@long, @long)), tf_key_v1('a', 'b') = tf_key_v1('ab'), tf_key_v1('a', 'b') = tf_key_v1('a', 'b'), tf_key_v1('a,b') = tf_key_v1('a', 'b')");
        command.Parameters.AddWithValue("long", TestScopes.LongIdentifier());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(32, reader.GetInt32(0));
        Assert.IsFalse(reader.GetBoolean(1));
        Assert.IsTrue(reader.GetBoolean(2));
        Assert.IsFalse(reader.GetBoolean(3));
    }
}
