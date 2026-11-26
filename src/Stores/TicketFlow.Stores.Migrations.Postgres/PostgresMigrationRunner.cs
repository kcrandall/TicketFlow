using Npgsql;

namespace TicketFlow.Stores.Migrations.Postgres;

/// <summary>Explicit operator-controlled migrations. Construction, inspection and planning never initialize or alter a schema.</summary>
public sealed class PostgresMigrationRunner
{
    private const long MigrationLockSeed = 582214907731;
    private readonly NpgsqlDataSource dataSource;
    private readonly PostgresMigrationCatalog catalog;
    private readonly PostgresMigrationOptions options;
    public PostgresMigrationRunner(NpgsqlDataSource dataSource, PostgresMigrationCatalog catalog, PostgresMigrationOptions? options = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.options = options ?? new();
        this.options.Validate();
    }

    public async Task<PostgresMigrationInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = Deadline(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, timeout.Token).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
        var journal = await ResolveJournalAsync(connection, transaction, timeout.Token).ConfigureAwait(false);
        var history = await ReadHistoryAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
        Validate(history);
        await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
        return new(catalog.Component, history);
    }

    public async Task<PostgresMigrationPlan> PlanAsync(int? targetVersion = null, CancellationToken cancellationToken = default) =>
        Plan(await InspectAsync(cancellationToken).ConfigureAwait(false), targetVersion ?? catalog.Migrations.Count);

    /// <summary>Fresh application or versioned upgrade. Revalidates the complete journal under a bounded database lock; all pending SQL and journal records commit together.</summary>
    public Task<PostgresMigrationInspection> ApplyAsync(int? targetVersion = null, CancellationToken cancellationToken = default) =>
        ApplyCoreAsync(targetVersion, null, cancellationToken);

    /// <summary>Explicit feature initialization. Atomically adopts a validated legacy schema when its table exists, then applies the catalog; construction and workers never call this implicitly.</summary>
    public Task<PostgresMigrationInspection> InitializeAsync(string legacyTable, CancellationToken cancellationToken = default)
    {
        MigrationIdentifiers.Validate(legacyTable, nameof(legacyTable));
        return ApplyCoreAsync(null, legacyTable, cancellationToken);
    }

    private async Task<PostgresMigrationInspection> ApplyCoreAsync(int? targetVersion, string? legacyTable, CancellationToken cancellationToken)
    {
        var target = targetVersion ?? catalog.Migrations.Count;
        ValidateTarget(target);
        using var timeout = Deadline(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
        var journal = await ResolveJournalAsync(connection, transaction, timeout.Token).ConfigureAwait(false);
        await LockAndInitializeJournalAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
        var history = await ReadHistoryAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
        Validate(history);
        if (history.Count == 0 && legacyTable is not null)
        {
            await using var exists = new NpgsqlCommand("SELECT to_regclass(format('%I.%I',@schema,@table)) IS NOT NULL", connection, transaction);
            exists.Parameters.AddWithValue("schema", journal.Schema);
            exists.Parameters.AddWithValue("table", legacyTable);
            if (await exists.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) is true)
            {
                PostgresSchemaBaseline? matching = null;
                foreach (var baseline in catalog.Baselines.OrderByDescending(item => item.Version))
                {
                    await using var validation = new NpgsqlCommand(baseline.ValidationSql, connection, transaction);
                    if (await validation.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) is not true)
                        continue;
                    matching = baseline;
                    break;
                }
                if (matching is null)
                    throw new MigrationProtocolException(MigrationProtocolFailure.BaselineMismatch);
                foreach (var migration in catalog.Migrations.Take(matching.Version))
                    await RecordAsync(connection, transaction, journal, migration, true, timeout.Token).ConfigureAwait(false);
                history = await ReadHistoryAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
            }
        }
        var plan = Plan(new(catalog.Component, history), target);
        foreach (var migration in plan.Pending)
        {
            await using var command = new NpgsqlCommand(migration.Sql, connection, transaction);
            await command.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            await RecordAsync(connection, transaction, journal, migration, false, timeout.Token).ConfigureAwait(false);
        }
        var applied = await ReadHistoryAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
        await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
        return new(catalog.Component, applied);
    }

    /// <summary>Explicit adoption of an existing initialized schema. The feature's validation must cover its schema and serialized format; existing operational rows are never rewritten by the protocol.</summary>
    public async Task<PostgresMigrationInspection> AdoptBaselineAsync(int version, CancellationToken cancellationToken = default)
    {
        ValidateTarget(version);
        var baseline = catalog.Baselines.SingleOrDefault(item => item.Version == version)
            ?? throw new MigrationProtocolException(MigrationProtocolFailure.BaselineUnavailable);
        using var timeout = Deadline(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
        var journal = await ResolveJournalAsync(connection, transaction, timeout.Token).ConfigureAwait(false);
        await LockAndInitializeJournalAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
        var history = await ReadHistoryAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
        Validate(history);
        if (history.Count > 0)
        {
            if (history[^1].Version != version || history.Any(item => !item.AdoptedBaseline))
                throw new MigrationProtocolException(MigrationProtocolFailure.AlreadyVersioned);
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return new(catalog.Component, history);
        }
        await using var validation = new NpgsqlCommand(baseline.ValidationSql, connection, transaction);
        if (await validation.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) is not true)
            throw new MigrationProtocolException(MigrationProtocolFailure.BaselineMismatch);
        foreach (var migration in catalog.Migrations.Take(version))
            await RecordAsync(connection, transaction, journal, migration, true, timeout.Token).ConfigureAwait(false);
        var adopted = await ReadHistoryAsync(connection, transaction, journal, timeout.Token).ConfigureAwait(false);
        await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
        return new(catalog.Component, adopted);
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OperationTimeout);
        return timeout;
    }
    private sealed record MigrationJournal(string Schema, string Table);
    private static async Task<MigrationJournal> ResolveJournalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT current_schema()", connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string schema)
            throw new InvalidOperationException("PostgreSQL migrations require a current schema.");
        using var identifiers = new NpgsqlCommandBuilder();
        return new(schema, identifiers.QuoteIdentifier(schema) + ".tf_store_migrations");
    }
    private async Task LockAndInitializeJournalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MigrationJournal journal, CancellationToken cancellationToken)
    {
        await using var settings = new NpgsqlCommand("SELECT set_config('lock_timeout',@lock_timeout,true),set_config('statement_timeout',@statement_timeout,true)", connection, transaction);
        settings.Parameters.AddWithValue("lock_timeout", $"{Math.Max(1, (long)options.LockTimeout.TotalMilliseconds)}ms");
        settings.Parameters.AddWithValue("statement_timeout", $"{Math.Max(1, (long)options.OperationTimeout.TotalMilliseconds)}ms");
        await settings.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var initialize = new NpgsqlCommand($"""
            SELECT pg_advisory_xact_lock(hashtextextended(@schema,@seed));
            CREATE TABLE IF NOT EXISTS {journal.Table} (
                component text COLLATE "C" NOT NULL,
                version integer NOT NULL CHECK(version>0),
                migration_id text COLLATE "C" NOT NULL,
                checksum text NOT NULL CHECK(length(checksum)=64),
                adopted_baseline boolean NOT NULL,
                applied_at_utc timestamptz NOT NULL DEFAULT clock_timestamp(),
                PRIMARY KEY(component,version));
            """, connection, transaction);
        initialize.Parameters.AddWithValue("schema", journal.Schema);
        initialize.Parameters.AddWithValue("seed", MigrationLockSeed);
        await initialize.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private async Task<IReadOnlyList<AppliedPostgresMigration>> ReadHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MigrationJournal journal, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@journal) IS NOT NULL", connection, transaction);
        exists.Parameters.AddWithValue("journal", journal.Table);
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            return Array.Empty<AppliedPostgresMigration>();
        await using var command = new NpgsqlCommand($"SELECT version,migration_id,checksum,adopted_baseline,applied_at_utc FROM {journal.Table} WHERE component=@component ORDER BY version LIMIT 1001", connection, transaction);
        command.Parameters.AddWithValue("component", catalog.Component);
        var result = new List<AppliedPostgresMigration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken).ConfigureAwait(false)));
        return Array.AsReadOnly(result.ToArray());
    }
    private void Validate(IReadOnlyList<AppliedPostgresMigration> history)
    {
        for (var index = 0; index < history.Count; index++)
        {
            var applied = history[index];
            if (applied.Version != index + 1)
                throw new MigrationProtocolException(MigrationProtocolFailure.InvalidHistory);
            if (applied.Version > catalog.Migrations.Count)
                throw new MigrationProtocolException(MigrationProtocolFailure.UnsupportedVersion);
            var expected = catalog.Migrations[index];
            if (!string.Equals(applied.Id, expected.Id, StringComparison.Ordinal) || !string.Equals(applied.Checksum, expected.Checksum, StringComparison.Ordinal))
                throw new MigrationProtocolException(MigrationProtocolFailure.ChecksumMismatch);
        }
    }
    private void ValidateTarget(int version)
    {
        if (version < 1 || version > catalog.Migrations.Count)
            throw new MigrationProtocolException(MigrationProtocolFailure.UnsupportedVersion);
    }
    private PostgresMigrationPlan Plan(PostgresMigrationInspection current, int target)
    {
        ValidateTarget(target);
        if (current.CurrentVersion > target)
            throw new MigrationProtocolException(MigrationProtocolFailure.Downgrade);
        return new(catalog.Component, current.CurrentVersion, target, Array.AsReadOnly(catalog.Migrations.Skip(current.CurrentVersion).Take(target - current.CurrentVersion).ToArray()));
    }
    private async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, MigrationJournal journal, PostgresMigration migration, bool baseline, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {journal.Table}(component,version,migration_id,checksum,adopted_baseline) VALUES(@component,@version,@id,@checksum,@baseline)", connection, transaction);
        command.Parameters.AddWithValue("component", catalog.Component); command.Parameters.AddWithValue("version", migration.Version);
        command.Parameters.AddWithValue("id", migration.Id); command.Parameters.AddWithValue("checksum", migration.Checksum); command.Parameters.AddWithValue("baseline", baseline);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
