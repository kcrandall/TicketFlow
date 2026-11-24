using System.Data;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Routing.Stores.Postgres;

public sealed partial class PostgresRoutingStore
{
    private const string AgentColumns = "agent_id, format_version, default_capacity, settings, status, changed_at, last_heartbeat_at, expires_at, reassign_pending, revision";
    private const string AgentKey = "agent_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@agent) AND agent_id=@agent AND " + ScopeWhere;

    private const string CreateAgentSql = $"""
        WITH clk AS (SELECT {NowSql} AS now),
        existing AS (
            SELECT revision FROM tf_routing_agents WHERE {AgentKey}
        ),
        inserted AS (
            INSERT INTO tf_routing_agents({ScopeColumns}, agent_id, format_version, default_capacity, settings, status, changed_at, last_heartbeat_at, expires_at,
                reassign_pending, reassign_fence, reassign_lease_until, revision)
            SELECT {ScopeValues}, @agent, 1, @capacity, @settings, 4, (SELECT now FROM clk), NULL, NULL, false, 0, NULL, 1
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_routing_agents WHERE scope_key={ScopeKey} AND {ScopeWhere}) < @max
            RETURNING revision, changed_at
        )
        SELECT (SELECT revision FROM inserted), (SELECT changed_at FROM inserted), (SELECT revision FROM existing)
        """;

    public async Task<StoredAgentProfile?> GetAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        await using var command = source.CreateCommand($"SELECT {AgentColumns} FROM tf_routing_agents WHERE {AgentKey}");
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAgent(reader, scope) : null;
    }

    public async Task<IReadOnlyList<StoredAgentProfile>> ListAsync(TicketScope scope, UserId? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumList);
        var sql = after is null
            ? $"SELECT {AgentColumns} FROM tf_routing_agents WHERE scope_key={ScopeKey} AND {ScopeWhere} ORDER BY agent_id LIMIT @limit"
            : $"SELECT {AgentColumns} FROM tf_routing_agents WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id > @after COLLATE \"C\" ORDER BY agent_id LIMIT @limit";
        await using var command = source.CreateCommand(sql);
        AddScope(command, scope);
        if (after is { } cursor)
            AddText(command, "after", cursor.Value);
        AddInt(command, "limit", limit);
        return await ReadAgentsAsync(command, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredAgentProfile>> ListAvailableAsync(TicketScope scope, AvailableAgentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        // Conditional clauses match tf_routing_agents_online (scope_key, agent_id) WHERE status = 1 and parse settings::jsonb at most once per row.
        var sql = new StringBuilder(512);
        sql.Append("SELECT ").Append(AgentColumns).Append(" FROM tf_routing_agents a CROSS JOIN LATERAL (SELECT a.settings::jsonb AS doc) s WHERE a.scope_key=")
            .Append(ScopeKey).Append(" AND a.tenant_id=@tenant AND a.project_id=@project AND a.environment_id=@environment AND a.app_id=@app AND a.workspace_id=@workspace AND a.status=1 AND a.expires_at > ")
            .Append(NowSql);
        if (query.After is not null)
            sql.Append(" AND a.agent_id > @after COLLATE \"C\"");
        if (query.Agents is not null)
            sql.Append(" AND a.agent_id = ANY(@agents)");
        sql.Append(" AND COALESCE((SELECT (c->>'max')::integer FROM jsonb_array_elements(s.doc->'capacities') c WHERE c->>'kind'=@channel LIMIT 1), a.default_capacity) > 0");
        if (query.RequiredSkills.Count > 0)
            sql.Append(" AND NOT EXISTS (SELECT 1 FROM unnest(@skill_names::text[], @skill_levels::integer[]) r(name, level) WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(s.doc->'skills') sk WHERE sk->>'name'=r.name AND (sk->>'level')::integer >= r.level))");
        sql.Append(" ORDER BY a.agent_id LIMIT @limit");
        await using var command = source.CreateCommand(sql.ToString());
        AddScope(command, scope);
        AddInstant(command, "now", OverrideNow());
        if (query.After is { } after)
            AddText(command, "after", after.Value);
        if (query.Agents is { } agents)
        {
            var agentValues = new string[agents.Count];
            for (var i = 0; i < agents.Count; i++)
                agentValues[i] = agents[i].Value;
            AddTextArray(command, "agents", agentValues);
        }
        AddText(command, "channel", query.Channel.Value);
        if (query.RequiredSkills.Count > 0)
        {
            var names = new string[query.RequiredSkills.Count];
            var levels = new int[query.RequiredSkills.Count];
            var idx = 0;
            foreach (var (name, level) in query.RequiredSkills)
            {
                names[idx] = name;
                levels[idx] = level;
                idx++;
            }
            AddTextArray(command, "skill_names", names);
            command.Parameters.Add(new NpgsqlParameter<int[]>("skill_levels", NpgsqlDbType.Array | NpgsqlDbType.Integer) { TypedValue = levels });
        }
        AddInt(command, "limit", query.Limit);
        return await ReadAgentsAsync(command, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> SaveSettingsAsync(TicketScope scope, AgentRoutingSettings settings, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (expectedRevision is null)
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await LockScopeAsync(connection, transaction, "agents", scope, cancellationToken).ConfigureAwait(false);
            long? insertedRevision;
            DateTimeOffset? changedAt;
            long? existingRevision;
            await using (var create = new NpgsqlCommand(CreateAgentSql, connection, transaction))
            {
                AddScope(create, scope);
                AddSettings(create, settings);
                AddInstant(create, "now", OverrideNow());
                AddLong(create, "max", IAgentAvailabilityStore.MaximumAgentsPerWorkspace);
                await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                insertedRevision = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                changedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
                existingRevision = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            }
            if (insertedRevision is { } rev)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StoreApplied<StoredAgentProfile>(new StoredAgentProfile(scope, settings, AgentPresence.Offline(changedAt!.Value), false, rev));
            }
            return existingRevision is { } existing
                ? new StoreConflict(existing)
                : new StoreRejected<RoutingWriteRejection>(RoutingWriteRejection.CapacityExceeded);
        }
        else
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (var update = new NpgsqlCommand($"""
                UPDATE tf_routing_agents SET default_capacity=@capacity, settings=@settings, revision=revision+1
                WHERE {AgentKey} AND revision=@expected
                RETURNING {AgentColumns}
                """, connection))
            {
                AddScope(update, scope);
                AddSettings(update, settings);
                AddLong(update, "expected", expectedRevision.Value);
                await using var reader = await update.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return new StoreApplied<StoredAgentProfile>(ReadAgent(reader, scope));
            }
            return await LoadAgentRevisionAsync(connection, scope, settings.Agent, cancellationToken).ConfigureAwait(false) is { } current
                ? new StoreConflict(current)
                : StoreNotFound.Instance;
        }
    }

    public async Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> SetStatusAsync(TicketScope scope, UserId agent, AgentStatus status, TimeSpan presenceTtl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        RequireTtl(presenceTtl);
        var offline = status == AgentStatus.Offline;
        await using var command = source.CreateCommand($"""
            WITH clk AS (SELECT {NowSql} AS now)
            UPDATE tf_routing_agents SET status=@status, changed_at=(SELECT now FROM clk),
                last_heartbeat_at=CASE WHEN @offline THEN NULL ELSE (SELECT now FROM clk) END,
                expires_at=CASE WHEN @offline THEN NULL ELSE (SELECT now FROM clk) + @ttl END,
                reassign_pending=reassign_pending OR COALESCE(@offline AND status <> 4 AND expires_at > (SELECT now FROM clk), false),
                revision=revision+1
            WHERE {AgentKey}
            RETURNING {AgentColumns}
            """);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        AddShort(command, "status", (short)status);
        AddBool(command, "offline", offline);
        AddInstant(command, "now", OverrideNow());
        AddInterval(command, "ttl", presenceTtl);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoreApplied<StoredAgentProfile>(ReadAgent(reader, scope))
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> HeartbeatAsync(TicketScope scope, UserId agent, TimeSpan presenceTtl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        RequireTtl(presenceTtl);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            WITH clk AS (SELECT {NowSql} AS now)
            UPDATE tf_routing_agents SET last_heartbeat_at=(SELECT now FROM clk), expires_at=(SELECT now FROM clk) + @ttl
            WHERE {AgentKey} AND status <> 4 AND expires_at > (SELECT now FROM clk)
            RETURNING {AgentColumns}
            """, connection))
        {
            AddScope(command, scope);
            AddText(command, "agent", agent.Value);
            AddInstant(command, "now", OverrideNow());
            AddInterval(command, "ttl", presenceTtl);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredAgentProfile>(ReadAgent(reader, scope));
        }
        return await LoadAgentAsync(connection, null, scope, agent, false, cancellationToken).ConfigureAwait(false) is { } profile
            ? new StoreApplied<StoredAgentProfile>(profile)
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredAgentProfile, RoutingWriteRejection>> DeleteAsync(TicketScope scope, UserId agent, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_routing_agents WHERE {AgentKey} AND revision=@expected RETURNING {AgentColumns}", connection))
        {
            AddScope(delete, scope);
            AddText(delete, "agent", agent.Value);
            AddLong(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredAgentProfile>(ReadAgent(reader, scope));
        }
        return await LoadAgentRevisionAsync(connection, scope, agent, cancellationToken).ConfigureAwait(false) is { } current
            ? new StoreConflict(current)
            : StoreNotFound.Instance;
    }

    public async Task<IReadOnlyList<AgentPresenceExpired>> ExpirePresenceAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumAvailableList);
        await using var command = source.CreateCommand($"""
            WITH clk AS (SELECT {NowSql} AS now),
            due AS (
                SELECT agent_key FROM tf_routing_agents WHERE status <> 4 AND expires_at <= (SELECT now FROM clk)
                ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_routing_agents a SET status=4, changed_at=(SELECT now FROM clk), last_heartbeat_at=NULL, expires_at=NULL, reassign_pending=true, revision=a.revision+1
            FROM due WHERE a.agent_key=due.agent_key
            RETURNING {ScopeColumnsA}, a.agent_id, a.changed_at
            """);
        AddInstant(command, "now", OverrideNow());
        AddInt(command, "limit", limit);
        var expired = new List<AgentPresenceExpired>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            expired.Add(new AgentPresenceExpired(ReadScope(reader, 0), new UserId(reader.GetString(5)), Instant(reader.GetFieldValue<DateTimeOffset>(6))));
        return expired;
    }

    public async Task<IReadOnlyList<AgentReassignmentClaim>> ClaimReassignmentsAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingQueueStore.MaximumClaim);
        RequireLease(lease);
        await using var command = source.CreateCommand($"""
            WITH clk AS (SELECT {NowSql} AS now),
            due AS (
                SELECT agent_key FROM tf_routing_agents WHERE reassign_pending AND (reassign_lease_until IS NULL OR reassign_lease_until <= (SELECT now FROM clk))
                ORDER BY agent_key LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_routing_agents a SET reassign_fence=nextval('tf_routing_fences'), reassign_lease_until=(SELECT now FROM clk) + @lease
            FROM due WHERE a.agent_key=due.agent_key
            RETURNING {ScopeColumnsA}, a.agent_id, a.reassign_fence, a.reassign_lease_until
            """);
        AddInstant(command, "now", OverrideNow());
        AddInterval(command, "lease", lease);
        AddInt(command, "limit", limit);
        var claims = new List<AgentReassignmentClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            claims.Add(new AgentReassignmentClaim(ReadScope(reader, 0), new UserId(reader.GetString(5)), reader.GetInt64(6), Instant(reader.GetFieldValue<DateTimeOffset>(7))));
        return claims;
    }

    public async Task<bool> CompleteReassignmentAsync(TicketScope scope, UserId agent, long fence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        await using var command = source.CreateCommand($"""
            UPDATE tf_routing_agents SET reassign_pending=false, reassign_lease_until=NULL
            WHERE {AgentKey} AND reassign_pending AND reassign_fence=@fence
            """);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        AddLong(command, "fence", fence);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void AddSettings(NpgsqlCommand command, AgentRoutingSettings settings)
    {
        AddText(command, "agent", settings.Agent.Value);
        AddInt(command, "capacity", settings.DefaultCapacity);
        AddText(command, "settings", AgentSettingsCodec.Serialize(settings));
    }

    private static async Task<long?> LoadAgentRevisionAsync(NpgsqlConnection connection, TicketScope scope, UserId agent, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_routing_agents WHERE {AgentKey}", connection);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static async Task<StoredAgentProfile?> LoadAgentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, UserId agent, bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {AgentColumns} FROM tf_routing_agents WHERE {AgentKey}{(forUpdate ? " FOR UPDATE" : "")}", connection, transaction);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAgent(reader, scope) : null;
    }

    private static async Task<List<StoredAgentProfile>> ReadAgentsAsync(NpgsqlCommand command, TicketScope scope, CancellationToken cancellationToken)
    {
        var rows = new List<StoredAgentProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadAgent(reader, scope));
        return rows;
    }

    private static StoredAgentProfile ReadAgent(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported agent format version.");
            var status = (AgentStatus)reader.GetInt16(4);
            if (!Enum.IsDefined(status))
                throw new FormatException("Unknown agent status.");
            var agent = new UserId(reader.GetString(0));
            return new StoredAgentProfile(scope, AgentSettingsCodec.Deserialize(agent, reader.GetInt32(2), reader.GetString(3)),
                new AgentPresence(status, reader.GetFieldValue<DateTimeOffset>(5), reader.NullableInstant(6), reader.NullableInstant(7)),
                reader.GetBoolean(8), reader.GetInt64(9));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("A stored routing agent profile is invalid or incompatible.", error);
        }
    }
}
