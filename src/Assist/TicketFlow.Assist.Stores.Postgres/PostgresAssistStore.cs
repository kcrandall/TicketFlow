using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Assist.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IAssistStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables live
/// in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized and
/// every key includes the full scope. Reservations serialize per workspace on a transaction-scoped advisory lock so quota and rate
/// checks cannot be raced; settlement is a conditional update that succeeds once.
/// </summary>
public sealed class PostgresAssistStore : IAssistStore
{
    private const string WorkspaceWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + WorkspaceWhere;
    private const string UsageKeyWhere = "usage_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@usage::text) AND " + WorkspaceWhere + " AND usage_id=@usage";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeParameters = "@tenant, @project, @environment, @app, @workspace";
    private const string UsageColumns = "usage_id, format_version, feature, model, actor_kind, actor_id, conversation_id, state, reserved_tokens, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, created_at, completed_at";
    private const string CountedTokens = "CASE WHEN state = 1 THEN reserved_tokens ELSE input_tokens + output_tokens + cache_read_tokens + cache_write_tokens END";

    private readonly NpgsqlDataSource source;

    public PostgresAssistStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Explicitly applies <see cref="PostgresAssistMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresAssistMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<StoredAssistSettings?> GetSettingsAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT format_version, settings::text, revision FROM tf_assist_settings WHERE {ScopeKeyWhere}");
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(0) != 1)
                throw new FormatException("Unsupported settings format version.");
            return new StoredAssistSettings(scope, AssistJsonCodec.DecodeSettings(reader.GetString(1)), reader.GetInt64(2));
        }, "settings record");
    }

    public async Task<AssistWriteResult<StoredAssistSettings>> PutSettingsAsync(TicketScope scope, AssistSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, "settings", cancellationToken).ConfigureAwait(false);
        long current;
        await using (var select = new NpgsqlCommand($"SELECT revision FROM tf_assist_settings WHERE {ScopeKeyWhere}", connection, transaction))
        {
            AddScope(select, scope);
            current = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long? ?? 0;
        }
        if (current != expectedRevision)
            return new AssistWriteConflict(current);
        var sql = current == 0
            ? $"INSERT INTO tf_assist_settings({ScopeColumns}, format_version, settings, revision) VALUES({ScopeParameters}, 1, @settings, 1)"
            : $"UPDATE tf_assist_settings SET settings=@settings, revision=revision+1 WHERE {ScopeKeyWhere} AND revision=@expected";
        await using (var write = new NpgsqlCommand(sql, connection, transaction))
        {
            AddScope(write, scope);
            write.Parameters.Add(new NpgsqlParameter("settings", NpgsqlDbType.Jsonb) { Value = AssistJsonCodec.EncodeSettings(settings) });
            write.Parameters.AddWithValue("expected", expectedRevision);
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AssistWriteApplied<StoredAssistSettings>(new(scope, settings, current + 1));
    }

    public async Task<AssistReservationResult> ReserveAsync(TicketScope scope, AssistUsageRecord reservation, long dailyTokenLimit, int requestsPerMinute, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.State != AssistUsageState.Reserved)
            throw new ArgumentException("Reservations start in the Reserved state.", nameof(reservation));
        ArgumentOutOfRangeException.ThrowIfLessThan(dailyTokenLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);
        var dayStart = AssistLimits.DayStart(reservation.CreatedAt);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, "usage", cancellationToken).ConfigureAwait(false);
        await using (var totals = new NpgsqlCommand($"""
            SELECT COALESCE(SUM({CountedTokens}) FILTER (WHERE created_at >= @day_start AND created_at < @day_end), 0)::bigint,
                   COUNT(*) FILTER (WHERE created_at > @window_start AND created_at <= @now)
            FROM tf_assist_usage WHERE {ScopeKeyWhere} AND created_at >= LEAST(@day_start, @window_start) AND created_at < GREATEST(@day_end, @now + interval '1 microsecond')
            """, connection, transaction))
        {
            AddScope(totals, scope);
            AddTime(totals, "day_start", dayStart);
            AddTime(totals, "day_end", dayStart.AddDays(1));
            AddTime(totals, "window_start", reservation.CreatedAt - TimeSpan.FromMinutes(1));
            AddTime(totals, "now", reservation.CreatedAt);
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var tokens = reader.GetInt64(0);
            var recent = reader.GetInt64(1);
            await reader.CloseAsync().ConfigureAwait(false);
            if (tokens + reservation.ReservedTokens > dailyTokenLimit)
                return new AssistQuotaExceeded(AssistQuotaKind.DailyTokens, dayStart.AddDays(1));
            if (recent >= requestsPerMinute)
            {
                await using var oldest = new NpgsqlCommand($"""
                    SELECT created_at FROM tf_assist_usage WHERE {ScopeKeyWhere} AND created_at > @window_start AND created_at <= @now
                    ORDER BY created_at DESC OFFSET @offset LIMIT 1
                    """, connection, transaction);
                AddScope(oldest, scope);
                AddTime(oldest, "window_start", reservation.CreatedAt - TimeSpan.FromMinutes(1));
                AddTime(oldest, "now", reservation.CreatedAt);
                oldest.Parameters.AddWithValue("offset", (long)requestsPerMinute - 1);
                var at = await oldest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime time ? new DateTimeOffset(time, TimeSpan.Zero) : reservation.CreatedAt;
                return new AssistQuotaExceeded(AssistQuotaKind.RequestsPerMinute, at + TimeSpan.FromMinutes(1));
            }
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_assist_usage({ScopeColumns}, usage_id, format_version, feature, model, actor_kind, actor_id, conversation_id, state, reserved_tokens,
                input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, created_at, completed_at)
            VALUES({ScopeParameters}, @usage, 1, @feature, @model, @actor_kind, @actor_id, @conversation, 1, @reserved, 0, 0, 0, 0, @created, NULL)
            """, connection, transaction))
        {
            AddScope(insert, scope);
            insert.Parameters.AddWithValue("usage", reservation.Id);
            insert.Parameters.AddWithValue("feature", (short)reservation.Feature);
            AddText(insert, "model", reservation.Model);
            insert.Parameters.AddWithValue("actor_kind", (short)reservation.Actor.Kind);
            AddText(insert, "actor_id", reservation.Actor.Id);
            AddText(insert, "conversation", reservation.Conversation?.Value);
            insert.Parameters.AddWithValue("reserved", reservation.ReservedTokens);
            AddTime(insert, "created", reservation.CreatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AssistUsageReserved(reservation);
    }

    public async Task<AssistUsageRecord?> SettleAsync(TicketScope scope, Guid id, AssistUsageState state, AssistUsage usage, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(usage);
        if (state is not (AssistUsageState.Completed or AssistUsageState.Failed))
            throw new ArgumentOutOfRangeException(nameof(state));
        await using var command = source.CreateCommand($"""
            UPDATE tf_assist_usage SET state=@state, input_tokens=@input, output_tokens=@output, cache_read_tokens=@cache_read, cache_write_tokens=@cache_write, completed_at=@completed
            WHERE {UsageKeyWhere} AND state=1 RETURNING {UsageColumns}
            """);
        AddScope(command, scope);
        command.Parameters.AddWithValue("usage", id);
        command.Parameters.AddWithValue("state", (short)state);
        command.Parameters.AddWithValue("input", usage.InputTokens);
        command.Parameters.AddWithValue("output", usage.OutputTokens);
        command.Parameters.AddWithValue("cache_read", usage.CacheReadInputTokens);
        command.Parameters.AddWithValue("cache_write", usage.CacheCreationInputTokens);
        AddTime(command, "completed", AssistLimits.Truncate(completedAt));
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistUsageRecord?> GetUsageAsync(TicketScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {UsageColumns} FROM tf_assist_usage WHERE {UsageKeyWhere}");
        AddScope(command, scope);
        command.Parameters.AddWithValue("usage", id);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssistUsageSummary> SummarizeAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        AssistLimits.RequireRange(from, to);
        await using var command = source.CreateCommand($"""
            SELECT feature, model, COUNT(*), COUNT(*) FILTER (WHERE state = 3), COALESCE(SUM(input_tokens), 0)::bigint, COALESCE(SUM(output_tokens), 0)::bigint,
                COALESCE(SUM(cache_read_tokens), 0)::bigint, COALESCE(SUM(cache_write_tokens), 0)::bigint, COALESCE(SUM(reserved_tokens) FILTER (WHERE state = 1), 0)::bigint
            FROM tf_assist_usage WHERE {ScopeKeyWhere} AND created_at >= @from AND created_at < @to
            GROUP BY feature, model ORDER BY feature, model COLLATE "C"
            """);
        AddScope(command, scope);
        AddTime(command, "from", from);
        AddTime(command, "to", to);
        var totals = new List<AssistUsageTotal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            totals.Add(Decode(() => new AssistUsageTotal(Feature(reader.GetInt16(0)), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)), "usage total"));
        return new AssistUsageSummary(from, to, totals);
    }

    public async Task<int> PurgeUsageAsync(TicketScope scope, DateTimeOffset before, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AssistLimits.MaximumPurgeBatch);
        await using var command = source.CreateCommand($"""
            DELETE FROM tf_assist_usage WHERE usage_key IN (
                SELECT usage_key FROM tf_assist_usage WHERE {ScopeKeyWhere} AND state <> 1 AND created_at < @before ORDER BY created_at LIMIT @limit)
            """);
        AddScope(command, scope);
        AddTime(command, "before", before);
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AssistUsageRecord?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported usage format version.");
            var actorKind = (AccessActorKind)reader.GetInt16(4);
            if (!Enum.IsDefined(actorKind))
                throw new FormatException("Unknown actor kind.");
            return new AssistUsageRecord(reader.GetGuid(0), Feature(reader.GetInt16(2)), reader.GetString(3), new AccessActor(actorKind, reader.GetString(5)),
                reader.IsDBNull(6) ? null : new ConversationId(reader.GetString(6)), State(reader.GetInt16(7)), reader.GetInt64(8),
                new AssistUsage(reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12)),
                reader.GetFieldValue<DateTimeOffset>(13), reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14));
        }, "usage record");
    }

    private static AssistFeature Feature(short value) =>
        Enum.IsDefined((AssistFeature)value) ? (AssistFeature)value : throw new FormatException("Unknown assist feature.");

    private static AssistUsageState State(short value) =>
        Enum.IsDefined((AssistUsageState)value) ? (AssistUsageState)value : throw new FormatException("Unknown usage state.");

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

    private static void AddTime(NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value.ToUniversalTime() });

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, string area, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = (string[])["ticketflow.assist", area, scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value],
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException($"A stored assist {description} is invalid or incompatible.", error);
        }
    }
}
