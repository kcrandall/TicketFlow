using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of every routing store contract over an application-owned <see cref="NpgsqlDataSource"/>. Tables
/// live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized
/// and confined to one full scope (claims and sweeps are store-wide; rows carry their scope). Rows that fail to decode throw. Lease,
/// presence and expiry times come from the supplied <see cref="TimeProvider"/>, so every client must use a synchronized clock.
/// </summary>
public sealed partial class PostgresRoutingStore : IRoutingPolicyStore, IAgentAvailabilityStore, IRoutingAssignmentStore, IRoutingQueueStore, IConversationPresenceStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace)";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeValues = "@tenant, @project, @environment, @app, @workspace";

    private readonly NpgsqlDataSource source;
    private readonly TimeProvider time;

    public PostgresRoutingStore(NpgsqlDataSource dataSource, TimeProvider? time = null)
    {
        source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Explicitly applies <see cref="PostgresRoutingMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresRoutingMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    private DateTimeOffset Now => Instant(time.GetUtcNow());

    // ---- Policies ----

    public async Task<StoredRoutingPolicy?> GetAsync(TicketScope scope, RoutingPolicyId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequirePolicy(id);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadPolicyAsync(connection, null, scope, id, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredRoutingPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT policy_id, format_version, document, revision FROM tf_routing_policies
            WHERE scope_key={ScopeKey} AND {ScopeWhere} ORDER BY policy_id LIMIT {IRoutingPolicyStore.MaximumPoliciesPerWorkspace}
            """);
        AddScope(command, scope);
        var policies = new List<StoredRoutingPolicy>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            policies.Add(ReadPolicy(reader, scope));
        return policies;
    }

    public async Task<RoutingWriteResult<StoredRoutingPolicy>> CreateAsync(TicketScope scope, RoutingPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, "policies", scope, cancellationToken).ConfigureAwait(false);
        if (await LoadPolicyAsync(connection, transaction, scope, policy.Id, true, cancellationToken).ConfigureAwait(false) is { } existing)
            return new RoutingWriteConflict(existing.Revision);
        if (await CountAsync(connection, transaction, "tf_routing_policies", scope, cancellationToken).ConfigureAwait(false) >= IRoutingPolicyStore.MaximumPoliciesPerWorkspace)
            return new RoutingWriteRejected(RoutingWriteRejection.CapacityExceeded);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_routing_policies({ScopeColumns}, policy_id, format_version, document, revision)
            VALUES({ScopeValues}, @policy, 1, @document, 1)
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "policy", policy.Id.Value);
            AddText(insert, "document", RoutingPolicyCodec.Serialize(policy));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoutingWriteApplied<StoredRoutingPolicy>(new StoredRoutingPolicy(scope, policy, 1));
    }

    public async Task<RoutingWriteResult<StoredRoutingPolicy>> UpdateAsync(TicketScope scope, RoutingPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await LoadPolicyAsync(connection, transaction, scope, policy.Id, true, cancellationToken).ConfigureAwait(false) is not { } existing)
            return RoutingWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new RoutingWriteConflict(existing.Revision);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_routing_policies SET document=@document, revision=revision+1
            WHERE policy_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@policy) AND policy_id=@policy AND {ScopeWhere}
            """, connection, transaction))
        {
            AddScope(update, scope);
            AddText(update, "policy", policy.Id.Value);
            AddText(update, "document", RoutingPolicyCodec.Serialize(policy));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoutingWriteApplied<StoredRoutingPolicy>(new StoredRoutingPolicy(scope, policy, existing.Revision + 1));
    }

    public async Task<RoutingWriteResult<StoredRoutingPolicy>> DeleteAsync(TicketScope scope, RoutingPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequirePolicy(id);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await LoadPolicyAsync(connection, transaction, scope, id, true, cancellationToken).ConfigureAwait(false) is not { } existing)
            return RoutingWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new RoutingWriteConflict(existing.Revision);
        await using (var delete = new NpgsqlCommand($"""
            DELETE FROM tf_routing_policies WHERE policy_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@policy) AND policy_id=@policy AND {ScopeWhere}
            """, connection, transaction))
        {
            AddScope(delete, scope);
            AddText(delete, "policy", id.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoutingWriteApplied<StoredRoutingPolicy>(existing);
    }

    private static async Task<StoredRoutingPolicy?> LoadPolicyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, RoutingPolicyId id,
        bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT policy_id, format_version, document, revision FROM tf_routing_policies
            WHERE policy_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@policy) AND policy_id=@policy AND {ScopeWhere}{(forUpdate ? " FOR UPDATE" : "")}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "policy", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPolicy(reader, scope) : null;
    }

    private static StoredRoutingPolicy ReadPolicy(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported policy format version.");
        return new StoredRoutingPolicy(scope, RoutingPolicyCodec.Deserialize(new RoutingPolicyId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
    }, "policy");

    // ---- Shared helpers ----

    private static async Task LockScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string area, TicketScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('ticketflow.routing.{area}:' || encode({ScopeKey}, 'hex'), 0))",
            connection, transaction);
        AddScope(command, scope);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, TicketScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE scope_key={ScopeKey} AND {ScopeWhere}", connection, transaction);
        AddScope(command, scope);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static TicketScope ReadScope(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetString(ordinal), reader.GetString(ordinal + 1), reader.GetString(ordinal + 2), reader.GetString(ordinal + 3), new WorkspaceId(reader.GetString(ordinal + 4)));

    private static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableInstant(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value ?? DBNull.Value });

    private static void AddLong(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = value });

    private static void AddInt(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = value });

    private static void RequirePolicy(RoutingPolicyId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A routing policy identifier is required.", nameof(id));
    }

    private static void RequireAgent(UserId agent)
    {
        if (agent == default)
            throw new ArgumentException("An agent identifier is required.", nameof(agent));
    }

    private static void RequireConversation(ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
    }

    private static void RequireTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(ttl), "Durations are positive and at most 24 hours.");
    }

    private static void RequireLease(TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > IRoutingQueueStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most ten minutes.");
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException($"A stored routing {description} is invalid or incompatible.", error);
        }
    }
}
