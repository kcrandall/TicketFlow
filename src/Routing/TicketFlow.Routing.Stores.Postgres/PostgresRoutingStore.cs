using System.Collections.Concurrent;
using System.Data;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of every routing store contract over an application-owned <see cref="NpgsqlDataSource"/>. Tables
/// live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized
/// and confined to one full scope (claims and sweeps are store-wide; rows carry their scope). Rows that fail to decode throw.
/// <para>
/// Clock: by default every lease, reservation, presence and expiry instant comes from the database (<c>clock_timestamp()</c>, evaluated
/// inside the statement), so all clients compare leases against one authoritative clock regardless of host clock drift. Supplying a
/// <see cref="TimeProvider"/> switches the store to that application clock — intended for deterministic tests; clients then need
/// synchronized clocks.
/// </para>
/// </summary>
public sealed partial class PostgresRoutingStore : IRoutingPolicyStore, IAgentAvailabilityStore, IRoutingAssignmentStore, IRoutingQueueStore, IConversationPresenceStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeWhereQ = "q.tenant_id=@tenant AND q.project_id=@project AND q.environment_id=@environment AND q.app_id=@app AND q.workspace_id=@workspace";
    private const string ScopeKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace)";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeColumnsA = "a.tenant_id, a.project_id, a.environment_id, a.app_id, a.workspace_id";
    private const string ScopeValues = "@tenant, @project, @environment, @app, @workspace";
    private const string NowSql = "COALESCE(@now, clock_timestamp())";
    private const string PolicyWhere = "policy_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@policy) AND policy_id=@policy AND " + ScopeWhere;

    private const string CreatePolicySql = $"""
        WITH existing AS (
            SELECT revision FROM tf_routing_policies WHERE {PolicyWhere}
        ),
        inserted AS (
            INSERT INTO tf_routing_policies({ScopeColumns}, policy_id, format_version, document, revision)
            SELECT {ScopeValues}, @policy, 1, @document, 1
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_routing_policies WHERE scope_key={ScopeKey} AND {ScopeWhere}) < @max
            RETURNING revision
        )
        SELECT (SELECT revision FROM inserted), (SELECT revision FROM existing)
        """;

    private readonly NpgsqlDataSource source;
    private readonly TimeProvider? time;
    private readonly ConcurrentDictionary<(TicketScope Scope, string Cursor), byte> knownCursors = new();
    private readonly ConcurrentDictionary<(TicketScope Scope, UserId Agent, ChannelKind Channel), byte> knownAnchors = new();

    /// <param name="dataSource">The application-owned data source.</param>
    /// <param name="time">An application clock (tests); <see langword="null"/> (default) uses the database clock.</param>
    public PostgresRoutingStore(NpgsqlDataSource dataSource, TimeProvider? time = null)
    {
        source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.time = time;
    }

    /// <summary>True when instants come from the database clock rather than an application <see cref="TimeProvider"/>.</summary>
    public bool UsesDatabaseClock => time is null;

    /// <summary>Explicitly applies <see cref="PostgresRoutingMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresRoutingMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    private DateTimeOffset? OverrideNow() => time is not null ? Instant(time.GetUtcNow()) : null;

    // ---- Policies ----

    public async Task<StoredRoutingPolicy?> GetAsync(TicketScope scope, RoutingPolicyId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequirePolicy(id);
        await using var command = source.CreateCommand($"SELECT policy_id, format_version, document, revision FROM tf_routing_policies WHERE {PolicyWhere}");
        AddScope(command, scope);
        AddText(command, "policy", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPolicy(reader, scope) : null;
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
        var document = RoutingPolicyCodec.Serialize(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, "policies", scope, cancellationToken).ConfigureAwait(false);
        long? insertedRevision;
        long? existingRevision;
        await using (var create = new NpgsqlCommand(CreatePolicySql, connection, transaction))
        {
            AddScope(create, scope);
            AddText(create, "policy", policy.Id.Value);
            AddText(create, "document", document);
            AddLong(create, "max", IRoutingPolicyStore.MaximumPoliciesPerWorkspace);
            await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            insertedRevision = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            existingRevision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        if (insertedRevision is { } rev)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RoutingWriteApplied<StoredRoutingPolicy>(new StoredRoutingPolicy(scope, policy, rev));
        }
        return existingRevision is { } existing
            ? new RoutingWriteConflict(existing)
            : new RoutingWriteRejected(RoutingWriteRejection.CapacityExceeded);
    }

    public async Task<RoutingWriteResult<StoredRoutingPolicy>> UpdateAsync(TicketScope scope, RoutingPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        var document = RoutingPolicyCodec.Serialize(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_routing_policies SET document=@document, revision=revision+1
            WHERE {PolicyWhere} AND revision=@expected
            RETURNING revision
            """, connection))
        {
            AddScope(update, scope);
            AddText(update, "policy", policy.Id.Value);
            AddText(update, "document", document);
            AddLong(update, "expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new RoutingWriteApplied<StoredRoutingPolicy>(new StoredRoutingPolicy(scope, policy, revision));
        }
        return await LoadPolicyRevisionAsync(connection, scope, policy.Id, cancellationToken).ConfigureAwait(false) is { } current
            ? new RoutingWriteConflict(current)
            : RoutingWriteNotFound.Instance;
    }

    public async Task<RoutingWriteResult<StoredRoutingPolicy>> DeleteAsync(TicketScope scope, RoutingPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequirePolicy(id);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"""
            DELETE FROM tf_routing_policies WHERE {PolicyWhere} AND revision=@expected
            RETURNING policy_id, format_version, document, revision
            """, connection))
        {
            AddScope(delete, scope);
            AddText(delete, "policy", id.Value);
            AddLong(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new RoutingWriteApplied<StoredRoutingPolicy>(ReadPolicy(reader, scope));
        }
        return await LoadPolicyRevisionAsync(connection, scope, id, cancellationToken).ConfigureAwait(false) is { } current
            ? new RoutingWriteConflict(current)
            : RoutingWriteNotFound.Instance;
    }

    private static async Task<long?> LoadPolicyRevisionAsync(NpgsqlConnection connection, TicketScope scope, RoutingPolicyId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_routing_policies WHERE {PolicyWhere}", connection);
        AddScope(command, scope);
        AddText(command, "policy", id.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static StoredRoutingPolicy ReadPolicy(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported policy format version.");
            return new StoredRoutingPolicy(scope, RoutingPolicyCodec.Deserialize(new RoutingPolicyId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("A stored routing policy is invalid or incompatible.", error);
        }
    }

    // ---- Shared helpers ----

    private static async Task LockScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string area, TicketScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('ticketflow.routing.{area}:' || encode({ScopeKey}, 'hex'), 0))",
            connection, transaction);
        AddScope(command, scope);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TicketScope ReadScope(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetString(ordinal), reader.GetString(ordinal + 1), reader.GetString(ordinal + 2), reader.GetString(ordinal + 3), new WorkspaceId(reader.GetString(ordinal + 4)));

    private static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableInstant(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddScope(NpgsqlCommand command, TicketScope scope) => AddScope(command.Parameters, scope);

    private static void AddScope(NpgsqlParameterCollection parameters, TicketScope scope)
    {
        AddText(parameters, "tenant", scope.TenantId);
        AddText(parameters, "project", scope.ProjectId);
        AddText(parameters, "environment", scope.EnvironmentId);
        AddText(parameters, "app", scope.AppId);
        AddText(parameters, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) => AddText(command.Parameters, name, value);

    private static void AddText(NpgsqlParameterCollection parameters, string name, string? value) =>
        parameters.Add(value is null
            ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value }
            : new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });

    private static void AddTextArray(NpgsqlCommand command, string name, string[] values) => AddTextArray(command.Parameters, name, values);

    private static void AddTextArray(NpgsqlParameterCollection parameters, string name, string[] values) =>
        parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) => AddInstant(command.Parameters, name, value);

    private static void AddInstant(NpgsqlParameterCollection parameters, string name, DateTimeOffset? value) =>
        parameters.Add(value is { } instant
            ? new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = instant }
            : new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });

    private static void AddInterval(NpgsqlCommand command, string name, TimeSpan value) =>
        command.Parameters.Add(new NpgsqlParameter<TimeSpan>(name, NpgsqlDbType.Interval) { TypedValue = value });

    private static void AddShort(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddBool(NpgsqlCommand command, string name, bool value) =>
        command.Parameters.Add(new NpgsqlParameter<bool>(name, NpgsqlDbType.Boolean) { TypedValue = value });

    private static void AddLong(NpgsqlCommand command, string name, long value) => AddLong(command.Parameters, name, value);

    private static void AddLong(NpgsqlParameterCollection parameters, string name, long value) =>
        parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddInt(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

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
}
