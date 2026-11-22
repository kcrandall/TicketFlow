using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Reporting.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IReportingStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables
/// live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized and
/// confined to one full scope (retention deletes are store-wide). A projection is one transaction: the revision-fenced state row, the
/// facts inserted with <c>ON CONFLICT DO NOTHING RETURNING</c> and one rollup upsert for exactly the inserted facts, ordered by cell key
/// so concurrent projections never deadlock. Projections hold a shared and rebuilds an exclusive scope advisory lock. Aggregates,
/// backlog counts and fact lists apply the translated <see cref="AccessFilter"/> in the query; untranslatable filters return no rows.
/// Rows that fail to decode throw.
/// </summary>
public sealed class PostgresReportingStore : IReportingStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + ScopeWhere;
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeParameters = "@tenant, @project, @environment, @app, @workspace";
    private const string ConversationWhere = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;
    private const string StateColumns = "conversation_id, format_version, inbox_id, channel, created_at, status, assignee_id, team_id, priority, labels, waiting_since, first_response_at, resolutions, last_sequence, revision";
    private const string FactColumns = "source_id, metric, conversation_id, occurred_at, inbox_id, team_id, agent_id, channel, priority, labels, value, format_version";
    private const long UnixEpochTicks = 621_355_968_000_000_000;

    private static readonly PostgresAccessFilterColumns RollupColumns = Columns("r.agent_id", "r.no_labels");
    private static readonly PostgresAccessFilterColumns FactFilterColumns = Columns("r.agent_id", "r.labels");
    private static readonly PostgresAccessFilterColumns StateFilterColumns = Columns("r.assignee_id", "r.labels");

    private readonly NpgsqlDataSource source;

    public PostgresReportingStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Explicitly applies <see cref="PostgresReportingMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresReportingMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<StoredReportConversation?> GetConversationAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(conversation);
        await using var command = source.CreateCommand($"SELECT {StateColumns} FROM tf_report_conversations WHERE {ConversationWhere}");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadState(reader, scope) : null;
    }

    public async Task<ReportWriteResult> ApplyAsync(TicketScope scope, ReportProjection projection, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(projection);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var state = projection.State;
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scope, shared: true, cancellationToken).ConfigureAwait(false);

        long? revision;
        var write = expectedRevision is null
            ? $"""
              INSERT INTO tf_report_conversations({ScopeColumns}, conversation_id, format_version, inbox_id, channel, created_at, status, assignee_id, team_id, priority, labels,
                  waiting_since, first_response_at, resolutions, last_sequence, revision)
              VALUES({ScopeParameters}, @conversation, 1, @inbox, @channel, @created, @status, @assignee, @team, @priority, @labels, @waiting, @first, @resolutions, @sequence, 1)
              ON CONFLICT (conversation_key) DO NOTHING RETURNING revision
              """
            : $"""
              UPDATE tf_report_conversations SET inbox_id=@inbox, channel=@channel, created_at=@created, status=@status, assignee_id=@assignee, team_id=@team, priority=@priority,
                  labels=@labels, waiting_since=@waiting, first_response_at=@first, resolutions=@resolutions, last_sequence=@sequence, revision=revision+1
              WHERE {ConversationWhere} AND revision=@expected RETURNING revision
              """;
        await using (var command = new NpgsqlCommand(write, connection, transaction))
        {
            BindState(command, scope, state);
            if (expectedRevision is { } expected)
                command.Parameters.AddWithValue("expected", expected);
            revision = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }
        if (revision is null)
            return new ReportWriteConflict(await RevisionAsync(connection, transaction, scope, state.Conversation, cancellationToken).ConfigureAwait(false));

        var inserted = await InsertFactsAsync(connection, transaction, scope, projection.Facts, cancellationToken).ConfigureAwait(false);
        await UpsertRollupsAsync(connection, transaction, scope, inserted, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReportWriteApplied(new StoredReportConversation(scope, state, revision.Value), inserted.Count);
    }

    public async Task<ReportWriteResult> DeleteConversationAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(conversation);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        StoredReportConversation? removed = null;
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_report_conversations WHERE {ConversationWhere} AND revision=@expected RETURNING {StateColumns}", connection, transaction))
        {
            AddScope(command, scope);
            AddText(command, "conversation", conversation.Value);
            command.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                removed = ReadState(reader, scope);
        }
        if (removed is null)
            return new ReportWriteConflict(await RevisionAsync(connection, transaction, scope, conversation, cancellationToken).ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReportWriteApplied(removed, 0);
    }

    public async Task<ReportAggregateResult> AggregateAsync(TicketScope scope, ReportAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var access = PostgresAccessFilterTranslator.Translate(filter, RollupColumns).OrDenyAll();
        if (access.Sql == "FALSE")
            return new ReportAggregateResult([], false);
        var group = query.GroupBy switch
        {
            ReportDimension.None => "NULL::text",
            ReportDimension.Inbox => "r.inbox_id",
            ReportDimension.Team => "r.team_id",
            ReportDimension.Agent => "r.agent_id",
            ReportDimension.Channel => "r.channel",
            ReportDimension.Priority => "r.priority::text",
            ReportDimension.Label => "r.label",
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        var bin = query.Histograms ? "CASE WHEN r.metric = ANY(@durations) THEN r.bin ELSE 0 END" : "0";
        var where = new StringBuilder(query.UsesLabelRows ? " AND t.label <> ''" : " AND t.label = ''");
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        AddFilter(command, where, query.Filter, "t.agent_id", labelColumn: "t.label = @f_label");
        command.CommandText = $"""
            SELECT width_bucket(r.cell_us, @bounds) - 1 AS bucket, {group} AS grp, r.metric, {bin} AS bin,
                sum(r.count)::bigint, sum(r.total)::bigint, min(r.minimum), max(r.maximum)
            FROM (SELECT t.*, NULL::text[] AS no_labels, NULL::jsonb AS no_attributes FROM tf_report_rollups t
                WHERE t.{ScopeKeyWhere.Replace(" AND ", " AND t.", StringComparison.Ordinal)} AND t.metric = ANY(@metrics) AND t.cell_us >= @from AND t.cell_us < @to{where}) r
            WHERE {access.Sql}
            GROUP BY 1, 2, 3, 4
            ORDER BY 1, ({group}) COLLATE "C" NULLS FIRST, 3, 4
            LIMIT {ReportAggregateQuery.MaximumRows + 1}
            """;
        foreach (var parameter in access.Parameters)
            command.Parameters.Add(parameter);
        command.Parameters.Add(new NpgsqlParameter("bounds", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = query.Boundaries.Select(Micro).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("metrics", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = query.Metrics.Select(metric => (short)metric).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("durations", NpgsqlDbType.Array | NpgsqlDbType.Smallint)
            { Value = ReportMetrics.All.Where(ReportMetrics.IsDuration).Select(metric => (short)metric).ToArray() });
        command.Parameters.AddWithValue("from", Micro(query.From));
        command.Parameters.AddWithValue("to", Micro(query.To));
        var rows = new List<ReportAggregateRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(Decode(() => new ReportAggregateRow(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), Metric(reader.GetInt16(2)), reader.GetInt32(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)), "rollup aggregate"));
        var truncated = rows.Count > ReportAggregateQuery.MaximumRows;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);
        return new ReportAggregateResult(rows, truncated);
    }

    public async Task<long> CountBacklogAsync(TicketScope scope, ReportFilter query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var access = PostgresAccessFilterTranslator.Translate(filter, StateFilterColumns).OrDenyAll();
        if (access.Sql == "FALSE")
            return 0;
        var where = new StringBuilder();
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        AddFilter(command, where, query, "t.assignee_id", labelColumn: "@f_label = ANY(t.labels)");
        command.CommandText = $"""
            SELECT count(*) FROM (SELECT t.*, NULL::jsonb AS no_attributes FROM tf_report_conversations t
                WHERE t.{ScopeKeyWhere.Replace(" AND ", " AND t.", StringComparison.Ordinal)} AND t.status NOT IN (4, 5){where}) r
            WHERE {access.Sql}
            """;
        foreach (var parameter in access.Parameters)
            command.Parameters.Add(parameter);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<ReportFactPage> ListFactsAsync(TicketScope scope, ReportFactQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var access = PostgresAccessFilterTranslator.Translate(filter, FactFilterColumns).OrDenyAll();
        if (access.Sql == "FALSE")
            return new ReportFactPage([], null);
        var where = new StringBuilder();
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        AddFilter(command, where, query.Filter, "t.agent_id", labelColumn: "@f_label = ANY(t.labels)");
        if (query.After is { } after)
        {
            where.Append(" AND (t.occurred_at, t.source_id, t.metric) > (@after_at, @after_source, @after_metric)");
            AddTime(command, "after_at", after.OccurredAt);
            command.Parameters.Add(new NpgsqlParameter("after_source", NpgsqlDbType.Uuid) { Value = after.SourceId });
            command.Parameters.AddWithValue("after_metric", (short)after.Metric);
        }
        command.CommandText = $"""
            SELECT {string.Join(", ", FactColumns.Split(", ").Select(column => "r." + column))}
            FROM (SELECT t.*, NULL::jsonb AS no_attributes FROM tf_report_facts t
                WHERE t.{ScopeKeyWhere.Replace(" AND ", " AND t.", StringComparison.Ordinal)} AND t.occurred_at >= @from AND t.occurred_at < @to{where}) r
            WHERE {access.Sql}
            ORDER BY r.occurred_at, r.source_id, r.metric
            LIMIT {query.Limit + 1}
            """;
        foreach (var parameter in access.Parameters)
            command.Parameters.Add(parameter);
        AddTime(command, "from", query.From);
        AddTime(command, "to", query.To);
        var facts = new List<ReportFact>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                facts.Add(ReadFact(reader));
        var more = facts.Count > query.Limit;
        if (more)
            facts.RemoveAt(facts.Count - 1);
        return new ReportFactPage(facts, more ? new ReportFactCursor(facts[^1].OccurredAt, facts[^1].SourceId, facts[^1].Metric) : null);
    }

    public async Task<int> PruneFactsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        RequireLimit(limit);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_report_facts WHERE fact_key IN (
                SELECT fact_key FROM tf_report_facts WHERE occurred_at < @cutoff ORDER BY occurred_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            """);
        AddTime(command, "cutoff", olderThan);
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneRollupsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        RequireLimit(limit);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_report_rollups WHERE cell_key IN (
                SELECT cell_key FROM tf_report_rollups WHERE cell_us < @cutoff ORDER BY cell_us LIMIT @limit FOR UPDATE SKIP LOCKED)
            """);
        command.Parameters.AddWithValue("cutoff", Micro(olderThan));
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> RebuildRollupsAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ReportStoreLimits.RequireRebuildRange(from, to);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Exclusive against projections of the scope, so no fact can commit between the delete and the recount.
        await LockScopeAsync(connection, transaction, scope, shared: false, cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_report_rollups WHERE {ScopeKeyWhere} AND cell_us >= @from AND cell_us < @to", connection, transaction))
        {
            AddScope(delete, scope);
            delete.Parameters.AddWithValue("from", Micro(from));
            delete.Parameters.AddWithValue("to", Micro(to));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_report_rollups({ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, format_version)
            SELECT {ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, l.label, bin, count(*), sum(value)::bigint, min(value), max(value), 1
            FROM tf_report_facts f CROSS JOIN LATERAL (SELECT ''::text AS label UNION ALL SELECT unnest(f.labels)) l
            WHERE f.{ScopeKeyWhere.Replace(" AND ", " AND f.", StringComparison.Ordinal)} AND f.cell_us >= @from AND f.cell_us < @to
            GROUP BY {ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, l.label, bin
            """, connection, transaction))
        {
            AddScope(insert, scope);
            insert.Parameters.AddWithValue("from", Micro(from));
            insert.Parameters.AddWithValue("to", Micro(to));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long counted;
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_report_facts WHERE {ScopeKeyWhere} AND cell_us >= @from AND cell_us < @to", connection, transaction))
        {
            AddScope(count, scope);
            count.Parameters.AddWithValue("from", Micro(from));
            count.Parameters.AddWithValue("to", Micro(to));
            counted = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counted;
    }

    // ---------------------------------------------------------------- projection writes

    private static async Task<List<ReportFact>> InsertFactsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, IReadOnlyList<ReportFact> facts,
        CancellationToken cancellationToken)
    {
        var distinct = facts.DistinctBy(fact => (fact.SourceId, fact.Metric)).ToList();
        if (distinct.Count == 0)
            return [];
        await using var command = new NpgsqlCommand($"""
            INSERT INTO tf_report_facts({ScopeColumns}, source_id, metric, conversation_id, occurred_at, cell_us, inbox_id, team_id, agent_id, channel, priority, labels, value, bin, format_version)
            SELECT {ScopeParameters}, f.source_id, f.metric, f.conversation_id, f.occurred_at, f.cell_us, f.inbox_id, f.team_id, f.agent_id, f.channel, f.priority,
                ARRAY(SELECT jsonb_array_elements_text(f.labels)), f.value, f.bin, 1
            FROM unnest(@sources, @metrics, @conversations, @occurred, @cells, @inboxes, @teams, @agents, @channels, @priorities, @labels, @values, @bins)
                AS f(source_id, metric, conversation_id, occurred_at, cell_us, inbox_id, team_id, agent_id, channel, priority, labels, value, bin)
            ON CONFLICT (fact_key) DO NOTHING
            RETURNING source_id, metric
            """, connection, transaction);
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter("sources", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = distinct.Select(fact => fact.SourceId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("metrics", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = distinct.Select(fact => (short)fact.Metric).ToArray() });
        AddTextArray(command, "conversations", distinct.Select(fact => fact.Conversation.Value));
        command.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = distinct.Select(fact => fact.OccurredAt).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("cells", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = distinct.Select(fact => Micro(fact.CellStart)).ToArray() });
        AddTextArray(command, "inboxes", distinct.Select(fact => fact.Dimensions.Inbox.Value));
        AddTextArray(command, "teams", distinct.Select(fact => fact.Dimensions.Team?.Value));
        AddTextArray(command, "agents", distinct.Select(fact => fact.Dimensions.Agent?.Value));
        AddTextArray(command, "channels", distinct.Select(fact => fact.Dimensions.Channel));
        command.Parameters.Add(new NpgsqlParameter("priorities", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = distinct.Select(fact => (short)fact.Dimensions.Priority).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("labels", NpgsqlDbType.Array | NpgsqlDbType.Jsonb)
            { Value = distinct.Select(fact => JsonSerializer.Serialize(fact.Dimensions.Labels.Select(label => label.Value))).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = distinct.Select(fact => fact.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("bins", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = distinct.Select(fact => (short)fact.Bin).ToArray() });
        var inserted = new HashSet<(Guid, ReportMetric)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                inserted.Add((reader.GetGuid(0), Metric(reader.GetInt16(1))));
        return [.. distinct.Where(fact => inserted.Contains((fact.SourceId, fact.Metric)))];
    }

    private static async Task UpsertRollupsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, IReadOnlyList<ReportFact> facts,
        CancellationToken cancellationToken)
    {
        var cells = new Dictionary<CellKey, (long Count, long Total, long Minimum, long Maximum)>();
        foreach (var fact in facts)
        {
            var dimensions = fact.Dimensions;
            foreach (var label in dimensions.Labels.Select(label => label.Value).Prepend(string.Empty))
            {
                var key = new CellKey(fact.Metric, Micro(fact.CellStart), dimensions.Inbox.Value, dimensions.Team?.Value, dimensions.Agent?.Value, dimensions.Channel,
                    dimensions.Priority, label, fact.Bin);
                cells[key] = cells.TryGetValue(key, out var current)
                    ? (current.Count + 1, current.Total + fact.Value, Math.Min(current.Minimum, fact.Value), Math.Max(current.Maximum, fact.Value))
                    : (1, fact.Value, fact.Value, fact.Value);
            }
        }
        if (cells.Count == 0)
            return;
        var rows = cells.ToList();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO tf_report_rollups AS cell({ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, format_version)
            SELECT {ScopeParameters}, r.metric, r.cell_us, r.inbox_id, r.team_id, r.agent_id, r.channel, r.priority, r.label, r.bin, r.count, r.total, r.minimum, r.maximum, 1
            FROM unnest(@metrics, @cells, @inboxes, @teams, @agents, @channels, @priorities, @labels, @bins, @counts, @totals, @minimums, @maximums)
                AS r(metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum)
            ORDER BY tf_key_v1({ScopeParameters}, r.metric::text, r.cell_us::text, r.inbox_id, r.team_id, r.agent_id, r.channel, r.priority::text, r.label, r.bin::text)
            ON CONFLICT (cell_key) DO UPDATE SET count = cell.count + EXCLUDED.count, total = cell.total + EXCLUDED.total,
                minimum = LEAST(cell.minimum, EXCLUDED.minimum), maximum = GREATEST(cell.maximum, EXCLUDED.maximum)
            """, connection, transaction);
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter("metrics", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = rows.Select(row => (short)row.Key.Metric).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("cells", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(row => row.Key.Cell).ToArray() });
        AddTextArray(command, "inboxes", rows.Select(row => row.Key.Inbox));
        AddTextArray(command, "teams", rows.Select(row => row.Key.Team));
        AddTextArray(command, "agents", rows.Select(row => row.Key.Agent));
        AddTextArray(command, "channels", rows.Select(row => row.Key.Channel));
        command.Parameters.Add(new NpgsqlParameter("priorities", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = rows.Select(row => (short)row.Key.Priority).ToArray() });
        AddTextArray(command, "labels", rows.Select(row => row.Key.Label));
        command.Parameters.Add(new NpgsqlParameter("bins", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = rows.Select(row => (short)row.Key.Bin).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("counts", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(row => row.Value.Count).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("totals", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(row => row.Value.Total).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("minimums", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(row => row.Value.Minimum).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("maximums", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(row => row.Value.Maximum).ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct CellKey(ReportMetric Metric, long Cell, string Inbox, string? Team, string? Agent, string? Channel, ConversationPriority Priority, string Label, int Bin);

    // ---------------------------------------------------------------- shared helpers

    private static PostgresAccessFilterColumns Columns(string assignee, string labels) => new()
    {
        TenantId = "r.tenant_id",
        ProjectId = "r.project_id",
        EnvironmentId = "r.environment_id",
        AppId = "r.app_id",
        WorkspaceId = "r.workspace_id",
        TeamId = "r.team_id",
        InboxId = "r.inbox_id",
        AssigneeId = assignee,
        Labels = labels,
        Attributes = "r.no_attributes",
    };

    /// <summary>Appends the dimension restrictions of <paramref name="filter"/> (over alias <c>t</c>) and binds their parameters.</summary>
    private static void AddFilter(NpgsqlCommand command, StringBuilder where, ReportFilter filter, string agentColumn, string labelColumn)
    {
        if (filter.Inbox is { } inbox)
        {
            where.Append(" AND t.inbox_id = @f_inbox");
            AddText(command, "f_inbox", inbox.Value);
        }
        if (filter.Team is { } team)
        {
            where.Append(" AND t.team_id = @f_team");
            AddText(command, "f_team", team.Value);
        }
        if (filter.Agent is { } agent)
        {
            where.Append($" AND {agentColumn} = @f_agent");
            AddText(command, "f_agent", agent.Value);
        }
        if (filter.Channel is { } channel)
        {
            where.Append(" AND t.channel = @f_channel");
            AddText(command, "f_channel", channel);
        }
        if (filter.Priority is { } priority)
        {
            where.Append(" AND t.priority = @f_priority");
            command.Parameters.AddWithValue("f_priority", (short)priority);
        }
        if (filter.Label is { } label)
        {
            where.Append(" AND ").Append(labelColumn);
            AddText(command, "f_label", label.Value);
        }
    }

    private static async Task LockScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, bool shared, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {(shared ? "pg_advisory_xact_lock_shared" : "pg_advisory_xact_lock")}(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = (string[])["ticketflow.reporting", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value] });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long?> RevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_report_conversations WHERE {ConversationWhere}", connection, transaction);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static void BindState(NpgsqlCommand command, TicketScope scope, ReportConversationState state)
    {
        AddScope(command, scope);
        AddText(command, "conversation", state.Conversation.Value);
        AddText(command, "inbox", state.Inbox.Value);
        AddText(command, "channel", state.Channel);
        AddTime(command, "created", state.CreatedAt);
        command.Parameters.AddWithValue("status", (short)state.Status);
        AddText(command, "assignee", state.Assignee?.Value);
        AddText(command, "team", state.Team?.Value);
        command.Parameters.AddWithValue("priority", (short)state.Priority);
        AddTextArray(command, "labels", state.Labels.Select(label => label.Value));
        AddTime(command, "waiting", state.WaitingSince);
        AddTime(command, "first", state.FirstResponseAt);
        command.Parameters.AddWithValue("resolutions", state.Resolutions);
        command.Parameters.AddWithValue("sequence", state.LastSequence);
    }

    private static StoredReportConversation ReadState(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported reporting state format version.");
        var state = new ReportConversationState(new ConversationId(reader.GetString(0)), new InboxId(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4), Enum<ConversationStatusKind>(reader.GetInt16(5)), reader.IsDBNull(6) ? null : new UserId(reader.GetString(6)),
            reader.IsDBNull(7) ? null : new TeamId(reader.GetString(7)), Enum<ConversationPriority>(reader.GetInt16(8)),
            reader.GetFieldValue<string[]>(9).Select(label => new LabelId(label)), reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11), reader.GetInt32(12), reader.GetInt64(13));
        return new StoredReportConversation(scope, state, reader.GetInt64(14));
    }, "reporting state");

    private static ReportFact ReadFact(NpgsqlDataReader reader) => Decode(() =>
    {
        if (reader.GetInt16(11) != 1)
            throw new FormatException("Unsupported report fact format version.");
        var dimensions = new ReportDimensions(new InboxId(reader.GetString(4)), reader.IsDBNull(5) ? null : new TeamId(reader.GetString(5)),
            reader.IsDBNull(6) ? null : new UserId(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7), Enum<ConversationPriority>(reader.GetInt16(8)),
            reader.GetFieldValue<string[]>(9).Select(label => new LabelId(label)));
        return new ReportFact(reader.GetGuid(0), Metric(reader.GetInt16(1)), new ConversationId(reader.GetString(2)), reader.GetFieldValue<DateTimeOffset>(3), dimensions,
            reader.GetInt64(10));
    }, "report fact");

    private static long Micro(DateTimeOffset instant) => (instant.UtcTicks - UnixEpochTicks) / 10;

    private static ReportMetric Metric(short value) => Enum<ReportMetric>(value);

    private static T Enum<T>(short value)
        where T : struct, System.Enum
    {
        var result = (T)(object)(int)value;
        return System.Enum.IsDefined(result) ? result : throw new FormatException($"Unknown {typeof(T).Name} value.");
    }

    private static void Require(ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
    }

    private static void RequireLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ReportStoreLimits.MaximumPrune);
    }

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

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string?> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    private static void AddTime(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value is { } time ? time : DBNull.Value });

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
