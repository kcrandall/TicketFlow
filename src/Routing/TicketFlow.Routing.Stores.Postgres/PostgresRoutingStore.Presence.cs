using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Routing.Stores.Postgres;

public sealed partial class PostgresRoutingStore
{
    private const string ConversationScope = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;

    public async Task<PresenceWriteResult> TouchAsync(TicketScope scope, ConversationId conversation, UserId user, ConversationActivity activity, TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(user);
        if (!Enum.IsDefined(activity))
            throw new ArgumentOutOfRangeException(nameof(activity));
        RequireTtl(ttl);
        var now = Now;
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
        ConversationPresence? existing = null;
        long live;
        await using (var read = new NpgsqlCommand($"""
            SELECT count(*) FILTER (WHERE expires_at > @now AND user_id <> @user), max(activity) FILTER (WHERE user_id=@user AND expires_at > @now),
                max(since) FILTER (WHERE user_id=@user AND expires_at > @now)
            FROM tf_routing_presence WHERE {ConversationScope}
            """, connection, transaction))
        {
            AddScope(read, scope);
            AddText(read, "conversation", conversation.Value);
            AddText(read, "user", user.Value);
            AddInstant(read, "now", now);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            live = reader.GetInt64(0);
            if (!reader.IsDBNull(1))
                existing = new ConversationPresence(user, (ConversationActivity)reader.GetInt16(1), reader.GetFieldValue<DateTimeOffset>(2), now);
        }
        if (existing is null && live >= IConversationPresenceStore.MaximumPresencePerConversation)
            return PresenceCapacityExceeded.Instance;
        var changed = existing is null || existing.Activity != activity;
        var current = new ConversationPresence(user, activity, changed ? now : existing!.Since, now + ttl);
        await using (var upsert = new NpgsqlCommand($"""
            INSERT INTO tf_routing_presence({ScopeColumns}, conversation_id, user_id, activity, since, expires_at)
            VALUES({ScopeValues}, @conversation, @user, @activity, @since, @expires)
            ON CONFLICT (presence_key) DO UPDATE SET activity=EXCLUDED.activity, since=EXCLUDED.since, expires_at=EXCLUDED.expires_at
            """, connection, transaction))
        {
            AddScope(upsert, scope);
            AddText(upsert, "conversation", conversation.Value);
            AddText(upsert, "user", user.Value);
            upsert.Parameters.AddWithValue("activity", (short)activity);
            AddInstant(upsert, "since", current.Since);
            AddInstant(upsert, "expires", current.ExpiresAt);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PresenceTouched(current, changed);
    }

    public async Task<bool> LeaveAsync(TicketScope scope, ConversationId conversation, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(user);
        await using var command = source.CreateCommand($"""
            DELETE FROM tf_routing_presence
            WHERE presence_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@user) AND conversation_id=@conversation AND user_id=@user AND {ScopeWhere}
            RETURNING expires_at > @now
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "user", user.Value);
        AddInstant(command, "now", Now);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    async Task<IReadOnlyList<ConversationPresence>> IConversationPresenceStore.ListAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"""
            SELECT user_id, activity, since, expires_at FROM tf_routing_presence WHERE {ConversationScope} AND expires_at > @now
            ORDER BY user_id LIMIT {IConversationPresenceStore.MaximumPresencePerConversation}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInstant(command, "now", Now);
        var entries = new List<ConversationPresence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            entries.Add(Decode(() =>
            {
                var activity = (ConversationActivity)reader.GetInt16(1);
                if (!Enum.IsDefined(activity))
                    throw new FormatException("Unknown activity.");
                return new ConversationPresence(new UserId(reader.GetString(0)), activity, reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3));
            }, "presence"));
        return entries;
    }

    public async Task<ReplyLockResult> AcquireLockAsync(TicketScope scope, ConversationId conversation, UserId holder, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(holder);
        RequireTtl(duration);
        var now = Now;
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_routing_reply_locks({ScopeColumns}, conversation_id, holder_id, fence, acquired_at, expires_at)
            VALUES({ScopeValues}, @conversation, @holder, nextval('tf_routing_fences'), @now, @expires)
            ON CONFLICT (conversation_key) DO NOTHING
            RETURNING fence
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "conversation", conversation.Value);
            AddText(insert, "holder", holder.Value);
            AddInstant(insert, "now", now);
            AddInstant(insert, "expires", now + duration);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long fence)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ReplyLockAcquired(new ReplyLock(conversation, holder, fence, now, now + duration), false);
            }
        }
        var current = await LoadLockAsync(connection, transaction, scope, conversation, true, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The reply lock vanished during acquisition.");
        if (current.ExpiresAt > now && current.Holder != holder)
            return new ReplyLockHeld(current);
        var renewed = current.ExpiresAt > now;
        var acquiredAt = renewed ? current.AcquiredAt : now;
        long next;
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_routing_reply_locks SET holder_id=@holder, fence=nextval('tf_routing_fences'), acquired_at=@acquired, expires_at=@expires
            WHERE {ConversationScope} RETURNING fence
            """, connection, transaction))
        {
            AddScope(update, scope);
            AddText(update, "conversation", conversation.Value);
            AddText(update, "holder", holder.Value);
            AddInstant(update, "acquired", acquiredAt);
            AddInstant(update, "expires", now + duration);
            next = (long)(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ReplyLockAcquired(new ReplyLock(conversation, holder, next, acquiredAt, now + duration), renewed);
    }

    public async Task<ReplyLock?> GetLockAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadLockAsync(connection, null, scope, conversation, false, cancellationToken).ConfigureAwait(false) is { } current && current.ExpiresAt > Now ? current : null;
    }

    public async Task<bool> ReleaseLockAsync(TicketScope scope, ConversationId conversation, UserId holder, long fence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        RequireAgent(holder);
        await using var command = source.CreateCommand($"DELETE FROM tf_routing_reply_locks WHERE {ConversationScope} AND holder_id=@holder AND fence=@fence AND expires_at > @now");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "holder", holder.Value);
        AddLong(command, "fence", fence);
        AddInstant(command, "now", Now);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> PruneAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationPresenceStore.MaximumPrune);
        var now = Now;
        await using var command = source.CreateCommand("""
            WITH presence AS (
                DELETE FROM tf_routing_presence WHERE presence_key IN (
                    SELECT presence_key FROM tf_routing_presence WHERE expires_at <= @now ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
                RETURNING 1),
            locks AS (
                DELETE FROM tf_routing_reply_locks WHERE conversation_key IN (
                    SELECT conversation_key FROM tf_routing_reply_locks WHERE expires_at <= @now ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
                RETURNING 1)
            SELECT (SELECT count(*) FROM presence) + (SELECT count(*) FROM locks)
            """);
        AddInstant(command, "now", now);
        AddInt(command, "limit", limit);
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    private static async Task<ReplyLock?> LoadLockAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ConversationId conversation, bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT holder_id, fence, acquired_at, expires_at FROM tf_routing_reply_locks WHERE {ConversationScope}{(forUpdate ? " FOR UPDATE" : "")}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Decode(() => new ReplyLock(conversation, new UserId(reader.GetString(0)), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)), "reply lock")
            : null;
    }
}
