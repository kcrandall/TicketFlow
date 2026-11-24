using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Access.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of every access store contract over an application-owned <see cref="NpgsqlDataSource"/>.
/// Tables live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is
/// parameterized; every key includes the full partition or scope; updates and deletes are compare-and-set on revision; capacity
/// checks run under a transaction-scoped advisory lock for the scope or subject. Persisted shapes use explicit numeric
/// discriminators and a <c>format_version</c> column, never union JSON.
/// </summary>
public sealed partial class PostgresAccessStore : IAccessRoleStore, IAccessGrantStore, IAccessFieldPolicyStore, IAccessAuditSink
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app";
    private const string WorkspaceWhere = ScopeWhere + " AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + WorkspaceWhere;

    private readonly NpgsqlDataSource source;

    public PostgresAccessStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Explicitly applies <see cref="PostgresAccessMigrations.Catalog"/> through <see cref="PostgresMigrationRunner"/> (advisory-locked,
    /// checksum-verified, idempotent). Fails on unknown future versions or modified history. Never called implicitly.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresAccessMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    private static void AddPartition(NpgsqlCommand command, AccessPartition partition)
    {
        AddText(command, "tenant", partition.TenantId);
        AddText(command, "project", partition.ProjectId);
        AddText(command, "environment", partition.EnvironmentId);
        AddText(command, "app", partition.AppId);
    }

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value)
    {
        if (value is null)
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value });
        else
            command.Parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });
    }

    private static void AddShort(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddNullableShort(NpgsqlCommand command, string name, short? value)
    {
        if (value is { } v)
            command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = v });
        else
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Smallint) { Value = DBNull.Value });
    }

    private static void AddInt(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddLong(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddBool(NpgsqlCommand command, string name, bool value) =>
        command.Parameters.Add(new NpgsqlParameter<bool>(name, NpgsqlDbType.Boolean) { TypedValue = value });

    private static void AddGuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid) { TypedValue = value });

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string> values) =>
        command.Parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values as string[] ?? values.ToArray() });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = instant });
        else
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
    }

    /// <summary>Serializes writers for one scope or subject within the current transaction.</summary>
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken, params string[] parts)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        AddTextArray(command, "parts", parts);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(NpgsqlCommand command, CancellationToken cancellationToken) =>
        (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

    /// <summary>Distinguishes a missed compare-and-set: not found, or conflict with the current revision.</summary>
    private static async Task<StoreWriteResult<T, AccessWriteRejection>> MissAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        bind(command);
        var current = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current is long revision ? new StoreConflict(revision) : StoreNotFound.Instance;
    }

    private static InvalidOperationException StoredDataError(Exception error, string description) =>
        new($"A stored {description} is invalid or incompatible.", error);
}
