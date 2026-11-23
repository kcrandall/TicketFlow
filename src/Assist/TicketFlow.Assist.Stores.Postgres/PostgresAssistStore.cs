using System.Data;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Assist.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IAssistStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables live
/// in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized and
/// every key includes the full scope. Reservations serialize per workspace on a transaction-scoped advisory lock; the daily quota is a
/// conditional UPSERT on a per-workspace, per-UTC-day counter row (<c>tokens = tokens + n</c> only while within the limit) and the
/// per-minute limit reads at most <c>limit</c> rows of the last minute, so no check scans a day of usage. Settlement is a
/// conditional update that succeeds once and adjusts the counter by the difference between actual and reserved tokens in the same
/// transaction; sweeps settle abandoned reservations at their estimate (counter unchanged).
/// </summary>
public sealed class PostgresAssistStore : IAssistStore
{
    private const string WorkspaceWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + WorkspaceWhere;
    private const string UsageKeyWhere = "usage_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@usage::text) AND " + WorkspaceWhere + " AND usage_id=@usage";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeParameters = "@tenant, @project, @environment, @app, @workspace";
    private const string UsageColumns = "usage_id, format_version, feature, model, actor_kind, actor_id, conversation_id, state, reserved_tokens, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, created_at, completed_at, estimated";
    private const string DayKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace,(@day::date - DATE '1970-01-01')::text)";

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
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
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
            return new StoreConflict(current);
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
        return new StoreApplied<StoredAssistSettings>(new(scope, settings, current + 1));
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
        // Serializes reservations of one workspace so the per-minute window and the day counter are checked together.
        await LockAsync(connection, transaction, scope, "usage", cancellationToken).ConfigureAwait(false);
        // Fetch only the Nth newest timestamp in the last minute (1 row instead of streaming up to requestsPerMinute rows).
        await using (var recent = new NpgsqlCommand($"""
            SELECT created_at FROM tf_assist_usage WHERE {ScopeKeyWhere} AND created_at > @window_start AND created_at <= @now
            ORDER BY created_at DESC OFFSET @offset LIMIT 1
            """, connection, transaction))
        {
            AddScope(recent, scope);
            AddTime(recent, "window_start", reservation.CreatedAt - TimeSpan.FromMinutes(1));
            AddTime(recent, "now", reservation.CreatedAt);
            recent.Parameters.Add(new NpgsqlParameter<int>("offset", NpgsqlDbType.Integer) { TypedValue = requestsPerMinute - 1 });
            await using var reader = await recent.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new AssistQuotaExceeded(AssistQuotaKind.RequestsPerMinute, reader.GetFieldValue<DateTimeOffset>(0) + TimeSpan.FromMinutes(1));
        }
        if (reservation.ReservedTokens > dailyTokenLimit)
            return new AssistQuotaExceeded(AssistQuotaKind.DailyTokens, dayStart.AddDays(1));
        // Combine daily counter upsert + usage record insert in one writable CTE.
        await using (var reserve = new NpgsqlCommand($"""
            WITH bumped AS (
                INSERT INTO tf_assist_usage_days({ScopeColumns}, day, tokens) VALUES({ScopeParameters}, @day, @tokens)
                ON CONFLICT (day_key) DO UPDATE SET tokens = tf_assist_usage_days.tokens + EXCLUDED.tokens
                WHERE tf_assist_usage_days.tokens + EXCLUDED.tokens <= @limit
                RETURNING tokens
            ),
            recorded AS (
                INSERT INTO tf_assist_usage({ScopeColumns}, usage_id, format_version, feature, model, actor_kind, actor_id, conversation_id, state, reserved_tokens,
                    input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, created_at, completed_at)
                SELECT {ScopeParameters}, @usage, 1, @feature, @model, @actor_kind, @actor_id, @conversation, 1, @reserved, 0, 0, 0, 0, @created, NULL
                FROM bumped
            )
            SELECT tokens FROM bumped
            """, connection, transaction))
        {
            AddScope(reserve, scope);
            AddDay(reserve, dayStart);
            reserve.Parameters.Add(new NpgsqlParameter<long>("tokens", NpgsqlDbType.Bigint) { TypedValue = reservation.ReservedTokens });
            reserve.Parameters.Add(new NpgsqlParameter<long>("limit", NpgsqlDbType.Bigint) { TypedValue = dailyTokenLimit });
            reserve.Parameters.Add(new NpgsqlParameter<Guid>("usage", NpgsqlDbType.Uuid) { TypedValue = reservation.Id });
            reserve.Parameters.Add(new NpgsqlParameter<short>("feature", NpgsqlDbType.Smallint) { TypedValue = (short)reservation.Feature });
            AddText(reserve, "model", reservation.Model);
            reserve.Parameters.Add(new NpgsqlParameter<short>("actor_kind", NpgsqlDbType.Smallint) { TypedValue = (short)reservation.Actor.Kind });
            AddText(reserve, "actor_id", reservation.Actor.Id);
            AddText(reserve, "conversation", reservation.Conversation?.Value);
            reserve.Parameters.Add(new NpgsqlParameter<long>("reserved", NpgsqlDbType.Bigint) { TypedValue = reservation.ReservedTokens });
            AddTime(reserve, "created", reservation.CreatedAt);
            if (await reserve.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
                return new AssistQuotaExceeded(AssistQuotaKind.DailyTokens, dayStart.AddDays(1));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AssistUsageReserved(reservation);
    }

    public async Task<AssistUsageRecord?> SettleAsync(TicketScope scope, Guid id, AssistUsageState state, AssistUsage usage, DateTimeOffset completedAt, bool estimated = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(usage);
        if (state is not (AssistUsageState.Completed or AssistUsageState.Failed))
            throw new ArgumentOutOfRangeException(nameof(state));
        // Single auto-committed writable CTE: update usage row and adjust the daily counter when actual != reserved.
        var sql = estimated
            ? $"""
              UPDATE tf_assist_usage SET state=@state, input_tokens=@input, output_tokens=@output, cache_read_tokens=@cache_read, cache_write_tokens=@cache_write,
                  completed_at=@completed, estimated=true
              WHERE {UsageKeyWhere} AND state=1 RETURNING {UsageColumns}
              """
            : $"""
              WITH settled AS (
                  UPDATE tf_assist_usage SET state=@state, input_tokens=@input, output_tokens=@output, cache_read_tokens=@cache_read, cache_write_tokens=@cache_write,
                      completed_at=@completed, estimated=false
                  WHERE {UsageKeyWhere} AND state=1 RETURNING {UsageColumns}
              ),
              adjusted AS (
                  UPDATE tf_assist_usage_days d
                  SET tokens = GREATEST(0, d.tokens + (@actual - s.reserved_tokens))
                  FROM settled s
                  WHERE s.reserved_tokens <> @actual
                    AND d.day_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace,((s.created_at AT TIME ZONE 'UTC')::date - DATE '1970-01-01')::text)
                    AND d.tenant_id=@tenant AND d.project_id=@project AND d.environment_id=@environment AND d.app_id=@app AND d.workspace_id=@workspace
                    AND d.day = (s.created_at AT TIME ZONE 'UTC')::date
              )
              SELECT {UsageColumns} FROM settled
              """;
        await using var command = source.CreateCommand(sql);
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter<Guid>("usage", NpgsqlDbType.Uuid) { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<short>("state", NpgsqlDbType.Smallint) { TypedValue = (short)state });
        command.Parameters.Add(new NpgsqlParameter<long>("input", NpgsqlDbType.Bigint) { TypedValue = usage.InputTokens });
        command.Parameters.Add(new NpgsqlParameter<long>("output", NpgsqlDbType.Bigint) { TypedValue = usage.OutputTokens });
        command.Parameters.Add(new NpgsqlParameter<long>("cache_read", NpgsqlDbType.Bigint) { TypedValue = usage.CacheReadInputTokens });
        command.Parameters.Add(new NpgsqlParameter<long>("cache_write", NpgsqlDbType.Bigint) { TypedValue = usage.CacheCreationInputTokens });
        if (!estimated)
            command.Parameters.Add(new NpgsqlParameter<long>("actual", NpgsqlDbType.Bigint) { TypedValue = usage.TotalTokens });
        AddTime(command, "completed", AssistLimits.Truncate(completedAt));
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> SweepReservationsAsync(DateTimeOffset createdBefore, DateTimeOffset completedAt, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AssistLimits.MaximumPurgeBatch);
        // Estimated settlement keeps the counted tokens equal to the reservation, so the day counters do not change.
        await using var command = source.CreateCommand("""
            WITH stale AS MATERIALIZED (
                SELECT usage_key FROM tf_assist_usage WHERE state = 1 AND created_at < @before ORDER BY created_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_assist_usage AS usage SET state=3, estimated=true, completed_at=@completed
            FROM stale WHERE usage.usage_key = stale.usage_key AND usage.state = 1
            """);
        AddTime(command, "before", createdBefore);
        AddTime(command, "completed", AssistLimits.Truncate(completedAt));
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
                COALESCE(SUM(cache_read_tokens), 0)::bigint, COALESCE(SUM(cache_write_tokens), 0)::bigint, COALESCE(SUM(reserved_tokens) FILTER (WHERE state = 1), 0)::bigint,
                COALESCE(SUM(reserved_tokens) FILTER (WHERE estimated), 0)::bigint
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
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9)), "usage total"));
        return new AssistUsageSummary(from, to, totals);
    }

    public async Task<int> PurgeUsageAsync(TicketScope scope, DateTimeOffset before, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AssistLimits.MaximumPurgeBatch);
        await using var command = source.CreateCommand($"""
            WITH doomed AS MATERIALIZED (
                SELECT usage_key FROM tf_assist_usage WHERE {ScopeKeyWhere} AND state <> 1 AND created_at < @before ORDER BY created_at LIMIT @limit)
            DELETE FROM tf_assist_usage AS usage USING doomed WHERE usage.usage_key = doomed.usage_key
            """);
        AddScope(command, scope);
        AddTime(command, "before", before);
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AssistUsageRecord?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
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
                reader.GetFieldValue<DateTimeOffset>(13), reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14), reader.GetBoolean(15));
        }, "usage record");
    }

    private static void AddDay(NpgsqlCommand command, DateTimeOffset dayStart) =>
        command.Parameters.Add(new NpgsqlParameter<DateOnly>("day", NpgsqlDbType.Date) { TypedValue = DateOnly.FromDateTime(dayStart.UtcDateTime) });

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
