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
        command.Parameters.AddWithValue("tenant", partition.TenantId);
        command.Parameters.AddWithValue("project", partition.ProjectId);
        command.Parameters.AddWithValue("environment", partition.EnvironmentId);
        command.Parameters.AddWithValue("app", partition.AppId);
    }

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        command.Parameters.AddWithValue("tenant", scope.TenantId);
        command.Parameters.AddWithValue("project", scope.ProjectId);
        command.Parameters.AddWithValue("environment", scope.EnvironmentId);
        command.Parameters.AddWithValue("app", scope.AppId);
        command.Parameters.AddWithValue("workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value ?? DBNull.Value });

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
    private static async Task<AccessWriteResult<T>> MissAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        bind(command);
        var current = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current is long revision ? new AccessWriteConflict(revision) : AccessWriteNotFound.Instance;
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException($"A stored {description} is invalid or incompatible.", error);
        }
    }
}
