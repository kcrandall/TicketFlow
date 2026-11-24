using System.Data;
using System.Runtime.CompilerServices;
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

    private static readonly string ScopeKeyWhereT = "t." + ScopeKeyWhere.Replace(" AND ", " AND t.", StringComparison.Ordinal);
    private static readonly string ScopeKeyWhereF = "f." + ScopeKeyWhere.Replace(" AND ", " AND f.", StringComparison.Ordinal);
    private static readonly string FactColumnsR = string.Join(", ", FactColumns.Split(", ").Select(column => "r." + column));
    private static readonly short[] DurationMetrics = [.. ReportMetric.All.Where(metric => metric.IsDuration).Select(metric => (short)metric)];

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
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadState(reader, scope) : null;
    }

    public async Task<ReportWriteResult> ApplyAsync(TicketScope scope, ReportProjection projection, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(projection);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var state = projection.State;
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

        // When no facts are written, skip the explicit transaction and scope advisory lock (1 auto-committed RTT).
        if (projection.Facts.Count == 0)
        {
            long? directRevision;
            await using (var command = source.CreateCommand(write))
            {
                BindState(command, scope, state);
                if (expectedRevision is { } expected)
                    AddInt64(command, "expected", expected);
                directRevision = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
            }
            if (directRevision is null)
                return new ReportWriteConflict(await RevisionAsync(scope, state.Conversation, cancellationToken).ConfigureAwait(false));
            return new ReportWriteApplied(new StoredReportConversation(scope, state, directRevision.Value), 0);
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scope, shared: true, cancellationToken).ConfigureAwait(false);

        long? revision;
        await using (var command = new NpgsqlCommand(write, connection, transaction))
        {
            BindState(command, scope, state);
            if (expectedRevision is { } expected)
                AddInt64(command, "expected", expected);
            revision = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }
        if (revision is null)
            return new ReportWriteConflict(await RevisionAsync(connection, transaction, scope, state.Conversation, cancellationToken).ConfigureAwait(false));

        var inserted = await InsertFactsAndUpsertRollupsAsync(connection, transaction, scope, projection.Facts, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReportWriteApplied(new StoredReportConversation(scope, state, revision.Value), inserted);
    }

    public async Task<ReportWriteResult> DeleteConversationAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(conversation);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        StoredReportConversation? removed = null;
        await using (var command = source.CreateCommand($"DELETE FROM tf_report_conversations WHERE {ConversationWhere} AND revision=@expected RETURNING {StateColumns}"))
        {
            AddScope(command, scope);
            AddText(command, "conversation", conversation.Value);
            AddInt64(command, "expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                removed = ReadState(reader, scope);
        }
        if (removed is null)
            return new ReportWriteConflict(await RevisionAsync(scope, conversation, cancellationToken).ConfigureAwait(false));
        return new ReportWriteApplied(removed, 0);
    }

    public async Task<ReportAggregateResult> AggregateAsync(TicketScope scope, ReportAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        await using var command = CreateAggregateCommand(scope, query, filter);
        if (command is null)
            return new ReportAggregateResult([], false);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        command.Connection = connection;
        var rows = new List<ReportAggregateRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadAggregateRow(reader));
        var truncated = rows.Count > ReportAggregateQuery.MaximumRows;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);
        return new ReportAggregateResult(rows, truncated);
    }

    /// <summary>
    /// The aggregate statement (without a connection), or <see langword="null"/> when the filter denies everything. Unlabelled aggregates
    /// read rollups through the partial index <c>tf_report_rollups_query_v2</c> (<c>label = ''</c>); label groupings and filters read the
    /// facts through <c>tf_report_facts_labelled</c> (<c>cardinality(labels) &gt; 0</c>), with labels unknown to the
    /// Access filter exactly as on rollups.
    /// </summary>
    internal static NpgsqlCommand? CreateAggregateCommand(TicketScope scope, ReportAggregateQuery query, AccessFilter filter)
    {
        var access = PostgresAccessFilterTranslator.Translate(filter, RollupColumns).OrDenyAll();
        if (access.Sql == "FALSE")
            return null;
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
        var singleBucket = query.Boundaries.Count == 2;
        var bucket = singleBucket ? "0" : "width_bucket(r.cell_us, @bounds) - 1";
        var bin = query.Histograms ? "CASE WHEN r.metric = ANY(@durations) THEN r.bin ELSE 0 END" : "0";
        var where = new StringBuilder();
        var command = new NpgsqlCommand();
        AddScope(command, scope);
        string from, totals;
        if (query.GroupBy == ReportDimension.Label)
        {
            AddFilter(command, where, query.Filter, "t.agent_id", labelColumn: "l.label = @f_label");
            from = $"""
                SELECT t.*, l.label, NULL::text[] AS no_labels, NULL::jsonb AS no_attributes FROM tf_report_facts t CROSS JOIN LATERAL unnest(t.labels) AS l(label)
                WHERE {ScopeKeyWhereT} AND cardinality(t.labels) > 0 AND t.metric = ANY(@metrics) AND t.cell_us >= @from AND t.cell_us < @to{where}
                """;
            totals = "count(*)::bigint, sum(r.value)::bigint, min(r.value), max(r.value)";
        }
        else if (query.Filter.Label is not null)
        {
            // Labels on each fact are distinct, so filtering by one label without grouping by label matches at most once per fact without LATERAL unnest.
            AddFilter(command, where, query.Filter, "t.agent_id", labelColumn: "@f_label = ANY(t.labels)");
            from = $"""
                SELECT t.*, NULL::text[] AS no_labels, NULL::jsonb AS no_attributes FROM tf_report_facts t
                WHERE {ScopeKeyWhereT} AND cardinality(t.labels) > 0 AND t.metric = ANY(@metrics) AND t.cell_us >= @from AND t.cell_us < @to{where}
                """;
            totals = "count(*)::bigint, sum(r.value)::bigint, min(r.value), max(r.value)";
        }
        else
        {
            AddFilter(command, where, query.Filter, "t.agent_id", labelColumn: "FALSE");
            from = $"""
                SELECT t.*, NULL::text[] AS no_labels, NULL::jsonb AS no_attributes FROM tf_report_rollups t
                WHERE {ScopeKeyWhereT} AND t.label = '' AND t.metric = ANY(@metrics) AND t.cell_us >= @from AND t.cell_us < @to{where}
                """;
            totals = "sum(r.count)::bigint, sum(r.total)::bigint, min(r.minimum), max(r.maximum)";
        }
        command.CommandText = $"""
            SELECT {bucket} AS bucket, {group} AS grp, r.metric, {bin} AS bin, {totals}
            FROM ({from}) r
            WHERE {access.Sql}
            GROUP BY 1, 2, 3, 4
            ORDER BY 1, ({group}) COLLATE "C" NULLS FIRST, 3, 4
            LIMIT {ReportAggregateQuery.MaximumRows + 1}
            """;
        foreach (var parameter in access.Parameters)
            command.Parameters.Add(parameter);
        if (!singleBucket)
        {
            var bounds = new long[query.Boundaries.Count];
            for (var i = 0; i < bounds.Length; i++)
                bounds[i] = Micro(query.Boundaries[i]);
            command.Parameters.Add(new NpgsqlParameter<long[]>("bounds", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = bounds });
        }
        var metrics = new short[query.Metrics.Count];
        for (var i = 0; i < metrics.Length; i++)
            metrics[i] = (short)query.Metrics[i];
        command.Parameters.Add(new NpgsqlParameter<short[]>("metrics", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = metrics });
        if (query.Histograms)
            command.Parameters.Add(new NpgsqlParameter<short[]>("durations", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = DurationMetrics });
        AddInt64(command, "from", Micro(query.From));
        AddInt64(command, "to", Micro(query.To));
        return command;
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
                WHERE {ScopeKeyWhereT} AND t.status NOT IN (4, 5){where}) r
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
            command.Parameters.Add(new NpgsqlParameter<Guid>("after_source", NpgsqlDbType.Uuid) { TypedValue = after.SourceId });
            AddInt16(command, "after_metric", (short)after.Metric);
        }
        command.CommandText = $"""
            SELECT {FactColumnsR}
            FROM (SELECT t.*, NULL::jsonb AS no_attributes FROM tf_report_facts t
                WHERE {ScopeKeyWhereT} AND t.occurred_at >= @from AND t.occurred_at < @to{where}) r
            WHERE {access.Sql}
            ORDER BY r.occurred_at, r.source_id, r.metric
            LIMIT {query.Limit + 1}
            """;
        foreach (var parameter in access.Parameters)
            command.Parameters.Add(parameter);
        AddTime(command, "from", query.From);
        AddTime(command, "to", query.To);
        var facts = new List<ReportFact>(Math.Min(query.Limit + 1, 64));
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
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Advance the horizon first: the row lock serializes this prune with rebuilds, which read the horizon FOR SHARE.
        await using (var horizon = new NpgsqlCommand(
            "UPDATE tf_report_retention SET fact_horizon = GREATEST(COALESCE(fact_horizon, @cutoff), @cutoff) WHERE id = 1", connection, transaction))
        {
            AddTime(horizon, "cutoff", olderThan);
            if (await horizon.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The reporting retention row is missing; run InitializeAsync.");
        }
        int deleted;
        await using (var command = new NpgsqlCommand("""
            DELETE FROM tf_report_facts WHERE ctid IN (
                SELECT ctid FROM tf_report_facts WHERE occurred_at < @cutoff ORDER BY occurred_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            """, connection, transaction))
        {
            AddTime(command, "cutoff", olderThan);
            AddInt32(command, "limit", limit);
            deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async Task<DateTimeOffset?> GetFactHorizonAsync(CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand("SELECT fact_horizon FROM tf_report_retention WHERE id = 1");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime value
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : null;
    }

    public async Task<int> PruneRollupsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        RequireLimit(limit);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_report_rollups WHERE ctid IN (
                SELECT ctid FROM tf_report_rollups WHERE cell_us < @cutoff ORDER BY cell_us LIMIT @limit FOR UPDATE SKIP LOCKED)
            """);
        AddInt64(command, "cutoff", Micro(olderThan));
        AddInt32(command, "limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReportRebuildResult> RebuildRollupsAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ReportStoreLimits.RequireRebuildRange(from, to);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Exclusive against projections of the scope, so no fact can commit between the delete and the recount.
        await LockScopeAsync(connection, transaction, scope, shared: false, cancellationToken).ConfigureAwait(false);
        // FOR SHARE blocks concurrent fact prunes (they update this row first) until the rebuild commits.
        await using (var horizon = new NpgsqlCommand("SELECT fact_horizon FROM tf_report_retention WHERE id = 1 FOR SHARE", connection, transaction))
            if (await horizon.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime value
                && new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) is var factHorizon && from < factHorizon)
                return new ReportRebuildRefused(factHorizon);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_report_rollups WHERE {ScopeKeyWhere} AND cell_us >= @from AND cell_us < @to", connection, transaction))
        {
            AddScope(delete, scope);
            AddInt64(delete, "from", Micro(from));
            AddInt64(delete, "to", Micro(to));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long counted = 0;
        await using (var insert = new NpgsqlCommand($"""
            WITH inserted AS (
                INSERT INTO tf_report_rollups({ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, format_version)
                SELECT {ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, '', bin, count(*), sum(value)::bigint, min(value), max(value), 1
                FROM tf_report_facts f
                WHERE {ScopeKeyWhereF} AND f.cell_us >= @from AND f.cell_us < @to
                GROUP BY {ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, bin
                RETURNING count)
            SELECT COALESCE(sum(count), 0)::bigint FROM inserted
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddInt64(insert, "from", Micro(from));
            AddInt64(insert, "to", Micro(to));
            counted = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReportRebuilt(counted);
    }

    // ---------------------------------------------------------------- projection writes

    private static async Task<int> InsertFactsAndUpsertRollupsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, IReadOnlyList<ReportFact> facts,
        CancellationToken cancellationToken)
    {
        var count = facts.Count;
        if (count == 0)
            return 0;
        if (count == 1)
        {
            var fact = facts[0];
            var dims = fact.Dimensions;
            await using var single = new NpgsqlCommand($"""
                WITH inserted AS (
                    INSERT INTO tf_report_facts({ScopeColumns}, source_id, metric, conversation_id, occurred_at, cell_us, inbox_id, team_id, agent_id, channel, priority, labels, value, bin, format_version)
                    VALUES ({ScopeParameters}, @source, @metric, @conversation, @occurred, @cell, @inbox, @team, @agent, @channel, @priority, @labels, @value, @bin, 1)
                    ON CONFLICT (fact_key) DO NOTHING
                    RETURNING metric, cell_us, inbox_id, team_id, agent_id, channel, priority, value, bin
                ),
                upserted AS (
                    INSERT INTO tf_report_rollups AS cell({ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, format_version)
                    SELECT {ScopeParameters}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, '', bin, 1, value, value, value, 1
                    FROM inserted
                    ON CONFLICT (cell_key) DO UPDATE SET count = cell.count + EXCLUDED.count, total = cell.total + EXCLUDED.total,
                        minimum = LEAST(cell.minimum, EXCLUDED.minimum), maximum = GREATEST(cell.maximum, EXCLUDED.maximum)
                )
                SELECT count(*)::int FROM inserted
                """, connection, transaction);
            AddScope(single, scope);
            single.Parameters.Add(new NpgsqlParameter<Guid>("source", NpgsqlDbType.Uuid) { TypedValue = fact.SourceId });
            AddInt16(single, "metric", (short)fact.Metric);
            AddText(single, "conversation", fact.Conversation.Value);
            AddTime(single, "occurred", fact.OccurredAt);
            AddInt64(single, "cell", Micro(fact.CellStart));
            AddText(single, "inbox", dims.Inbox.Value);
            AddText(single, "team", dims.Team?.Value);
            AddText(single, "agent", dims.Agent?.Value);
            AddText(single, "channel", dims.Channel);
            AddInt16(single, "priority", (short)dims.Priority);
            AddLabelArray(single, "labels", dims.Labels);
            AddInt64(single, "value", fact.Value);
            AddInt16(single, "bin", (short)fact.Bin);
            return (int)(await single.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var distinct = new List<ReportFact>(count);
        var seen = new HashSet<(Guid, ReportMetric)>(count);
        for (var i = 0; i < count; i++)
        {
            var fact = facts[i];
            if (seen.Add((fact.SourceId, fact.Metric)))
                distinct.Add(fact);
        }
        var n = distinct.Count;
        var sources = new Guid[n];
        var metrics = new short[n];
        var conversations = new string[n];
        var occurred = new DateTimeOffset[n];
        var cells = new long[n];
        var inboxes = new string[n];
        var teams = new string?[n];
        var agents = new string?[n];
        var channels = new string?[n];
        var priorities = new short[n];
        var labels = new string[n];
        var values = new long[n];
        var bins = new short[n];
        for (var i = 0; i < n; i++)
        {
            var fact = distinct[i];
            var dims = fact.Dimensions;
            sources[i] = fact.SourceId;
            metrics[i] = (short)fact.Metric;
            conversations[i] = fact.Conversation.Value;
            occurred[i] = fact.OccurredAt;
            cells[i] = Micro(fact.CellStart);
            inboxes[i] = dims.Inbox.Value;
            teams[i] = dims.Team?.Value;
            agents[i] = dims.Agent?.Value;
            channels[i] = dims.Channel;
            priorities[i] = (short)dims.Priority;
            labels[i] = EncodeLabelsJson(dims.Labels);
            values[i] = fact.Value;
            bins[i] = (short)fact.Bin;
        }
        await using var command = new NpgsqlCommand($"""
            WITH inserted AS (
                INSERT INTO tf_report_facts({ScopeColumns}, source_id, metric, conversation_id, occurred_at, cell_us, inbox_id, team_id, agent_id, channel, priority, labels, value, bin, format_version)
                SELECT {ScopeParameters}, f.source_id, f.metric, f.conversation_id, f.occurred_at, f.cell_us, f.inbox_id, f.team_id, f.agent_id, f.channel, f.priority,
                    ARRAY(SELECT jsonb_array_elements_text(f.labels)), f.value, f.bin, 1
                FROM unnest(@sources, @metrics, @conversations, @occurred, @cells, @inboxes, @teams, @agents, @channels, @priorities, @labels, @values, @bins)
                    AS f(source_id, metric, conversation_id, occurred_at, cell_us, inbox_id, team_id, agent_id, channel, priority, labels, value, bin)
                ON CONFLICT (fact_key) DO NOTHING
                RETURNING metric, cell_us, inbox_id, team_id, agent_id, channel, priority, value, bin
            ),
            rolled AS (
                SELECT metric, cell_us, inbox_id, team_id, agent_id, channel, priority, bin,
                    count(*)::bigint AS cnt, sum(value)::bigint AS total, min(value) AS minimum, max(value) AS maximum
                FROM inserted
                GROUP BY metric, cell_us, inbox_id, team_id, agent_id, channel, priority, bin
            ),
            upserted AS (
                INSERT INTO tf_report_rollups AS cell({ScopeColumns}, metric, cell_us, inbox_id, team_id, agent_id, channel, priority, label, bin, count, total, minimum, maximum, format_version)
                SELECT {ScopeParameters}, r.metric, r.cell_us, r.inbox_id, r.team_id, r.agent_id, r.channel, r.priority, '', r.bin, r.cnt, r.total, r.minimum, r.maximum, 1
                FROM rolled r
                ORDER BY tf_key_v1({ScopeParameters}, r.metric::text, r.cell_us::text, r.inbox_id, r.team_id, r.agent_id, r.channel, r.priority::text, '', r.bin::text)
                ON CONFLICT (cell_key) DO UPDATE SET count = cell.count + EXCLUDED.count, total = cell.total + EXCLUDED.total,
                    minimum = LEAST(cell.minimum, EXCLUDED.minimum), maximum = GREATEST(cell.maximum, EXCLUDED.maximum)
            )
            SELECT count(*)::int FROM inserted
            """, connection, transaction);
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("sources", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = sources });
        command.Parameters.Add(new NpgsqlParameter<short[]>("metrics", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = metrics });
        command.Parameters.Add(new NpgsqlParameter<string[]>("conversations", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = conversations });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = occurred });
        command.Parameters.Add(new NpgsqlParameter<long[]>("cells", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = cells });
        command.Parameters.Add(new NpgsqlParameter<string[]>("inboxes", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = inboxes });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("teams", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = teams });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("agents", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = agents });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("channels", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = channels });
        command.Parameters.Add(new NpgsqlParameter<short[]>("priorities", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = priorities });
        command.Parameters.Add(new NpgsqlParameter<string[]>("labels", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { TypedValue = labels });
        command.Parameters.Add(new NpgsqlParameter<long[]>("values", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = values });
        command.Parameters.Add(new NpgsqlParameter<short[]>("bins", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = bins });
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static string EncodeLabelsJson(IReadOnlyList<LabelId> labels)
    {
        if (labels.Count == 0)
            return "[]";
        var array = new string[labels.Count];
        for (var i = 0; i < labels.Count; i++)
            array[i] = labels[i].Value;
        return JsonSerializer.Serialize(array);
    }

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
            where.Append(" AND ").Append(agentColumn).Append(" = @f_agent");
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
            AddInt16(command, "f_priority", (short)priority);
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
            shared
                ? "SELECT pg_advisory_xact_lock_shared(hashtextextended(array_to_string(@parts, chr(31)), 0))"
                : "SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]>("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { TypedValue = ["ticketflow.reporting", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value] });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long?> RevisionAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand($"SELECT revision FROM tf_report_conversations WHERE {ConversationWhere}");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
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
        AddInt16(command, "status", (short)state.Status);
        AddText(command, "assignee", state.Assignee?.Value);
        AddText(command, "team", state.Team?.Value);
        AddInt16(command, "priority", (short)state.Priority);
        AddLabelArray(command, "labels", state.Labels);
        AddTime(command, "waiting", state.WaitingSince);
        AddTime(command, "first", state.FirstResponseAt);
        AddInt32(command, "resolutions", state.Resolutions);
        AddInt64(command, "sequence", state.LastSequence);
    }

    private static ReportAggregateRow ReadAggregateRow(NpgsqlDataReader reader)
    {
        try
        {
            return new ReportAggregateRow(reader.GetInt32(0), reader.NullableText(1), Metric(reader.GetInt16(2)), reader.GetInt32(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored rollup aggregate is invalid or incompatible.", error);
        }
    }

    private static StoredReportConversation ReadState(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported reporting state format version.");
            var rawLabels = reader.GetFieldValue<string[]>(9);
            var labels = ToLabelIds(rawLabels);
            var state = new ReportConversationState(new ConversationId(reader.GetString(0)), new InboxId(reader.GetString(2)), reader.NullableText(3),
                reader.GetFieldValue<DateTimeOffset>(4), Enum<ConversationStatusKind>(reader.GetInt16(5)), reader.NullableText(6) is { } agent ? new UserId(agent) : null,
                reader.NullableText(7) is { } team ? new TeamId(team) : null, Enum<ConversationPriority>(reader.GetInt16(8)),
                labels, reader.NullableInstant(10),
                reader.NullableInstant(11), reader.GetInt32(12), reader.GetInt64(13));
            return new StoredReportConversation(scope, state, reader.GetInt64(14));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored reporting state is invalid or incompatible.", error);
        }
    }

    private static ReportFact ReadFact(NpgsqlDataReader reader)
    {
        try
        {
            if (reader.GetInt16(11) != 1)
                throw new FormatException("Unsupported report fact format version.");
            var rawLabels = reader.GetFieldValue<string[]>(9);
            var labels = ToLabelIds(rawLabels);
            var dimensions = new ReportDimensions(new InboxId(reader.GetString(4)), reader.NullableText(5) is { } team ? new TeamId(team) : null,
                reader.NullableText(6) is { } agent ? new UserId(agent) : null, reader.NullableText(7), Enum<ConversationPriority>(reader.GetInt16(8)),
                labels);
            return new ReportFact(reader.GetGuid(0), Metric(reader.GetInt16(1)), new ConversationId(reader.GetString(2)), reader.GetFieldValue<DateTimeOffset>(3), dimensions,
                reader.GetInt64(10));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored report fact is invalid or incompatible.", error);
        }
    }

    private static LabelId[] ToLabelIds(string[] raw)
    {
        if (raw.Length == 0)
            return [];
        var result = new LabelId[raw.Length];
        for (var i = 0; i < raw.Length; i++)
            result[i] = new LabelId(raw[i]);
        return result;
    }

    private static long Micro(DateTimeOffset instant) => (instant.UtcTicks - UnixEpochTicks) / 10;

    private static ReportMetric Metric(short value) => Enum<ReportMetric>(value);

    private static T Enum<T>(short value)
        where T : struct, System.Enum
    {
        int raw = value;
        var result = Unsafe.As<int, T>(ref raw);
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

    private static void AddText(NpgsqlCommand command, string name, string? value)
    {
        if (value is null)
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value });
        else
            command.Parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });
    }

    private static void AddLabelArray(NpgsqlCommand command, string name, IReadOnlyList<LabelId> labels)
    {
        string[] values;
        if (labels.Count == 0)
            values = [];
        else
        {
            values = new string[labels.Count];
            for (var i = 0; i < labels.Count; i++)
                values[i] = labels[i].Value;
        }
        command.Parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values });
    }

    private static void AddTime(NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        if (value is { } time)
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = time });
        else
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
    }

    private static void AddInt16(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddInt32(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddInt64(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });
}
