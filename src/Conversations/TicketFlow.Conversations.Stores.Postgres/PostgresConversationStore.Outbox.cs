using System.Data;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Conversations.Stores.Postgres;

public sealed partial class PostgresConversationStore
{
    public async Task RegisterConsumerAsync(string consumer, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('ticketflow.conversations.outbox-consumers', 0))", connection, transaction))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using (var exists = new NpgsqlCommand("SELECT count(*) FILTER (WHERE consumer=@consumer), count(*) FROM tf_conversation_outbox_consumers", connection, transaction))
        {
            AddText(exists, "consumer", consumer);
            await using var reader = await exists.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reader.GetInt64(0) > 0)
                return;
            if (reader.GetInt64(1) >= IConversationOutbox.MaximumConsumers)
                throw new InvalidOperationException($"At most {IConversationOutbox.MaximumConsumers} outbox consumers can be registered.");
        }
        await using (var insert = new NpgsqlCommand("INSERT INTO tf_conversation_outbox_consumers(consumer, registered_at) VALUES(@consumer, @now)", connection, transaction))
        {
            AddText(insert, "consumer", consumer);
            AddInstant(insert, "now", time.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListConsumersAsync(CancellationToken cancellationToken = default)
    {
        await using var command = source.CreateCommand($"SELECT consumer FROM tf_conversation_outbox_consumers ORDER BY consumer LIMIT {IConversationOutbox.MaximumConsumers}");
        var consumers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            consumers.Add(reader.GetString(0));
        return consumers;
    }

    private const string ClaimSql = """
        WITH candidates AS (
            SELECT d.sequence FROM tf_conversation_outbox_deliveries d
            WHERE d.consumer=@consumer AND d.state=0 AND d.available_at<=@now AND (d.lease_until IS NULL OR d.lease_until<=@now)
              AND NOT EXISTS (SELECT 1 FROM tf_conversation_outbox_deliveries p
                  WHERE p.consumer=d.consumer AND p.conversation_key=d.conversation_key AND p.state=0 AND p.sequence<d.sequence)
            ORDER BY d.sequence LIMIT @limit FOR UPDATE OF d SKIP LOCKED),
        updated AS (
            UPDATE tf_conversation_outbox_deliveries d SET fence=d.fence+1, attempts=d.attempts+1, lease_until=@until
            FROM candidates c WHERE d.consumer=@consumer AND d.sequence=c.sequence
            RETURNING d.sequence, d.fence, d.attempts, d.lease_until)
        SELECT o.sequence, o.entry_id, o.tenant_id, o.project_id, o.environment_id, o.app_id, o.workspace_id, o.conversation_id, o.inbox_id,
            o.format_version, o.kind, o.revision, o.payload, o.occurred_at,
            u.fence, u.attempts, u.lease_until
        FROM updated u
        JOIN tf_conversation_outbox o ON o.sequence = u.sequence
        ORDER BY u.sequence
        """;

    public async Task<IReadOnlyList<ConversationOutboxClaim>> ClaimAsync(string consumer, int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationOutbox.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > IConversationOutbox.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most one hour.");
        var now = time.GetUtcNow();
        // A delivery is claimable only when no earlier pending delivery of the same conversation exists for the consumer (ordered
        // per conversation); SKIP LOCKED keeps concurrent claimers from blocking or double-claiming. Single auto-committed CTE.
        await using var command = source.CreateCommand(ClaimSql);
        AddText(command, "consumer", consumer);
        AddInstant(command, "now", now);
        AddInstant(command, "until", now + lease);
        AddInt32(command, "limit", limit);
        var claims = new List<ConversationOutboxClaim>(Math.Min(limit, 32));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            claims.Add(new ConversationOutboxClaim(ReadEntry(reader), consumer, reader.GetInt64(14), reader.GetInt32(15), await reader.GetFieldValueAsync<DateTimeOffset>(16, cancellationToken).ConfigureAwait(false)));
        return claims;
    }

    public Task<bool> AcknowledgeAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default) =>
        CompleteAsync(consumer, sequence, fence, "state=1, completed_at=@now, lease_until=NULL", requireLease: true, null, cancellationToken);

    public async Task<int> AcknowledgeBatchAsync(string consumer, IReadOnlyList<(long Sequence, long Fence)> claims, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Count == 0)
            return 0;
        if (claims.Count == 1)
            return await AcknowledgeAsync(consumer, claims[0].Sequence, claims[0].Fence, cancellationToken).ConfigureAwait(false) ? 1 : 0;
        var sequences = new long[claims.Count];
        var fences = new long[claims.Count];
        for (var i = 0; i < claims.Count; i++)
        {
            sequences[i] = claims[i].Sequence;
            fences[i] = claims[i].Fence;
        }
        await using var command = source.CreateCommand("""
            UPDATE tf_conversation_outbox_deliveries d
            SET state=1, completed_at=@now, lease_until=NULL
            FROM unnest(@sequences, @fences) AS item(sequence, fence)
            WHERE d.consumer=@consumer AND d.sequence=item.sequence AND d.state=0 AND d.fence=item.fence AND d.fence > 0 AND d.lease_until > @now
            """);
        AddText(command, "consumer", consumer);
        command.Parameters.Add(new NpgsqlParameter<long[]>("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = sequences });
        command.Parameters.Add(new NpgsqlParameter<long[]>("fences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = fences });
        AddInstant(command, "now", time.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ReleaseAsync(string consumer, long sequence, long fence, DateTimeOffset retryAt, CancellationToken cancellationToken = default) =>
        CompleteAsync(consumer, sequence, fence, "lease_until=NULL, available_at=@retry", requireLease: true, retryAt, cancellationToken);

    public async Task<int> ReleaseBatchAsync(string consumer, IReadOnlyList<(long Sequence, long Fence, DateTimeOffset RetryAt)> claims, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Count == 0)
            return 0;
        if (claims.Count == 1)
            return await ReleaseAsync(consumer, claims[0].Sequence, claims[0].Fence, claims[0].RetryAt, cancellationToken).ConfigureAwait(false) ? 1 : 0;
        var sequences = new long[claims.Count];
        var fences = new long[claims.Count];
        var retries = new DateTimeOffset[claims.Count];
        for (var i = 0; i < claims.Count; i++)
        {
            sequences[i] = claims[i].Sequence;
            fences[i] = claims[i].Fence;
            retries[i] = claims[i].RetryAt;
        }
        await using var command = source.CreateCommand("""
            UPDATE tf_conversation_outbox_deliveries d
            SET lease_until=NULL, available_at=item.retry
            FROM unnest(@sequences, @fences, @retries) AS item(sequence, fence, retry)
            WHERE d.consumer=@consumer AND d.sequence=item.sequence AND d.state=0 AND d.fence=item.fence AND d.fence > 0 AND d.lease_until > @now
            """);
        AddText(command, "consumer", consumer);
        command.Parameters.Add(new NpgsqlParameter<long[]>("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = sequences });
        command.Parameters.Add(new NpgsqlParameter<long[]>("fences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = fences });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("retries", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = retries });
        AddInstant(command, "now", time.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeadLetterAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default) =>
        CompleteAsync(consumer, sequence, fence, "state=2, completed_at=@now, lease_until=NULL", requireLease: false, null, cancellationToken);

    public async Task<int> DeadLetterBatchAsync(string consumer, IReadOnlyList<(long Sequence, long Fence)> claims, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Count == 0)
            return 0;
        if (claims.Count == 1)
            return await DeadLetterAsync(consumer, claims[0].Sequence, claims[0].Fence, cancellationToken).ConfigureAwait(false) ? 1 : 0;
        var sequences = new long[claims.Count];
        var fences = new long[claims.Count];
        for (var i = 0; i < claims.Count; i++)
        {
            sequences[i] = claims[i].Sequence;
            fences[i] = claims[i].Fence;
        }
        await using var command = source.CreateCommand("""
            UPDATE tf_conversation_outbox_deliveries d
            SET state=2, completed_at=@now, lease_until=NULL
            FROM unnest(@sequences, @fences) AS item(sequence, fence)
            WHERE d.consumer=@consumer AND d.sequence=item.sequence AND d.state=0 AND d.fence=item.fence AND d.fence > 0
            """);
        AddText(command, "consumer", consumer);
        command.Parameters.Add(new NpgsqlParameter<long[]>("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = sequences });
        command.Parameters.Add(new NpgsqlParameter<long[]>("fences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = fences });
        AddInstant(command, "now", time.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationOutbox.MaximumPrune);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_conversation_outbox WHERE sequence IN (
                SELECT o.sequence FROM tf_conversation_outbox o
                WHERE o.occurred_at < @before
                  AND NOT EXISTS (SELECT 1 FROM tf_conversation_outbox_deliveries d WHERE d.sequence=o.sequence AND d.state=0)
                ORDER BY o.occurred_at, o.sequence LIMIT @limit)
            """);
        AddInstant(command, "before", olderThan);
        AddInt32(command, "limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CompleteAsync(string consumer, long sequence, long fence, string assignments, bool requireLease, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        await using var command = source.CreateCommand($"""
            UPDATE tf_conversation_outbox_deliveries SET {assignments}
            WHERE consumer=@consumer AND sequence=@sequence AND state=0 AND fence=@fence AND fence > 0{(requireLease ? " AND lease_until > @now" : "")}
            """);
        AddText(command, "consumer", consumer);
        AddInt64(command, "sequence", sequence);
        AddInt64(command, "fence", fence);
        AddInstant(command, "now", time.GetUtcNow());
        if (retryAt is { } retry)
            AddInstant(command, "retry", retry);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>One statement: the outbox entry plus a pending delivery per registered consumer, returning the entry sequence.</summary>
    private NpgsqlBatchCommand AppendCommand(ConversationChange change)
    {
        var command = new NpgsqlBatchCommand("""
            WITH entry AS (
                INSERT INTO tf_conversation_outbox(entry_id, tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, inbox_id, format_version, kind,
                    revision, payload, occurred_at)
                VALUES(@entry, @tenant, @project, @environment, @app, @workspace, @conversation, @inbox, 1, @kind, @revision, @payload, @occurred)
                RETURNING sequence),
            fan AS (
                INSERT INTO tf_conversation_outbox_deliveries(consumer, sequence, conversation_key, state, fence, attempts, available_at)
                SELECT k.consumer, e.sequence, tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation), 0, 0, 0, @now
                FROM tf_conversation_outbox_consumers k CROSS JOIN entry e)
            SELECT sequence FROM entry
            """);
        var parameters = command.Parameters;
        AddGuid(parameters, "entry", change.Id);
        AddScope(parameters, change.Scope);
        AddText(parameters, "conversation", change.Conversation.Value);
        AddText(parameters, "inbox", change.Inbox.Value);
        AddInt16(parameters, "kind", (short)change.Kind);
        AddInt64(parameters, "revision", change.Revision);
        AddText(parameters, "payload", ConversationJsonCodec.SerializeChangePayload(change));
        AddInstant(parameters, "occurred", change.OccurredAt);
        AddInstant(parameters, "now", time.GetUtcNow());
        return command;
    }

    private static ConversationOutboxEntry ReadEntry(NpgsqlDataReader reader)
    {
        try
        {
            if (reader.GetInt16(9) != 1)
                throw new FormatException("Unsupported outbox format version.");
            var scope = new TicketScope(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), new WorkspaceId(reader.GetString(6)));
            var change = ConversationJsonCodec.DeserializeChange(reader.GetString(12), reader.GetGuid(1), scope, new ConversationId(reader.GetString(7)),
                new InboxId(reader.GetString(8)), reader.GetInt64(11), Defined((ConversationChangeKind)reader.GetInt16(10)), reader.GetFieldValue<DateTimeOffset>(13));
            return new ConversationOutboxEntry(reader.GetInt64(0), change);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored conversation outbox entry is invalid or incompatible.", error);
        }
    }
}
