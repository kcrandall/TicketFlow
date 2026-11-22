using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Routing.Stores.Postgres;

public sealed partial class PostgresRoutingStore
{
    private const string AgentColumns = "agent_id, format_version, default_capacity, settings, status, changed_at, last_heartbeat_at, expires_at, reassign_pending, revision";
    private const string AgentKey = "agent_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@agent) AND agent_id=@agent AND " + ScopeWhere;

    public async Task<StoredAgentProfile?> GetAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAgentAsync(connection, null, scope, agent, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredAgentProfile>> ListAsync(TicketScope scope, UserId? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumList);
        await using var command = source.CreateCommand($"""
            SELECT {AgentColumns} FROM tf_routing_agents
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND (@after::text IS NULL OR agent_id > @after COLLATE "C")
            ORDER BY agent_id LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "after", after?.Value);
        AddInt(command, "limit", limit);
        return await ReadAgentsAsync(command, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredAgentProfile>> ListAvailableAsync(TicketScope scope, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumAvailableList);
        await using var command = source.CreateCommand($"""
            SELECT {AgentColumns} FROM tf_routing_agents
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND status=1 AND expires_at > @now
            ORDER BY agent_id LIMIT @limit
            """);
        AddScope(command, scope);
        AddInstant(command, "now", Now);
        AddInt(command, "limit", limit);
        return await ReadAgentsAsync(command, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoutingWriteResult<StoredAgentProfile>> SaveSettingsAsync(TicketScope scope, AgentRoutingSettings settings, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var now = Now;
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (expectedRevision is null)
            await LockScopeAsync(connection, transaction, "agents", scope, cancellationToken).ConfigureAwait(false);
        var existing = await LoadAgentAsync(connection, transaction, scope, settings.Agent, true, cancellationToken).ConfigureAwait(false);
        StoredAgentProfile stored;
        if (expectedRevision is null)
        {
            if (existing is not null)
                return new RoutingWriteConflict(existing.Revision);
            if (await CountAsync(connection, transaction, "tf_routing_agents", scope, cancellationToken).ConfigureAwait(false) >= IAgentAvailabilityStore.MaximumAgentsPerWorkspace)
                return new RoutingWriteRejected(RoutingWriteRejection.CapacityExceeded);
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO tf_routing_agents({ScopeColumns}, agent_id, format_version, default_capacity, settings, status, changed_at, last_heartbeat_at, expires_at,
                    reassign_pending, reassign_fence, reassign_lease_until, revision)
                VALUES({ScopeValues}, @agent, 1, @capacity, @settings, 4, @now, NULL, NULL, false, 0, NULL, 1)
                """, connection, transaction);
            AddScope(insert, scope);
            AddSettings(insert, settings);
            AddInstant(insert, "now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            stored = new StoredAgentProfile(scope, settings, AgentPresence.Offline(now), false, 1);
        }
        else
        {
            if (existing is null)
                return RoutingWriteNotFound.Instance;
            if (existing.Revision != expectedRevision)
                return new RoutingWriteConflict(existing.Revision);
            await using var update = new NpgsqlCommand($"UPDATE tf_routing_agents SET default_capacity=@capacity, settings=@settings, revision=revision+1 WHERE {AgentKey}",
                connection, transaction);
            AddScope(update, scope);
            AddSettings(update, settings);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            stored = existing with { Settings = settings, Revision = existing.Revision + 1 };
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoutingWriteApplied<StoredAgentProfile>(stored);
    }

    public async Task<RoutingWriteResult<StoredAgentProfile>> SetStatusAsync(TicketScope scope, UserId agent, AgentStatus status, TimeSpan presenceTtl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        RequireTtl(presenceTtl);
        var now = Now;
        var offline = status == AgentStatus.Offline;
        await using var command = source.CreateCommand($"""
            UPDATE tf_routing_agents SET status=@status, changed_at=@now,
                last_heartbeat_at=CASE WHEN @offline THEN NULL ELSE @now END,
                expires_at=CASE WHEN @offline THEN NULL ELSE @expires END,
                reassign_pending=reassign_pending OR COALESCE(@offline AND status <> 4 AND expires_at > @now, false),
                revision=revision+1
            WHERE {AgentKey}
            RETURNING {AgentColumns}
            """);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        command.Parameters.AddWithValue("status", (short)status);
        command.Parameters.AddWithValue("offline", offline);
        AddInstant(command, "now", now);
        AddInstant(command, "expires", now + presenceTtl);
        var rows = await ReadAgentsAsync(command, scope, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? RoutingWriteNotFound.Instance : new RoutingWriteApplied<StoredAgentProfile>(rows[0]);
    }

    public async Task<RoutingWriteResult<StoredAgentProfile>> HeartbeatAsync(TicketScope scope, UserId agent, TimeSpan presenceTtl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        RequireTtl(presenceTtl);
        var now = Now;
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            UPDATE tf_routing_agents SET last_heartbeat_at=@now, expires_at=@expires WHERE {AgentKey} AND status <> 4 AND expires_at > @now
            """, connection))
        {
            AddScope(command, scope);
            AddText(command, "agent", agent.Value);
            AddInstant(command, "now", now);
            AddInstant(command, "expires", now + presenceTtl);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return await LoadAgentAsync(connection, null, scope, agent, false, cancellationToken).ConfigureAwait(false) is { } profile
            ? new RoutingWriteApplied<StoredAgentProfile>(profile)
            : RoutingWriteNotFound.Instance;
    }

    public async Task<RoutingWriteResult<StoredAgentProfile>> DeleteAsync(TicketScope scope, UserId agent, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await LoadAgentAsync(connection, transaction, scope, agent, true, cancellationToken).ConfigureAwait(false) is not { } existing)
            return RoutingWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new RoutingWriteConflict(existing.Revision);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_routing_agents WHERE {AgentKey}", connection, transaction))
        {
            AddScope(delete, scope);
            AddText(delete, "agent", agent.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoutingWriteApplied<StoredAgentProfile>(existing);
    }

    public async Task<IReadOnlyList<AgentPresenceExpired>> ExpirePresenceAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumAvailableList);
        var now = Now;
        await using var command = source.CreateCommand($"""
            WITH due AS (
                SELECT agent_key FROM tf_routing_agents WHERE status <> 4 AND expires_at <= @now
                ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_routing_agents a SET status=4, changed_at=@now, last_heartbeat_at=NULL, expires_at=NULL, reassign_pending=true, revision=a.revision+1
            FROM due WHERE a.agent_key=due.agent_key
            RETURNING a.{ScopeColumns.Replace(", ", ", a.", StringComparison.Ordinal)}, a.agent_id
            """);
        AddInstant(command, "now", now);
        AddInt(command, "limit", limit);
        var expired = new List<AgentPresenceExpired>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            expired.Add(new AgentPresenceExpired(ReadScope(reader, 0), new UserId(reader.GetString(5)), now));
        return expired;
    }

    public async Task<IReadOnlyList<AgentReassignmentClaim>> ClaimReassignmentsAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingQueueStore.MaximumClaim);
        RequireLease(lease);
        var now = Now;
        await using var command = source.CreateCommand($"""
            WITH due AS (
                SELECT agent_key FROM tf_routing_agents WHERE reassign_pending AND (reassign_lease_until IS NULL OR reassign_lease_until <= @now)
                ORDER BY agent_key LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_routing_agents a SET reassign_fence=nextval('tf_routing_fences'), reassign_lease_until=@until
            FROM due WHERE a.agent_key=due.agent_key
            RETURNING a.{ScopeColumns.Replace(", ", ", a.", StringComparison.Ordinal)}, a.agent_id, a.reassign_fence
            """);
        AddInstant(command, "now", now);
        AddInstant(command, "until", now + lease);
        AddInt(command, "limit", limit);
        var claims = new List<AgentReassignmentClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            claims.Add(new AgentReassignmentClaim(ReadScope(reader, 0), new UserId(reader.GetString(5)), reader.GetInt64(6), now + lease));
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

    private static async Task<StoredAgentProfile?> LoadAgentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, UserId agent, bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {AgentColumns} FROM tf_routing_agents WHERE {AgentKey}{(forUpdate ? " FOR UPDATE" : "")}", connection, transaction);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        var rows = await ReadAgentsAsync(command, scope, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async Task<List<StoredAgentProfile>> ReadAgentsAsync(NpgsqlCommand command, TicketScope scope, CancellationToken cancellationToken)
    {
        var rows = new List<StoredAgentProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(Decode(() =>
            {
                if (reader.GetInt16(1) != 1)
                    throw new FormatException("Unsupported agent format version.");
                var status = (AgentStatus)reader.GetInt16(4);
                if (!Enum.IsDefined(status))
                    throw new FormatException("Unknown agent status.");
                var agent = new UserId(reader.GetString(0));
                return new StoredAgentProfile(scope, AgentSettingsCodec.Deserialize(agent, reader.GetInt32(2), reader.GetString(3)),
                    new AgentPresence(status, reader.GetFieldValue<DateTimeOffset>(5), NullableInstant(reader, 6), NullableInstant(reader, 7)),
                    reader.GetBoolean(8), reader.GetInt64(9));
            }, "agent profile"));
        return rows;
    }
}
