using System.Buffers;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="ISlaPolicyStore"/> and <see cref="ISlaTrackerStore"/> over an application-owned
/// <see cref="NpgsqlDataSource"/>. Tables live in the data source's current schema and are created only by
/// <see cref="InitializeAsync"/>. Every statement is parameterized and confined to one full scope (timer claims are store-wide and
/// return their scope). Policy capacity is checked under a workspace advisory lock. Tracker writes are revision-fenced and update the
/// timer row in the same transaction; timers are claimed with <c>FOR UPDATE SKIP LOCKED</c> plus a lease and fence. Risk lists apply
/// the translated <see cref="AccessFilter"/> in the keyset query. Rows that fail to decode throw. Lease times come from the supplied
/// <see cref="TimeProvider"/>, so every client must use a synchronized clock.
/// </summary>
public sealed class PostgresSlaStore : ISlaPolicyStore, ISlaTrackerStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + ScopeWhere;
    private const string PolicyWhere = "policy_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@policy) AND policy_id=@policy AND " + ScopeWhere;
    private const string TrackerWhere = "tracker_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;

    private static readonly PostgresAccessFilterColumns FilterColumns = new()
    {
        TenantId = "t.tenant_id",
        ProjectId = "t.project_id",
        EnvironmentId = "t.environment_id",
        AppId = "t.app_id",
        WorkspaceId = "t.workspace_id",
        TeamId = "t.team_id",
        InboxId = "t.inbox_id",
        AssigneeId = "t.assignee_id",
        Labels = "t.labels",
        Attributes = "t.access_attributes",
    };

    private readonly NpgsqlDataSource source;
    private readonly TimeProvider time;

    public PostgresSlaStore(NpgsqlDataSource dataSource, TimeProvider? time = null)
    {
        source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Explicitly applies <see cref="PostgresSlaMigrations.Catalog"/> through <see cref="PostgresMigrationRunner"/> (advisory-locked,
    /// checksum-verified, idempotent). Never called implicitly.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresSlaMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<StoredSlaPolicy?> GetAsync(TicketScope scope, SlaPolicyId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        await using var command = source.CreateCommand($"SELECT policy_id, format_version, definition, revision FROM tf_sla_policies WHERE {PolicyWhere}");
        AddScope(command, scope);
        AddText(command, "policy", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPolicy(reader, scope) : null;
    }

    public async Task<IReadOnlyList<StoredSlaPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT policy_id, format_version, definition, revision FROM tf_sla_policies WHERE {ScopeKeyWhere}
            ORDER BY policy_id COLLATE "C" LIMIT {ISlaPolicyStore.MaximumPoliciesPerWorkspace}
            """);
        AddScope(command, scope);
        var policies = new List<StoredSlaPolicy>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            policies.Add(ReadPolicy(reader, scope));
        return policies;
    }

    public async Task<SlaWriteResult<StoredSlaPolicy>> CreateAsync(TicketScope scope, SlaPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        var definition = SlaPolicyCodec.Serialize(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = new[] { "ticketflow.sla", "policies", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value },
            });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (await PolicyRevisionAsync(connection, transaction, scope, policy.Id, cancellationToken).ConfigureAwait(false) is { } current)
            return new SlaWriteConflict(current);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_sla_policies WHERE {ScopeKeyWhere}", connection, transaction))
        {
            AddScope(count, scope);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= ISlaPolicyStore.MaximumPoliciesPerWorkspace)
                return new SlaWriteRejected(SlaWriteRejection.CapacityExceeded);
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_sla_policies(tenant_id, project_id, environment_id, app_id, workspace_id, policy_id, format_version, definition, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @policy, 1, @definition, 1)
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "policy", policy.Id.Value);
            AddText(insert, "definition", definition);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SlaWriteApplied<StoredSlaPolicy>(new StoredSlaPolicy(scope, policy, 1));
    }

    public async Task<SlaWriteResult<StoredSlaPolicy>> UpdateAsync(TicketScope scope, SlaPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"UPDATE tf_sla_policies SET definition=@definition, revision=revision+1 WHERE {PolicyWhere} AND revision=@expected RETURNING revision", connection);
        AddScope(command, scope);
        AddText(command, "policy", policy.Id.Value);
        AddText(command, "definition", SlaPolicyCodec.Serialize(policy));
        command.Parameters.AddWithValue("expected", expectedRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new SlaWriteApplied<StoredSlaPolicy>(new StoredSlaPolicy(scope, policy, revision));
        return await PolicyRevisionAsync(connection, null, scope, policy.Id, cancellationToken).ConfigureAwait(false) is { } current
            ? new SlaWriteConflict(current)
            : SlaWriteNotFound.Instance;
    }

    public async Task<SlaWriteResult<StoredSlaPolicy>> DeleteAsync(TicketScope scope, SlaPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_sla_policies WHERE {PolicyWhere} AND revision=@expected RETURNING policy_id, format_version, definition, revision", connection))
        {
            AddScope(command, scope);
            AddText(command, "policy", id.Value);
            command.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new SlaWriteApplied<StoredSlaPolicy>(ReadPolicy(reader, scope));
        }
        return await PolicyRevisionAsync(connection, null, scope, id, cancellationToken).ConfigureAwait(false) is { } current
            ? new SlaWriteConflict(current)
            : SlaWriteNotFound.Instance;
    }

    public async Task<StoredSlaTracker?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        await using var command = source.CreateCommand($"SELECT conversation_id, format_version, state, revision FROM tf_sla_trackers WHERE {TrackerWhere}");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTracker(reader, scope) : null;
    }

    public async Task<SlaWriteResult<StoredSlaTracker>> WriteAsync(TicketScope scope, SlaTracker tracker, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(tracker);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var state = SlaTrackerCodec.Serialize(tracker);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long? revision;
        await using (var command = new NpgsqlCommand(expectedRevision is null
            ? """
              INSERT INTO tf_sla_trackers(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, inbox_id, format_version, state, team_id,
                  assignee_id, labels, access_attributes, due_at, at_risk_at, breached, urgency_at, revision)
              VALUES(@tenant, @project, @environment, @app, @workspace, @conversation, @inbox, 1, @state, @team, @assignee, @labels, @attributes::jsonb, @due, @risk,
                  @breached, @urgency, 1)
              ON CONFLICT DO NOTHING RETURNING revision
              """
            : $"""
              UPDATE tf_sla_trackers SET state=@state, team_id=@team, assignee_id=@assignee, labels=@labels, access_attributes=@attributes::jsonb, due_at=@due,
                  at_risk_at=@risk, breached=@breached, urgency_at=@urgency, revision=revision+1
              WHERE {TrackerWhere} AND revision=@expected AND inbox_id=@inbox
              RETURNING revision
              """, connection, transaction))
        {
            AddScope(command, scope);
            AddText(command, "conversation", tracker.Conversation.Value);
            AddText(command, "inbox", tracker.Inbox.Value);
            AddText(command, "state", state);
            AddText(command, "team", tracker.Access.Team?.Value);
            AddText(command, "assignee", tracker.Access.Assignee?.Value);
            command.Parameters.Add(new NpgsqlParameter("labels", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tracker.Access.Labels.Select(label => label.Value).ToArray() });
            AddText(command, "attributes", SerializeAttributes(tracker.Access.Attributes));
            AddInstant(command, "due", tracker.Schedule.DueAt);
            AddInstant(command, "risk", tracker.Schedule.AtRiskAt);
            command.Parameters.AddWithValue("breached", tracker.Schedule.Breached);
            AddInstant(command, "urgency", tracker.Schedule.UrgencyAt);
            if (expectedRevision is { } expected)
                command.Parameters.AddWithValue("expected", expected);
            revision = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }
        if (revision is null)
        {
            var current = await TrackerRevisionAsync(connection, transaction, scope, tracker.Conversation, cancellationToken).ConfigureAwait(false);
            return current is { } value ? new SlaWriteConflict(value) : expectedRevision is null ? new SlaWriteConflict(0) : SlaWriteNotFound.Instance;
        }
        await using (var timer = new NpgsqlCommand("""
            INSERT INTO tf_sla_timers(tracker_key, tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, due_at, lease_until, fence)
            VALUES(tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation), @tenant, @project, @environment, @app, @workspace, @conversation, @due, NULL, 0)
            ON CONFLICT (tracker_key) DO UPDATE SET due_at=EXCLUDED.due_at, lease_until=NULL
            """, connection, transaction))
        {
            AddScope(timer, scope);
            AddText(timer, "conversation", tracker.Conversation.Value);
            AddInstant(timer, "due", tracker.Schedule.NextCheckAt);
            await timer.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SlaWriteApplied<StoredSlaTracker>(new StoredSlaTracker(scope, tracker, revision.Value));
    }

    public async Task<SlaWriteResult<StoredSlaTracker>> DeleteAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_sla_trackers WHERE {TrackerWhere} AND revision=@expected RETURNING conversation_id, format_version, state, revision", connection))
        {
            AddScope(command, scope);
            AddText(command, "conversation", conversation.Value);
            command.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new SlaWriteApplied<StoredSlaTracker>(ReadTracker(reader, scope));
        }
        return await TrackerRevisionAsync(connection, null, scope, conversation, cancellationToken).ConfigureAwait(false) is { } current
            ? new SlaWriteConflict(current)
            : SlaWriteNotFound.Instance;
    }

    public async Task<IReadOnlyList<SlaTimerClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ISlaTrackerStore.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > ISlaTrackerStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most one hour.");
        var now = time.GetUtcNow();
        // SKIP LOCKED keeps concurrent claimers from blocking on or double-claiming the same timer; the lease covers the time after commit.
        await using var command = source.CreateCommand("""
            WITH candidates AS (
                SELECT tracker_key FROM tf_sla_timers
                WHERE due_at <= @now AND (lease_until IS NULL OR lease_until <= @now)
                ORDER BY due_at, tracker_key LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_sla_timers t SET fence=t.fence+1, lease_until=@until
            FROM candidates c WHERE t.tracker_key=c.tracker_key
            RETURNING t.tenant_id, t.project_id, t.environment_id, t.app_id, t.workspace_id, t.conversation_id, t.due_at, t.fence, t.lease_until
            """);
        AddInstant(command, "now", now);
        AddInstant(command, "until", now + lease);
        command.Parameters.AddWithValue("limit", limit);
        var claims = new List<SlaTimerClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            claims.Add(Decode(() => new SlaTimerClaim(
                new TicketScope(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), new WorkspaceId(reader.GetString(4))),
                new ConversationId(reader.GetString(5)), reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7), reader.GetFieldValue<DateTimeOffset>(8)), "timer"));
        return [.. claims.OrderBy(claim => claim.DueAt)];
    }

    public async Task<bool> ReleaseAsync(SlaTimerClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var command = source.CreateCommand($"""
            UPDATE tf_sla_timers SET due_at=@retry, lease_until=NULL
            WHERE {TrackerWhere} AND fence=@fence AND lease_until > @now
            """);
        AddScope(command, claim.Scope);
        AddText(command, "conversation", claim.Conversation.Value);
        command.Parameters.AddWithValue("fence", claim.Fence);
        AddInstant(command, "retry", retryAt);
        AddInstant(command, "now", time.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<SlaTrackerPage> ListAsync(TicketScope scope, SlaRiskQuery query, DateTimeOffset now, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var translated = PostgresAccessFilterTranslator.Translate(filter, FilterColumns).OrDenyAll();
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        foreach (var parameter in translated.Parameters)
            command.Parameters.Add(parameter.Clone());
        AddInstant(command, "now", now);
        const string breached = "(t.breached OR COALESCE(t.due_at <= @now, FALSE))";
        var criteria = new List<string>
        {
            query.Filter switch
            {
                SlaRiskFilter.AtRisk => $"NOT {breached} AND COALESCE(t.at_risk_at <= @now, FALSE)",
                SlaRiskFilter.Breached => breached,
                SlaRiskFilter.AtRiskOrBreached => $"({breached} OR COALESCE(t.at_risk_at <= @now, FALSE))",
                _ => throw new ArgumentOutOfRangeException(nameof(query)),
            },
        };
        if (query.Inbox is { } inbox)
        {
            criteria.Add("t.inbox_id=@inbox");
            AddText(command, "inbox", inbox.Value);
        }
        if (query.After is { } after)
        {
            criteria.Add("(t.urgency_at, t.conversation_id) > (@after_at, @after_id COLLATE \"C\")");
            AddInstant(command, "after_at", after.UrgencyAt);
            AddText(command, "after_id", after.Conversation.Value);
        }
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        command.CommandText = $"""
            SELECT t.conversation_id, t.format_version, t.state, t.revision FROM tf_sla_trackers t
            WHERE t.scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND t.{ScopeWhere.Replace(" AND ", " AND t.", StringComparison.Ordinal)}
              AND t.urgency_at IS NOT NULL{string.Concat(criteria.Select(item => " AND " + item))}
              AND ({translated.Sql})
            ORDER BY t.urgency_at, t.conversation_id LIMIT @limit
            """;
        var rows = new List<StoredSlaTracker>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadTracker(reader, scope));
        if (rows.Count <= query.Limit)
            return new SlaTrackerPage(rows, null);
        var page = rows[..query.Limit];
        return new SlaTrackerPage(page, new SlaRiskCursor(page[^1].Tracker.Schedule.UrgencyAt!.Value, page[^1].Conversation));
    }

    private static async Task<long?> PolicyRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, SlaPolicyId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_sla_policies WHERE {PolicyWhere}", connection, transaction);
        AddScope(command, scope);
        AddText(command, "policy", id.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static async Task<long?> TrackerRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ConversationId conversation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_sla_trackers WHERE {TrackerWhere}", connection, transaction);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static StoredSlaPolicy ReadPolicy(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported policy format version.");
        return new StoredSlaPolicy(scope, SlaPolicyCodec.Deserialize(new SlaPolicyId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
    }, "policy");

    private static StoredSlaTracker ReadTracker(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported tracker format version.");
        var tracker = SlaTrackerCodec.Deserialize(reader.GetString(2));
        if (tracker.Conversation.Value != reader.GetString(0))
            throw new FormatException("The tracker state belongs to a different conversation.");
        return new StoredSlaTracker(scope, tracker, reader.GetInt64(3));
    }, "tracker");

    private static string SerializeAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in attributes)
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value?.ToUniversalTime() ?? DBNull.Value });

    private static void RequireId(SlaPolicyId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A policy identifier is required.", nameof(id));
    }

    private static void RequireId(ConversationId id)
    {
        if (id == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(id));
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException($"A stored SLA {description} is invalid or incompatible.", error);
        }
    }
}
