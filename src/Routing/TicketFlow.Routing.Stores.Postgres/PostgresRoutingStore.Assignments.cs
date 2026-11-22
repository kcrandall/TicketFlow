using Npgsql;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.Stores.Postgres;

public sealed partial class PostgresRoutingStore
{
    private const string AssignmentColumns = "conversation_id, format_version, agent_id, inbox_id, contact_id, channel_kind, state, reserved_until, conversation_revision, updated_at";
    private const string AssignmentKey = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;
    private const string AnchorKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace,'routing-load',a,@channel)";

    public async Task<RoutingReservationResult> ReserveAsync(TicketScope scope, RoutingReservationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        var agents = request.Candidates.Select(candidate => candidate.Agent.Value).ToArray();
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Anchor and cursor rows are permanent and created outside the transaction, so concurrent first uses never deadlock on inserts.
        await using (var anchors = new NpgsqlCommand($"""
            INSERT INTO tf_routing_load_anchors(anchor_key) SELECT k FROM (SELECT DISTINCT {AnchorKey} AS k FROM unnest(@agents) a) keys ORDER BY k ON CONFLICT DO NOTHING;
            INSERT INTO tf_routing_cursors({ScopeColumns}, cursor_name, last_agent_id) VALUES({ScopeValues}, @cursor, NULL) ON CONFLICT DO NOTHING;
            """, connection))
        {
            AddScope(anchors, scope);
            AddTextArray(anchors, "agents", agents);
            AddText(anchors, "channel", request.Channel.Value);
            AddText(anchors, "cursor", request.CursorKey);
            await anchors.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        // Lock order: cursor, then load anchors in key order. Every multi-row locker follows it, so reservations cannot deadlock.
        string? cursor;
        await using (var lockCursor = new NpgsqlCommand($"""
            SELECT last_agent_id FROM tf_routing_cursors
            WHERE cursor_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@cursor) AND cursor_name=@cursor AND {ScopeWhere} FOR UPDATE
            """, connection, transaction))
        {
            AddScope(lockCursor, scope);
            AddText(lockCursor, "cursor", request.CursorKey);
            cursor = await lockCursor.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        await using (var lockAnchors = new NpgsqlCommand($"""
            SELECT anchor_key FROM tf_routing_load_anchors WHERE anchor_key IN (SELECT {AnchorKey} FROM unnest(@agents) a) ORDER BY anchor_key FOR UPDATE
            """, connection, transaction))
        {
            AddScope(lockAnchors, scope);
            AddTextArray(lockAnchors, "agents", agents);
            AddText(lockAnchors, "channel", request.Channel.Value);
            await lockAnchors.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (await LoadAssignmentAsync(connection, transaction, scope, request.Conversation, true, cancellationToken).ConfigureAwait(false) is { } existing
            && Blocks(existing, request.ConversationRevision, now))
            return new RoutingAlreadyReserved(existing);

        var loads = new Dictionary<UserId, int>();
        await using (var count = new NpgsqlCommand($"""
            SELECT agent_id, count(*) FROM tf_routing_assignments
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id = ANY(@agents) AND channel_kind=@channel AND conversation_id <> @conversation
              AND (state=2 OR reserved_until > @now)
            GROUP BY agent_id
            """, connection, transaction))
        {
            AddScope(count, scope);
            AddTextArray(count, "agents", agents);
            AddText(count, "channel", request.Channel.Value);
            AddText(count, "conversation", request.Conversation.Value);
            AddInstant(count, "now", now);
            await using var reader = await count.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                loads[new UserId(reader.GetString(0))] = checked((int)reader.GetInt64(1));
        }
        var chosen = request.Select(loads, cursor);
        if (chosen is null)
            return RoutingNoCapacity.Instance;

        var reservation = new RoutingAssignment(request.Conversation, chosen.Agent, request.Inbox, request.Contact, request.Channel, RoutingAssignmentState.Reserved,
            now + request.Lease, request.ConversationRevision, now);
        await using (var upsert = new NpgsqlCommand($"""
            INSERT INTO tf_routing_assignments({ScopeColumns}, conversation_id, format_version, agent_id, inbox_id, contact_id, channel_kind, state, reserved_until,
                conversation_revision, updated_at)
            VALUES({ScopeValues}, @conversation, 1, @agent, @inbox, @contact, @channel, 1, @until, @revision, @now)
            ON CONFLICT (conversation_key) DO UPDATE SET agent_id=EXCLUDED.agent_id, inbox_id=EXCLUDED.inbox_id, contact_id=EXCLUDED.contact_id,
                channel_kind=EXCLUDED.channel_kind, state=1, reserved_until=EXCLUDED.reserved_until, conversation_revision=EXCLUDED.conversation_revision,
                updated_at=EXCLUDED.updated_at
            WHERE NOT ((tf_routing_assignments.state=1 AND tf_routing_assignments.reserved_until > @now)
                OR (tf_routing_assignments.state=2 AND tf_routing_assignments.conversation_revision > @revision))
            RETURNING 1
            """, connection, transaction))
        {
            AddScope(upsert, scope);
            AddText(upsert, "conversation", request.Conversation.Value);
            AddText(upsert, "agent", chosen.Agent.Value);
            AddText(upsert, "inbox", request.Inbox.Value);
            AddText(upsert, "contact", request.Contact.Value);
            AddText(upsert, "channel", request.Channel.Value);
            AddInstant(upsert, "until", reservation.ReservedUntil);
            AddLong(upsert, "revision", request.ConversationRevision);
            AddInstant(upsert, "now", now);
            if (await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return await GetAsync(scope, request.Conversation, cancellationToken).ConfigureAwait(false) is { } winner
                    ? new RoutingAlreadyReserved(winner)
                    : RoutingNoCapacity.Instance;
            }
        }
        await using (var move = new NpgsqlCommand($"""
            UPDATE tf_routing_cursors SET last_agent_id=@agent
            WHERE cursor_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@cursor) AND cursor_name=@cursor AND {ScopeWhere}
            """, connection, transaction))
        {
            AddScope(move, scope);
            AddText(move, "cursor", request.CursorKey);
            AddText(move, "agent", chosen.Agent.Value);
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RoutingReserved(reservation, loads.GetValueOrDefault(chosen.Agent) + 1);
    }

    public async Task<bool> ConfirmAsync(TicketScope scope, ConversationId conversation, UserId agent, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(conversationRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await LoadAssignmentAsync(connection, transaction, scope, conversation, true, cancellationToken).ConfigureAwait(false);
        if (row is null || row.Agent != agent || row.ConversationRevision > conversationRevision)
            return false;
        if (row.State == RoutingAssignmentState.Reserved || row.ConversationRevision < conversationRevision)
        {
            var now = await NowAsync(cancellationToken).ConfigureAwait(false);
            await using (var update = new NpgsqlCommand($"""
                UPDATE tf_routing_assignments SET state=2, reserved_until=NULL, conversation_revision=@revision, updated_at=@now WHERE {AssignmentKey}
                """, connection, transaction))
            {
                AddScope(update, scope);
                AddText(update, "conversation", conversation.Value);
                AddLong(update, "revision", conversationRevision);
                AddInstant(update, "now", now);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await UpsertStickyAsync(connection, transaction, scope, row.Contact, agent, now, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var upsert = new NpgsqlCommand($"""
            INSERT INTO tf_routing_assignments({ScopeColumns}, conversation_id, format_version, agent_id, inbox_id, contact_id, channel_kind, state, reserved_until,
                conversation_revision, updated_at)
            VALUES({ScopeValues}, @conversation, 1, @agent, @inbox, @contact, @channel, 2, NULL, @revision, @now)
            ON CONFLICT (conversation_key) DO UPDATE SET agent_id=EXCLUDED.agent_id, inbox_id=EXCLUDED.inbox_id, contact_id=EXCLUDED.contact_id,
                channel_kind=EXCLUDED.channel_kind, state=2, reserved_until=NULL, conversation_revision=EXCLUDED.conversation_revision, updated_at=EXCLUDED.updated_at
            WHERE tf_routing_assignments.conversation_revision <= @revision
              AND NOT (tf_routing_assignments.state=2 AND tf_routing_assignments.agent_id=@agent AND tf_routing_assignments.channel_kind=@channel)
            RETURNING 1
            """, connection, transaction))
        {
            AddScope(upsert, scope);
            AddText(upsert, "conversation", record.Conversation.Value);
            AddText(upsert, "agent", record.Agent.Value);
            AddText(upsert, "inbox", record.Inbox.Value);
            AddText(upsert, "contact", record.Contact.Value);
            AddText(upsert, "channel", record.Channel.Value);
            AddLong(upsert, "revision", record.ConversationRevision);
            AddInstant(upsert, "now", now);
            if (await upsert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                return;
        }
        await UpsertStickyAsync(connection, transaction, scope, record.Contact, record.Agent, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ClearAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"""
            DELETE FROM tf_routing_assignments WHERE {AssignmentKey} AND conversation_revision <= @revision AND NOT (state=1 AND reserved_until > @now)
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddLong(command, "revision", conversationRevision);
        AddInstant(command, "now", await NowAsync(cancellationToken).ConfigureAwait(false));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<RoutingAssignment?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAssignmentAsync(connection, null, scope, conversation, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RoutingAssignment>> ListForAgentAsync(TicketScope scope, UserId agent, ConversationId? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireAgent(agent);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingAssignmentStore.MaximumList);
        await using var command = source.CreateCommand($"""
            SELECT {AssignmentColumns} FROM tf_routing_assignments
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id=@agent AND (@after::text IS NULL OR conversation_id > @after COLLATE "C")
            ORDER BY conversation_id LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        AddText(command, "after", after?.Value);
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
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND agent_id=@agent AND (state=2 OR reserved_until > @now)
            GROUP BY channel_kind
            """);
        AddScope(command, scope);
        AddText(command, "agent", agent.Value);
        AddInstant(command, "now", await NowAsync(cancellationToken).ConfigureAwait(false));
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Decode(() => new StickyOwner(contact, new UserId(reader.GetString(0)), reader.GetFieldValue<DateTimeOffset>(1)), "sticky owner")
            : null;
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

    private static async Task UpsertStickyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ContactId contact, UserId agent, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO tf_routing_sticky_owners({ScopeColumns}, contact_id, agent_id, assigned_at) VALUES({ScopeValues}, @contact, @agent, @now)
            ON CONFLICT (contact_key) DO UPDATE SET agent_id=EXCLUDED.agent_id, assigned_at=EXCLUDED.assigned_at
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "contact", contact.Value);
        AddText(command, "agent", agent.Value);
        AddInstant(command, "now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RoutingAssignment?> LoadAssignmentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ConversationId conversation,
        bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {AssignmentColumns} FROM tf_routing_assignments WHERE {AssignmentKey}{(forUpdate ? " FOR UPDATE" : "")}",
            connection, transaction);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAssignment(reader) : null;
    }

    private static RoutingAssignment ReadAssignment(NpgsqlDataReader reader) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported assignment format version.");
        var state = (RoutingAssignmentState)reader.GetInt16(6);
        if (!Enum.IsDefined(state))
            throw new FormatException("Unknown assignment state.");
        return new RoutingAssignment(new ConversationId(reader.GetString(0)), new UserId(reader.GetString(2)), new InboxId(reader.GetString(3)),
            new ContactId(reader.GetString(4)), new ChannelKind(reader.GetString(5)), state, NullableInstant(reader, 7), reader.GetInt64(8),
            reader.GetFieldValue<DateTimeOffset>(9));
    }, "assignment");
}
