using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Conversations.Stores.Postgres;

public sealed partial class PostgresConversationStore
{
    private const string EntryColumns = "sequence, entry_id, tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, inbox_id, format_version, kind, revision, payload, occurred_at";

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
            await using var reader = await exists.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<ConversationOutboxClaim>> ClaimAsync(string consumer, int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        IConversationOutbox.RequireConsumer(consumer, nameof(consumer));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationOutbox.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > IConversationOutbox.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most one hour.");
        var now = time.GetUtcNow();
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // A delivery is claimable only when no earlier pending delivery of the same conversation exists for the consumer (ordered
        // per conversation); SKIP LOCKED keeps concurrent claimers from blocking or double-claiming.
        var claimed = new List<(long Sequence, long Fence, int Attempts, DateTimeOffset LeaseUntil)>();
        await using (var claim = new NpgsqlCommand("""
            WITH candidates AS (
                SELECT d.sequence FROM tf_conversation_outbox_deliveries d
                WHERE d.consumer=@consumer AND d.state=0 AND d.available_at<=@now AND (d.lease_until IS NULL OR d.lease_until<=@now)
                  AND NOT EXISTS (SELECT 1 FROM tf_conversation_outbox_deliveries p
                      WHERE p.consumer=d.consumer AND p.conversation_key=d.conversation_key AND p.state=0 AND p.sequence<d.sequence)
                ORDER BY d.sequence LIMIT @limit FOR UPDATE OF d SKIP LOCKED)
            UPDATE tf_conversation_outbox_deliveries d SET fence=d.fence+1, attempts=d.attempts+1, lease_until=@until
            FROM candidates c WHERE d.consumer=@consumer AND d.sequence=c.sequence
            RETURNING d.sequence, d.fence, d.attempts, d.lease_until
            """, connection, transaction))
        {
            AddText(claim, "consumer", consumer);
            AddInstant(claim, "now", now);
            AddInstant(claim, "until", now + lease);
            claim.Parameters.AddWithValue("limit", limit);
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                claimed.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }
        var entries = new Dictionary<long, ConversationOutboxEntry>();
        if (claimed.Count > 0)
        {
            await using var load = new NpgsqlCommand($"SELECT {EntryColumns} FROM tf_conversation_outbox WHERE sequence = ANY(@sequences)", connection, transaction);
            load.Parameters.Add(new NpgsqlParameter("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = claimed.Select(item => item.Sequence).ToArray() });
            await using var reader = await load.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var entry = ReadEntry(reader);
                entries[entry.Sequence] = entry;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. claimed.OrderBy(item => item.Sequence).Select(item => new ConversationOutboxClaim(entries[item.Sequence], consumer, item.Fence, item.Attempts, item.LeaseUntil))];
    }

    public Task<bool> AcknowledgeAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default) =>
        CompleteAsync(consumer, sequence, fence, "state=1, completed_at=@now, lease_until=NULL", requireLease: true, null, cancellationToken);

    public Task<bool> ReleaseAsync(string consumer, long sequence, long fence, DateTimeOffset retryAt, CancellationToken cancellationToken = default) =>
        CompleteAsync(consumer, sequence, fence, "lease_until=NULL, available_at=@retry", requireLease: true, retryAt, cancellationToken);

    public Task<bool> DeadLetterAsync(string consumer, long sequence, long fence, CancellationToken cancellationToken = default) =>
        CompleteAsync(consumer, sequence, fence, "state=2, completed_at=@now, lease_until=NULL", requireLease: false, null, cancellationToken);

    public async Task<int> PruneAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationOutbox.MaximumPrune);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_conversation_outbox WHERE sequence IN (
                SELECT o.sequence FROM tf_conversation_outbox o
                WHERE o.occurred_at < @before
                  AND NOT EXISTS (SELECT 1 FROM tf_conversation_outbox_deliveries d WHERE d.sequence=o.sequence AND d.state=0)
                ORDER BY o.sequence LIMIT @limit)
            """);
        AddInstant(command, "before", olderThan);
        command.Parameters.AddWithValue("limit", limit);
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
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("fence", fence);
        AddInstant(command, "now", time.GetUtcNow());
        if (retryAt is { } retry)
            AddInstant(command, "retry", retry);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Appends the change and a pending delivery for every registered consumer inside the caller's transaction.</summary>
    private async Task<ConversationOutboxEntry> AppendAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ConversationChange change, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection, transaction);
        batch.BatchCommands.Add(AppendCommand(change));
        var sequence = await ExecuteBatchAsync(batch, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The outbox entry was not written.");
        return new ConversationOutboxEntry(sequence, change);
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
        parameters.AddWithValue("entry", change.Id);
        AddScope(parameters, change.Scope);
        AddText(parameters, "conversation", change.Conversation.Value);
        AddText(parameters, "inbox", change.Inbox.Value);
        parameters.AddWithValue("kind", (short)change.Kind);
        parameters.AddWithValue("revision", change.Revision);
        AddText(parameters, "payload", ConversationJsonCodec.SerializeChangePayload(change));
        AddInstant(parameters, "occurred", change.OccurredAt);
        AddInstant(parameters, "now", time.GetUtcNow());
        return command;
    }

    private static ConversationOutboxEntry ReadEntry(NpgsqlDataReader reader) => Decode(() =>
    {
        if (reader.GetInt16(9) != 1)
            throw new FormatException("Unsupported outbox format version.");
        var scope = new TicketScope(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), new WorkspaceId(reader.GetString(6)));
        var change = ConversationJsonCodec.DeserializeChange(reader.GetString(12), reader.GetGuid(1), scope, new ConversationId(reader.GetString(7)),
            new InboxId(reader.GetString(8)), reader.GetInt64(11), Defined((ConversationChangeKind)reader.GetInt16(10)), reader.GetFieldValue<DateTimeOffset>(13));
        return new ConversationOutboxEntry(reader.GetInt64(0), change);
    }, "outbox entry");
}
