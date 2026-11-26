using System.Data;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Routing.Stores.Postgres;

public sealed partial class PostgresRoutingStore
{
    private const string ConversationScope = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;
    private const string ConversationScopeL = "l.conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND l.conversation_id=@conversation AND l.tenant_id=@tenant AND l.project_id=@project AND l.environment_id=@environment AND l.app_id=@app AND l.workspace_id=@workspace";

    private const string TouchPresenceSql = $"""
        WITH clk AS (SELECT {NowSql} AS now),
        stats AS (
            SELECT count(*) FILTER (WHERE expires_at > (SELECT now FROM clk) AND user_id <> @user) AS other_live,
                   max(activity) FILTER (WHERE user_id=@user AND expires_at > (SELECT now FROM clk)) AS prev_activity,
                   max(since) FILTER (WHERE user_id=@user AND expires_at > (SELECT now FROM clk)) AS prev_since
            FROM tf_routing_presence WHERE {ConversationScope}
        ),
        calc AS (
            SELECT (SELECT now FROM clk) AS now,
                   (prev_activity IS NULL AND other_live >= @max) AS exceeded,
                   (prev_activity IS NULL OR prev_activity <> @activity) AS changed,
                   CASE WHEN prev_activity IS NULL OR prev_activity <> @activity THEN (SELECT now FROM clk) ELSE prev_since END AS next_since
            FROM stats
        ),
        upserted AS (
            INSERT INTO tf_routing_presence({ScopeColumns}, conversation_id, user_id, activity, since, expires_at)
            SELECT {ScopeValues}, @conversation, @user, @activity, c.next_since, c.now + @ttl
            FROM calc c WHERE NOT c.exceeded
            ON CONFLICT (presence_key) DO UPDATE SET activity=EXCLUDED.activity, since=EXCLUDED.since, expires_at=EXCLUDED.expires_at
            RETURNING since, expires_at
        )
        SELECT c.exceeded, c.changed, u.since, u.expires_at
        FROM calc c LEFT JOIN upserted u ON true
        """;

    private const string UpdateOrReadLockSql = $"""
        WITH clk AS (SELECT {NowSql} AS now),
        prev AS (
            SELECT holder_id, fence, acquired_at, expires_at, (expires_at > (SELECT now FROM clk)) AS live
            FROM tf_routing_reply_locks WHERE {ConversationScope} FOR UPDATE
        ),
        upd AS (
            UPDATE tf_routing_reply_locks l
            SET holder_id=@holder, fence=nextval('tf_routing_fences'),
                acquired_at=CASE WHEN p.live THEN p.acquired_at ELSE (SELECT now FROM clk) END,
                expires_at=(SELECT now FROM clk) + @duration
            FROM prev p
            WHERE {ConversationScopeL} AND (NOT p.live OR p.holder_id=@holder)
            RETURNING l.holder_id, l.fence, l.acquired_at, l.expires_at, p.live AS renewed
        )
        SELECT true AS acquired, holder_id, fence, acquired_at, expires_at, renewed FROM upd
        UNION ALL
        SELECT false AS acquired, holder_id, fence, acquired_at, expires_at, false FROM prev WHERE NOT EXISTS (SELECT 1 FROM upd)
        """;

    public async Task<PresenceWriteResult> TouchAsync(TicketScope scope, ConversationId conversation, UserId user, ConversationActivity activity, TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(user);
        if (!Enum.IsDefined(activity))
            throw new ArgumentOutOfRangeException(nameof(activity));
        RequireTtl(ttl);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Serialize writers of one conversation so the live-presence cap is checked atomically.
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('ticketflow.routing.presence:' || encode(tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation), 'hex'), 0))",
            connection, transaction))
        {
            AddScope(gate, scope);
            AddText(gate, "conversation", conversation.Value);
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        bool exceeded;
        bool changed;
        DateTimeOffset since = default;
        DateTimeOffset expiresAt = default;
        await using (var touch = new NpgsqlCommand(TouchPresenceSql, connection, transaction))
        {
            AddScope(touch, scope);
            AddText(touch, "conversation", conversation.Value);
            AddText(touch, "user", user.Value);
            AddShort(touch, "activity", (short)activity);
            AddInstant(touch, "now", OverrideNow());
            AddInterval(touch, "ttl", ttl);
            AddLong(touch, "max", IConversationPresenceStore.MaximumPresencePerConversation);
            await using var reader = await touch.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            exceeded = reader.GetBoolean(0);
            changed = reader.GetBoolean(1);
            if (!exceeded)
            {
                since = Instant(await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false));
                expiresAt = Instant(await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false));
            }
        }
        if (exceeded)
            return PresenceCapacityExceeded.Instance;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PresenceTouched(new ConversationPresence(user, activity, since, expiresAt), changed);
    }

    public async Task<bool> LeaveAsync(TicketScope scope, ConversationId conversation, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(user);
        await using var command = source.CreateCommand($"""
            DELETE FROM tf_routing_presence
            WHERE presence_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@user) AND conversation_id=@conversation AND user_id=@user AND {ScopeWhere}
            RETURNING expires_at > {NowSql}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "user", user.Value);
        AddInstant(command, "now", OverrideNow());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    async Task<IReadOnlyList<ConversationPresence>> IConversationPresenceStore.ListAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"""
            SELECT user_id, activity, since, expires_at FROM tf_routing_presence WHERE {ConversationScope} AND expires_at > {NowSql}
            ORDER BY user_id LIMIT {IConversationPresenceStore.MaximumPresencePerConversation}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInstant(command, "now", OverrideNow());
        var entries = new List<ConversationPresence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            entries.Add(ReadPresence(reader));
        return entries;
    }

    public async Task<ReplyLockResult> AcquireLockAsync(TicketScope scope, ConversationId conversation, UserId holder, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(holder);
        RequireTtl(duration);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var overrideNow = OverrideNow();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using (var insert = new NpgsqlCommand($"""
                WITH clk AS (SELECT {NowSql} AS now)
                INSERT INTO tf_routing_reply_locks({ScopeColumns}, conversation_id, holder_id, fence, acquired_at, expires_at)
                VALUES({ScopeValues}, @conversation, @holder, nextval('tf_routing_fences'), (SELECT now FROM clk), (SELECT now FROM clk) + @duration)
                ON CONFLICT (conversation_key) DO NOTHING
                RETURNING fence, acquired_at, expires_at
                """, connection))
            {
                AddScope(insert, scope);
                AddText(insert, "conversation", conversation.Value);
                AddText(insert, "holder", holder.Value);
                AddInstant(insert, "now", overrideNow);
                AddInterval(insert, "duration", duration);
                await using var reader = await insert.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return new ReplyLockAcquired(new ReplyLock(conversation, holder, reader.GetInt64(0), Instant(await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false)),
                        Instant(await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false))), false);
            }
            await using (var update = new NpgsqlCommand(UpdateOrReadLockSql, connection))
            {
                AddScope(update, scope);
                AddText(update, "conversation", conversation.Value);
                AddText(update, "holder", holder.Value);
                AddInstant(update, "now", overrideNow);
                AddInterval(update, "duration", duration);
                await using var reader = await update.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var acquired = reader.GetBoolean(0);
                    var replyLock = ReadReplyLock(reader, conversation, offset: 1);
                    return acquired ? new ReplyLockAcquired(replyLock, reader.GetBoolean(5)) : new ReplyLockHeld(replyLock);
                }
            }
        }
        throw new InvalidOperationException("The reply lock vanished during acquisition.");
    }

    public async Task<ReplyLock?> GetLockAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"""
            SELECT holder_id, fence, acquired_at, expires_at FROM tf_routing_reply_locks
            WHERE {ConversationScope} AND expires_at > {NowSql}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInstant(command, "now", OverrideNow());
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadReplyLock(reader, conversation, offset: 0) : null;
    }

    public async Task<bool> ReleaseLockAsync(TicketScope scope, ConversationId conversation, UserId holder, long fence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(holder);
        await using var command = source.CreateCommand($"DELETE FROM tf_routing_reply_locks WHERE {ConversationScope} AND holder_id=@holder AND fence=@fence AND expires_at > {NowSql}");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "holder", holder.Value);
        AddLong(command, "fence", fence);
        AddInstant(command, "now", OverrideNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> PruneAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationPresenceStore.MaximumPrune);
        await using var command = source.CreateCommand($"""
            WITH clk AS (SELECT {NowSql} AS now),
            presence AS (
                DELETE FROM tf_routing_presence WHERE presence_key IN (
                    SELECT presence_key FROM tf_routing_presence WHERE expires_at <= (SELECT now FROM clk) ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
                RETURNING 1),
            locks AS (
                DELETE FROM tf_routing_reply_locks WHERE conversation_key IN (
                    SELECT conversation_key FROM tf_routing_reply_locks WHERE expires_at <= (SELECT now FROM clk) ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
                RETURNING 1)
            SELECT (SELECT count(*) FROM presence) + (SELECT count(*) FROM locks)
            """);
        AddInstant(command, "now", OverrideNow());
        AddInt(command, "limit", limit);
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    private static ConversationPresence ReadPresence(NpgsqlDataReader reader)
    {
        try
        {
            var activity = (ConversationActivity)reader.GetInt16(1);
            if (!Enum.IsDefined(activity))
                throw new FormatException("Unknown activity.");
            return new ConversationPresence(new UserId(reader.GetString(0)), activity, reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored routing presence is invalid or incompatible.", error);
        }
    }

    private static ReplyLock ReadReplyLock(NpgsqlDataReader reader, ConversationId conversation, int offset)
    {
        try
        {
            return new ReplyLock(conversation, new UserId(reader.GetString(offset)), reader.GetInt64(offset + 1),
                reader.GetFieldValue<DateTimeOffset>(offset + 2), reader.GetFieldValue<DateTimeOffset>(offset + 3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored routing reply lock is invalid or incompatible.", error);
        }
    }
}
