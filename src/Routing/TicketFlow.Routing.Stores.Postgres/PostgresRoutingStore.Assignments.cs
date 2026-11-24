using System.Data;
using Npgsql;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.Stores.Postgres;

public sealed partial class PostgresRoutingStore
{
    private const string AssignmentColumns = "conversation_id, format_version, agent_id, inbox_id, contact_id, channel_kind, state, reserved_until, conversation_revision, updated_at";
    private const string AssignmentKey = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;
    private const string AnchorKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace,'routing-load',a,@channel)";
    private const int MaximumAnchorCacheEntries = 50_000;

    public async Task<RoutingReservationResult> ReserveAsync(TicketScope scope, RoutingReservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        var candidates = request.Candidates;
        var agents = new string[candidates.Count];
        List<string>? missingAnchors = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var agent = candidates[i].Agent;
            agents[i] = agent.Value;
            if (!knownAnchors.ContainsKey((scope, agent, request.Channel)))
            {
                missingAnchors ??= [];
                missingAnchors.Add(agent.Value);
            }
        }
        var cursorKnown = knownCursors.ContainsKey((scope, request.CursorKey));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        for (var anchorAttempt = 0; anchorAttempt < 2; anchorAttempt++)
        {
            if (missingAnchors is not null || !cursorKnown)
            {
                await EnsureAnchorsAndCursorAsync(connection, scope, missingAnchors, !cursorKnown, request.Channel, request.CursorKey, cancellationToken).ConfigureAwait(false);
                if (knownAnchors.Count < MaximumAnchorCacheEntries && missingAnchors is not null)
                    for (var i = 0; i < candidates.Count; i++)
                        knownAnchors.TryAdd((scope, candidates[i].Agent, request.Channel), 0);
                if (knownCursors.Count < MaximumAnchorCacheEntries && !cursorKnown)
                    knownCursors.TryAdd((scope, request.CursorKey), 0);
                missingAnchors = null;
                cursorKnown = true;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var overrideNow = OverrideNow();

            // Pipeline cursor lock, anchor locks (in key order), existing assignment check, and agent load counts in 1 RTT.
            string? cursor = null;
            DateTimeOffset now = default;
            var cursorFound = false;
            var anchorCount = 0;
            RoutingAssignment? existing = null;
            var loads = new Dictionary<UserId, int>(candidates.Count);

            await using (var batch = new NpgsqlBatch(connection, transaction))
            {
                var lockCursor = new NpgsqlBatchCommand($"""
                    SELECT last_agent_id, COALESCE(@now, transaction_timestamp()) FROM tf_routing_cursors
                    WHERE cursor_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@cursor) AND cursor_name=@cursor AND {ScopeWhere} FOR UPDATE
                    """);
                AddScope(lockCursor.Parameters, scope);
                AddText(lockCursor.Parameters, "cursor", request.CursorKey);
                AddInstant(lockCursor.Parameters, "now", overrideNow);
                batch.BatchCommands.Add(lockCursor);

                var lockAnchors = new NpgsqlBatchCommand($"""
                    SELECT count(*) FROM (
                        SELECT anchor_key FROM tf_routing_load_anchors WHERE anchor_key IN (SELECT {AnchorKey} FROM unnest(@agents) a) ORDER BY anchor_key FOR UPDATE
                    ) locked
                    """);
                AddScope(lockAnchors.Parameters, scope);
                AddTextArray(lockAnchors.Parameters, "agents", agents);
                AddText(lockAnchors.Parameters, "channel", request.Channel.Value);
                batch.BatchCommands.Add(lockAnchors);

                var loadExisting = new NpgsqlBatchCommand($"SELECT {AssignmentColumns} FROM tf_routing_assignments WHERE {AssignmentKey} FOR UPDATE");
                AddScope(loadExisting.Parameters, scope);
                AddText(loadExisting.Parameters, "conversation", request.Conversation.Value);
                batch.BatchCommands.Add(loadExisting);

                var countLoads = new NpgsqlBatchCommand($"""
                    SELECT agent_id, count(*) FROM tf_routing_assignments
                    WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id = ANY(@agents) AND channel_kind=@channel AND conversation_id <> @conversation
                      AND (state=2 OR reserved_until > COALESCE(@now, transaction_timestamp()))
                    GROUP BY agent_id
                    """);
                AddScope(countLoads.Parameters, scope);
                AddTextArray(countLoads.Parameters, "agents", agents);
                AddText(countLoads.Parameters, "channel", request.Channel.Value);
                AddText(countLoads.Parameters, "conversation", request.Conversation.Value);
                AddInstant(countLoads.Parameters, "now", overrideNow);
                batch.BatchCommands.Add(countLoads);

                await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cursorFound = true;
                    cursor = reader.IsDBNull(0) ? null : reader.GetString(0);
                    now = Instant(reader.GetFieldValue<DateTimeOffset>(1));
                }
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    anchorCount = checked((int)reader.GetInt64(0));
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    existing = ReadAssignment(reader);
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    loads[new UserId(reader.GetString(0))] = checked((int)reader.GetInt64(1));
            }

            // If a cached cursor/anchor row was missing (e.g. external table wipe), re-create and retry once.
            if (!cursorFound || anchorCount < agents.Length)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                knownCursors.TryRemove((scope, request.CursorKey), out _);
                for (var i = 0; i < candidates.Count; i++)
                    knownAnchors.TryRemove((scope, candidates[i].Agent, request.Channel), out _);
                missingAnchors = [.. agents];
                cursorKnown = false;
                continue;
            }

            if (existing is not null && Blocks(existing, request.ConversationRevision, now))
                return new RoutingAlreadyReserved(existing);

            var chosen = request.Select(loads, cursor);
            if (chosen is null)
                return RoutingNoCapacity.Instance;

            var reservation = new RoutingAssignment(request.Conversation, chosen.Agent, request.Inbox, request.Contact, request.Channel, RoutingAssignmentState.Reserved,
                now + request.Lease, request.ConversationRevision, now);
            await using (var write = new NpgsqlCommand($"""
                WITH upserted AS (
                    INSERT INTO tf_routing_assignments({ScopeColumns}, conversation_id, format_version, agent_id, inbox_id, contact_id, channel_kind, state, reserved_until,
                        conversation_revision, updated_at)
                    VALUES({ScopeValues}, @conversation, 1, @agent, @inbox, @contact, @channel, 1, @until, @revision, @now)
                    ON CONFLICT (conversation_key) DO UPDATE SET agent_id=EXCLUDED.agent_id, inbox_id=EXCLUDED.inbox_id, contact_id=EXCLUDED.contact_id,
                        channel_kind=EXCLUDED.channel_kind, state=1, reserved_until=EXCLUDED.reserved_until, conversation_revision=EXCLUDED.conversation_revision,
                        updated_at=EXCLUDED.updated_at
                    WHERE NOT ((tf_routing_assignments.state=1 AND tf_routing_assignments.reserved_until > @now)
                        OR (tf_routing_assignments.state=2 AND tf_routing_assignments.conversation_revision > @revision))
                    RETURNING 1
                ),
                moved AS (
                    UPDATE tf_routing_cursors SET last_agent_id=@agent
                    FROM upserted
                    WHERE cursor_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@cursor) AND cursor_name=@cursor AND {ScopeWhere}
                )
                SELECT count(*) FROM upserted
                """, connection, transaction))
            {
                AddScope(write, scope);
                AddText(write, "conversation", request.Conversation.Value);
                AddText(write, "agent", chosen.Agent.Value);
                AddText(write, "inbox", request.Inbox.Value);
                AddText(write, "contact", request.Contact.Value);
                AddText(write, "channel", request.Channel.Value);
                AddInstant(write, "until", reservation.ReservedUntil);
                AddLong(write, "revision", request.ConversationRevision);
                AddInstant(write, "now", now);
                AddText(write, "cursor", request.CursorKey);
                if ((long)(await write.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 0)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return await GetAsync(scope, request.Conversation, cancellationToken).ConfigureAwait(false) is { } winner
                        ? new RoutingAlreadyReserved(winner)
                        : RoutingNoCapacity.Instance;
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RoutingReserved(reservation, loads.GetValueOrDefault(chosen.Agent) + 1);
        }
        throw new InvalidOperationException("Routing anchors could not be initialized.");
    }

    private static async Task EnsureAnchorsAndCursorAsync(NpgsqlConnection connection, TicketScope scope, List<string>? missingAnchors, bool ensureCursor,
        ChannelKind channel, string cursorKey, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection);
        if (missingAnchors is { Count: > 0 })
        {
            var insertAnchors = new NpgsqlBatchCommand($"""
                INSERT INTO tf_routing_load_anchors(anchor_key) SELECT k FROM (SELECT DISTINCT {AnchorKey} AS k FROM unnest(@agents) a) keys ORDER BY k ON CONFLICT DO NOTHING
                """);
            AddScope(insertAnchors.Parameters, scope);
            AddTextArray(insertAnchors.Parameters, "agents", [.. missingAnchors]);
            AddText(insertAnchors.Parameters, "channel", channel.Value);
            batch.BatchCommands.Add(insertAnchors);
        }
        if (ensureCursor)
        {
            var insertCursor = new NpgsqlBatchCommand($"""
                INSERT INTO tf_routing_cursors({ScopeColumns}, cursor_name, last_agent_id) VALUES({ScopeValues}, @cursor, NULL) ON CONFLICT DO NOTHING
                """);
            AddScope(insertCursor.Parameters, scope);
            AddText(insertCursor.Parameters, "cursor", cursorKey);
            batch.BatchCommands.Add(insertCursor);
        }
        if (batch.BatchCommands.Count > 0)
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConfirmAsync(TicketScope scope, ConversationId conversation, UserId agent, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(conversationRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var confirm = new NpgsqlCommand($"""
            WITH clk AS (SELECT {NowSql} AS now),
            updated AS (
                UPDATE tf_routing_assignments SET state=2, reserved_until=NULL, conversation_revision=@revision, updated_at=(SELECT now FROM clk)
                WHERE {AssignmentKey} AND agent_id=@agent AND conversation_revision <= @revision
                  AND (state=1 OR conversation_revision < @revision)
                RETURNING contact_id
            ),
            sticky AS (
                INSERT INTO tf_routing_sticky_owners({ScopeColumns}, contact_id, agent_id, assigned_at)
                SELECT {ScopeValues}, u.contact_id, @agent, (SELECT now FROM clk) FROM updated u
                ON CONFLICT (contact_key) DO UPDATE SET agent_id=EXCLUDED.agent_id, assigned_at=EXCLUDED.assigned_at
            )
            SELECT EXISTS (SELECT 1 FROM updated)
            """, connection))
        {
            AddScope(confirm, scope);
            AddText(confirm, "conversation", conversation.Value);
            AddText(confirm, "agent", agent.Value);
            AddLong(confirm, "revision", conversationRevision);
            AddInstant(confirm, "now", OverrideNow());
            if (await confirm.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                return true;
        }
        // Idempotent confirm of already-confirmed same agent & revision returns true; missing/mismatched returns false.
        await using var check = new NpgsqlCommand($"SELECT 1 FROM tf_routing_assignments WHERE {AssignmentKey} AND agent_id=@agent AND conversation_revision <= @revision", connection);
        AddScope(check, scope);
        AddText(check, "conversation", conversation.Value);
        AddText(check, "agent", agent.Value);
        AddLong(check, "revision", conversationRevision);
        return await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<bool> ReleaseAsync(TicketScope scope, ConversationId conversation, UserId agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(agent);
        await using var command = source.CreateCommand($"DELETE FROM tf_routing_assignments WHERE {AssignmentKey} AND agent_id=@agent AND state=1");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "agent", agent.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task RecordAssignedAsync(TicketScope scope, RoutingAssignmentRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(record);
        await using var command = source.CreateCommand($"""
            WITH clk AS (SELECT {NowSql} AS now),
            upserted AS (
                INSERT INTO tf_routing_assignments({ScopeColumns}, conversation_id, format_version, agent_id, inbox_id, contact_id, channel_kind, state, reserved_until,
                    conversation_revision, updated_at)
                VALUES({ScopeValues}, @conversation, 1, @agent, @inbox, @contact, @channel, 2, NULL, @revision, (SELECT now FROM clk))
                ON CONFLICT (conversation_key) DO UPDATE SET agent_id=EXCLUDED.agent_id, inbox_id=EXCLUDED.inbox_id, contact_id=EXCLUDED.contact_id,
                    channel_kind=EXCLUDED.channel_kind, state=2, reserved_until=NULL, conversation_revision=EXCLUDED.conversation_revision, updated_at=EXCLUDED.updated_at
                WHERE tf_routing_assignments.conversation_revision <= @revision
                  AND NOT (tf_routing_assignments.state=2 AND tf_routing_assignments.agent_id=@agent AND tf_routing_assignments.channel_kind=@channel)
                RETURNING 1
            )
            INSERT INTO tf_routing_sticky_owners({ScopeColumns}, contact_id, agent_id, assigned_at)
            SELECT {ScopeValues}, @contact, @agent, (SELECT now FROM clk) FROM upserted
            ON CONFLICT (contact_key) DO UPDATE SET agent_id=EXCLUDED.agent_id, assigned_at=EXCLUDED.assigned_at
            """);
        AddScope(command, scope);
        AddText(command, "conversation", record.Conversation.Value);
        AddText(command, "agent", record.Agent.Value);
        AddText(command, "inbox", record.Inbox.Value);
        AddText(command, "contact", record.Contact.Value);
        AddText(command, "channel", record.Channel.Value);
        AddLong(command, "revision", record.ConversationRevision);
        AddInstant(command, "now", OverrideNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ClearAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"""
            DELETE FROM tf_routing_assignments WHERE {AssignmentKey} AND conversation_revision <= @revision AND NOT (state=1 AND reserved_until > {NowSql})
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddLong(command, "revision", conversationRevision);
        AddInstant(command, "now", OverrideNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<RoutingAssignment?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"SELECT {AssignmentColumns} FROM tf_routing_assignments WHERE {AssignmentKey}");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAssignment(reader) : null;
    }

    public async Task<IReadOnlyList<RoutingAssignment>> ListForAgentAsync(TicketScope scope, UserId agent, ConversationId? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingAssignmentStore.MaximumList);
        var sql = after is null
            ? $"SELECT {AssignmentColumns} FROM tf_routing_assignments WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id=@agent ORDER BY conversation_id LIMIT @limit"
            : $"SELECT {AssignmentColumns} FROM tf_routing_assignments WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id=@agent AND conversation_id > @after COLLATE \"C\" ORDER BY conversation_id LIMIT @limit";
        await using var command = source.CreateCommand(sql);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        if (after is { } cursor)
            AddText(command, "after", cursor.Value);
        AddInt(command, "limit", limit);
        var rows = new List<RoutingAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadAssignment(reader));
        return rows;
    }

    public async Task<IReadOnlyDictionary<ChannelKind, int>> GetLoadAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        await using var command = source.CreateCommand($"""
            SELECT channel_kind, count(*) FROM tf_routing_assignments
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id=@agent AND (state=2 OR reserved_until > {NowSql})
            GROUP BY channel_kind
            """);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        AddInstant(command, "now", OverrideNow());
        var loads = new Dictionary<ChannelKind, int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            loads[new ChannelKind(reader.GetString(0))] = checked((int)reader.GetInt64(1));
        return loads;
    }

    public async Task<StickyOwner?> GetStickyOwnerAsync(TicketScope scope, ContactId contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (contact == default)
            throw new ArgumentException("A contact is required.", nameof(contact));
        await using var command = source.CreateCommand($"""
            SELECT agent_id, assigned_at FROM tf_routing_sticky_owners
            WHERE contact_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@contact) AND contact_id=@contact AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "contact", contact.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadStickyOwner(reader, contact) : null;
    }

    public async Task<string?> GetCursorAsync(TicketScope scope, string cursorKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        TicketScope.RequireIdentifier(cursorKey, nameof(cursorKey));
        await using var command = source.CreateCommand($"""
            SELECT last_agent_id FROM tf_routing_cursors
            WHERE cursor_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@cursor) AND cursor_name=@cursor AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "cursor", cursorKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static bool Blocks(RoutingAssignment existing, long revision, DateTimeOffset now) =>
        (existing.State == RoutingAssignmentState.Reserved && existing.CountsAt(now))
        || (existing.State == RoutingAssignmentState.Assigned && existing.ConversationRevision > revision);

    private static StickyOwner ReadStickyOwner(NpgsqlDataReader reader, ContactId contact)
    {
        try
        {
            return new StickyOwner(contact, new UserId(reader.GetString(0)), reader.GetFieldValue<DateTimeOffset>(1));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored routing sticky owner is invalid or incompatible.", error);
        }
    }

    private static RoutingAssignment ReadAssignment(NpgsqlDataReader reader)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported assignment format version.");
            var state = (RoutingAssignmentState)reader.GetInt16(6);
            if (!Enum.IsDefined(state))
                throw new FormatException("Unknown assignment state.");
            return new RoutingAssignment(new ConversationId(reader.GetString(0)), new UserId(reader.GetString(2)), new InboxId(reader.GetString(3)),
                new ContactId(reader.GetString(4)), new ChannelKind(reader.GetString(5)), state, reader.NullableInstant(7), reader.GetInt64(8),
                reader.GetFieldValue<DateTimeOffset>(9));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored routing assignment is invalid or incompatible.", error);
        }
    }
}
