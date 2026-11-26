using System.Data;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Conversations.Stores.Postgres;

public sealed partial class PostgresConversationStore
{
    public async Task<ConversationWriteResult> WriteAsync(TicketScope scope, ConversationWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(write);
        RequireChangeScope(scope, write.Change);
        var conversation = write.Conversation;
        var labelValues = ExtractLabels(conversation.Labels);
        var participantValues = ExtractParticipants(conversation.Participants);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The snapshot row first: the revision (and timeline head) fence takes the row lock that serializes this conversation's writers.
        var head = (write.FirstSequence ?? conversation.LastSequence + 1) - 1;
        long revision;
        await using (var put = new NpgsqlCommand(write.ExpectedRevision is null ? InsertConversation : UpdateConversation, connection, transaction))
        {
            BindConversation(put, scope, conversation, labelValues, participantValues);
            if (write.ExpectedRevision is { } expected)
            {
                AddInt64(put, "expected", expected);
                AddInt64(put, "head", head);
            }
            if (await put.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long written)
                return await MissAsync(connection, transaction, scope, conversation.Id, cancellationToken).ConfigureAwait(false);
            revision = written;
        }

        // Everything after the fenced snapshot row goes to the server in one batch: events, label index sync, message rows and the outbox
        // entry with its deliveries. Message inserts use ON CONFLICT DO NOTHING; a skipped row is diagnosed after rolling back.
        await using var batch = new NpgsqlBatch(connection, transaction);
        List<(NpgsqlBatchCommand Command, MessageEvent Message)>? messageRows = null;
        var eventCount = write.Events.Count;
        if (eventCount > 0)
        {
            var sequences = new long[eventCount];
            var eventIds = new Guid[eventCount];
            var kinds = new short[eventCount];
            var messageIds = new string?[eventCount];
            var occurred = new DateTimeOffset[eventCount];
            var actorKinds = new short[eventCount];
            var actorIds = new string[eventCount];
            var payloads = new string[eventCount];
            for (var i = 0; i < eventCount; i++)
            {
                var item = write.Events[i];
                sequences[i] = item.Sequence;
                eventIds[i] = item.Id;
                kinds[i] = (short)item.Event.Kind;
                messageIds[i] = MessageOf(item)?.Value;
                occurred[i] = item.OccurredAt;
                actorKinds[i] = (short)item.Actor.Kind;
                actorIds[i] = item.Actor.Id;
                payloads[i] = ConversationJsonCodec.SerializeEvent(item.Event);
            }
            var insert = new NpgsqlBatchCommand("""
                INSERT INTO tf_conversation_events(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, sequence, event_id, format_version,
                    kind, message_id, occurred_at, actor_kind, actor_id, payload)
                SELECT @tenant, @project, @environment, @app, @workspace, @conversation, sequence, event_id, 1, kind, message_id, occurred_at, actor_kind, actor_id, payload
                FROM unnest(@sequences, @events, @kinds, @messages, @occurred, @actor_kinds, @actor_ids, @payloads)
                    AS item(sequence, event_id, kind, message_id, occurred_at, actor_kind, actor_id, payload)
                """);
            var parameters = insert.Parameters;
            AddScope(parameters, scope);
            AddText(parameters, "conversation", conversation.Id.Value);
            parameters.Add(new NpgsqlParameter<long[]>("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = sequences });
            parameters.Add(new NpgsqlParameter<Guid[]>("events", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = eventIds });
            parameters.Add(new NpgsqlParameter<short[]>("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = kinds });
            parameters.Add(new NpgsqlParameter<string?[]>("messages", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = messageIds });
            parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = occurred });
            parameters.Add(new NpgsqlParameter<short[]>("actor_kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = actorKinds });
            AddTextArray(parameters, "actor_ids", actorIds);
            AddTextArray(parameters, "payloads", payloads);
            batch.BatchCommands.Add(insert);
        }

        // The label side table mirrors the snapshot's labels and activity time for index-backed label lists.
        if (write.ExpectedRevision is not null)
        {
            var labels = new NpgsqlBatchCommand("""
                DELETE FROM tf_conversation_labels
                WHERE conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND NOT (label = ANY(@labels))
                """);
            AddScope(labels.Parameters, scope);
            AddText(labels.Parameters, "conversation", conversation.Id.Value);
            AddTextArray(labels.Parameters, "labels", labelValues);
            batch.BatchCommands.Add(labels);
        }
        if (labelValues.Length > 0)
        {
            var upsert = new NpgsqlBatchCommand("""
                INSERT INTO tf_conversation_labels(scope_key, conversation_key, label, conversation_id, last_activity_at)
                SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace), tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation),
                    label, @conversation, @last_activity_at
                FROM unnest(@labels) AS item(label)
                ON CONFLICT (conversation_key, label) DO UPDATE SET last_activity_at=EXCLUDED.last_activity_at
                WHERE tf_conversation_labels.last_activity_at IS DISTINCT FROM EXCLUDED.last_activity_at
                """);
            AddScope(upsert.Parameters, scope);
            AddText(upsert.Parameters, "conversation", conversation.Id.Value);
            AddTextArray(upsert.Parameters, "labels", labelValues);
            AddInstant(upsert.Parameters, "last_activity_at", conversation.LastActivityAt);
            batch.BatchCommands.Add(upsert);
        }

        for (var i = 0; i < eventCount; i++)
        {
            var item = write.Events[i];
            if (item.Event.Value is not MessageEvent message)
                continue;
            var insert = new NpgsqlBatchCommand("""
                INSERT INTO tf_conversation_messages(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, message_id, inbox_id, format_version,
                    sequence, direction, external_id, delivery_state, delivery_failure, delivery_updated_at, delivery_revision)
                VALUES(@tenant, @project, @environment, @app, @workspace, @conversation, @message, @inbox, 1, @sequence, @direction, @external, @state, @failure, @updated, 1)
                ON CONFLICT DO NOTHING
                """);
            var parameters = insert.Parameters;
            AddScope(parameters, scope);
            AddText(parameters, "conversation", conversation.Id.Value);
            AddText(parameters, "message", message.Id.Value);
            AddText(parameters, "inbox", conversation.Inbox.Value);
            AddInt64(parameters, "sequence", item.Sequence);
            AddInt16(parameters, "direction", (short)message.Direction);
            AddText(parameters, "external", message.ExternalId);
            AddInt16(parameters, "state", (short)message.Delivery.State);
            AddText(parameters, "failure", message.Delivery.FailureCode);
            AddInstant(parameters, "updated", message.Delivery.UpdatedAt);
            batch.BatchCommands.Add(insert);
            messageRows ??= [];
            messageRows.Add((insert, message));
        }
        if (write.Change is { } changed)
            batch.BatchCommands.Add(AppendCommand(changed));

        long? sequence = null;
        if (batch.BatchCommands.Count > 0)
            sequence = await ExecuteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        if (messageRows is not null)
        {
            for (var i = 0; i < messageRows.Count; i++)
            {
                var (command, message) = messageRows[i];
                if (command.RecordsAffected == 1)
                    continue;
                // A committed row holds the message identifier or the external identifier: report which after rolling back.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                if (await LoadMessageAsync(connection, null, scope, conversation.Id, message.Id, forUpdate: false, cancellationToken).ConfigureAwait(false) is not null)
                    return new ConversationWriteRejected(ConversationWriteRejection.DuplicateMessageId);
                if (message.ExternalId is { } external && await FindExternalAsync(connection, null, scope, conversation.Inbox, external, cancellationToken).ConfigureAwait(false) is { } holder)
                    return new ConversationDuplicateMessage(holder);
                throw new InvalidOperationException("A conversation message row could not be written.");
            }
        }
        var entry = write.Change is { } appended ? new ConversationOutboxEntry(sequence ?? throw new InvalidOperationException("The outbox entry was not written."), appended) : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationWriteApplied(new StoredConversation(scope, conversation, revision), write.Events, entry);
    }

    /// <summary>Runs a batch and returns the single outbox sequence it produced, if any. Per-command row counts are then available.</summary>
    private static async Task<long?> ExecuteBatchAsync(NpgsqlBatch batch, CancellationToken cancellationToken)
    {
        long? sequence = null;
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                sequence = reader.GetInt64(0);
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        return sequence;
    }

    private const string DeleteConversationAndAppendSql = $"""
        WITH deleted AS (
            DELETE FROM tf_conversations WHERE {ConversationKey} AND revision=@expected
            RETURNING {UnaliasedConversationColumns}
        ),
        entry AS (
            INSERT INTO tf_conversation_outbox(entry_id, tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, inbox_id, format_version, kind,
                revision, payload, occurred_at)
            SELECT @entry, @tenant, @project, @environment, @app, @workspace, @conversation, @inbox, 1, @kind, @revision, @payload, @occurred
            FROM deleted
            RETURNING sequence
        ),
        fan AS (
            INSERT INTO tf_conversation_outbox_deliveries(consumer, sequence, conversation_key, state, fence, attempts, available_at)
            SELECT k.consumer, e.sequence, tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation), 0, 0, 0, @now
            FROM tf_conversation_outbox_consumers k CROSS JOIN entry e
        )
        SELECT {DeletedConversationColumns}, e.sequence FROM deleted d CROSS JOIN entry e
        """;

    public async Task<ConversationWriteResult> DeleteAsync(TicketScope scope, ConversationId id, long expectedRevision, ConversationChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        ArgumentNullException.ThrowIfNull(change);
        RequireChangeScope(scope, change);
        if (change.Kind != ConversationChangeKind.Deleted || change.Conversation != id)
            throw new ArgumentException("The change must describe this delete.", nameof(change));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(DeleteConversationAndAppendSql, connection))
        {
            AddScope(command, scope);
            AddText(command, "conversation", id.Value);
            AddInt64(command, "expected", expectedRevision);
            AddGuid(command.Parameters, "entry", change.Id);
            AddText(command, "inbox", change.Inbox.Value);
            AddInt16(command, "kind", (short)change.Kind);
            AddInt64(command, "revision", change.Revision);
            AddText(command, "payload", ConversationJsonCodec.SerializeChangePayload(change));
            AddInstant(command, "occurred", change.OccurredAt);
            AddInstant(command, "now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var deleted = ReadConversation(reader, scope);
                var sequence = reader.GetInt64(23);
                return new ConversationWriteApplied(deleted, [], new ConversationOutboxEntry(sequence, change));
            }
        }
        // Fresh Read Committed snapshot on conflict/not-found miss so concurrent updates report the exact winner revision.
        await using var check = new NpgsqlCommand($"SELECT revision FROM tf_conversations WHERE {ConversationKey}", connection);
        AddScope(check, scope);
        AddText(check, "conversation", id.Value);
        return await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision
            ? new ConversationWriteConflict(revision)
            : ConversationWriteNotFound.Instance;
    }

    private const string LockMessageForDeliverySql = $"""
        SELECT {MessageColumnsM}
        FROM tf_conversations c
        JOIN tf_conversation_messages m
          ON m.conversation_key = c.conversation_key
         AND m.message_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@message)
         AND m.conversation_id = @conversation AND m.message_id = @message
         AND {ScopeWhereM}
        WHERE {ConversationKeyC}
        FOR UPDATE OF c, m
        """;

    public async Task<MessageDeliveryWriteResult> UpdateDeliveryAsync(TicketScope scope, MessageDeliveryWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(write);
        RequireChangeScope(scope, write.Change);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Locking both the conversation row and the message row in one query orders this outbox entry after earlier conversation writes.
        StoredMessage? current;
        await using (var lockCommand = new NpgsqlCommand(LockMessageForDeliverySql, connection, transaction))
        {
            AddScope(lockCommand, scope);
            AddText(lockCommand, "conversation", write.Conversation.Value);
            AddText(lockCommand, "message", write.Message.Value);
            await using var reader = await lockCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            current = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader, scope) : null;
        }
        if (current is null)
            return ConversationWriteNotFound.Instance;
        if (current.DeliveryRevision != write.ExpectedDeliveryRevision)
            return new MessageDeliveryConflict(current);
        if (write.Change.Inbox != current.Inbox)
            throw new ArgumentException("The change must name the message's inbox.", nameof(write));
        var externalId = current.ExternalId;
        if (write.ExternalId is { } requested && !string.Equals(requested, current.ExternalId, StringComparison.Ordinal))
        {
            if (current.ExternalId is not null)
                throw new ArgumentException("A message's external identifier cannot be replaced.", nameof(write));
            externalId = requested;
        }
        long sequence;
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            var update = new NpgsqlBatchCommand($"""
                UPDATE tf_conversation_messages SET delivery_state=@state, delivery_failure=@failure, delivery_updated_at=@updated, external_id=@external,
                    delivery_revision=delivery_revision+1
                WHERE message_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@message) AND conversation_id=@conversation AND message_id=@message
                  AND {ScopeWhere} AND delivery_revision=@expected
                """);
            AddScope(update.Parameters, scope);
            AddText(update.Parameters, "conversation", write.Conversation.Value);
            AddText(update.Parameters, "message", write.Message.Value);
            AddInt16(update.Parameters, "state", (short)write.Delivery.State);
            AddText(update.Parameters, "failure", write.Delivery.FailureCode);
            AddInstant(update.Parameters, "updated", write.Delivery.UpdatedAt);
            AddText(update.Parameters, "external", externalId);
            AddInt64(update.Parameters, "expected", write.ExpectedDeliveryRevision);
            batch.BatchCommands.Add(update);
            batch.BatchCommands.Add(AppendCommand(write.Change));
            try
            {
                sequence = await ExecuteBatchAsync(batch, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The outbox entry was not written.");
            }
            catch (PostgresException error) when (string.Equals(error.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var holder = await FindExternalAsync(connection, null, scope, current.Inbox, externalId!, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A message external identifier conflicted but no holder was found.", error);
                return new ConversationDuplicateMessage(holder);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MessageDeliveryApplied(current with { Delivery = write.Delivery, ExternalId = externalId, DeliveryRevision = current.DeliveryRevision + 1 },
            new ConversationOutboxEntry(sequence, write.Change));
    }

    private const string InsertConversation = """
        INSERT INTO tf_conversations(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, format_version, inbox_id, contact_id, status,
            snoozed_until, priority, assignee_id, team_id, labels, attributes, access_attributes, participants, channel_kind, channel_thread_id, created_at,
            last_activity_at, waiting_since, first_reply_at, last_incoming_at, last_outgoing_at, last_sequence, contact_redirects, revision)
        VALUES(@tenant, @project, @environment, @app, @workspace, @conversation, 1, @inbox, @contact, @status, @snoozed_until, @priority, @assignee, @team,
            @labels, @attributes::jsonb, @access_attributes::jsonb, @participants, @channel_kind, @channel_thread, @created_at, @last_activity_at, @waiting_since,
            @first_reply_at, @last_incoming_at, @last_outgoing_at, @last_sequence, @redirects::jsonb, 1)
        ON CONFLICT DO NOTHING RETURNING revision
        """;

    private const string UpdateConversation = $"""
        UPDATE tf_conversations SET contact_id=@contact, status=@status, snoozed_until=@snoozed_until, priority=@priority, assignee_id=@assignee, team_id=@team,
            labels=@labels, attributes=@attributes::jsonb, access_attributes=@access_attributes::jsonb, participants=@participants,
            last_activity_at=@last_activity_at, waiting_since=@waiting_since, first_reply_at=@first_reply_at, last_incoming_at=@last_incoming_at,
            last_outgoing_at=@last_outgoing_at, last_sequence=@last_sequence, contact_redirects=@redirects::jsonb, revision=revision+1
        WHERE {ConversationKey} AND revision=@expected AND last_sequence=@head AND inbox_id=@inbox AND created_at=@created_at
            AND channel_kind IS NOT DISTINCT FROM @channel_kind AND channel_thread_id IS NOT DISTINCT FROM @channel_thread
        RETURNING revision
        """;

    private static string[] ExtractLabels(IReadOnlyList<LabelId> labels)
    {
        if (labels.Count == 0)
            return [];
        var values = new string[labels.Count];
        for (var i = 0; i < labels.Count; i++)
            values[i] = labels[i].Value;
        return values;
    }

    private static string[] ExtractParticipants(IReadOnlyList<UserId> participants)
    {
        if (participants.Count == 0)
            return [];
        var values = new string[participants.Count];
        for (var i = 0; i < participants.Count; i++)
            values[i] = participants[i].Value;
        return values;
    }

    private static void BindConversation(NpgsqlCommand command, TicketScope scope, Conversation conversation, string[] labelValues, string[] participantValues)
    {
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Id.Value);
        AddText(command, "inbox", conversation.Inbox.Value);
        AddText(command, "contact", conversation.Contact.Value);
        AddInt16(command, "status", (short)conversation.Status.Kind);
        AddInstant(command, "snoozed_until", conversation.Status.SnoozedUntil);
        AddInt16(command, "priority", (short)conversation.Priority);
        AddText(command, "assignee", conversation.Assignee?.Value);
        AddText(command, "team", conversation.Team?.Value);
        AddTextArray(command, "labels", labelValues);
        AddText(command, "attributes", ConversationJsonCodec.SerializeAttributes(conversation.Attributes));
        AddText(command, "access_attributes", ConversationJsonCodec.SerializeConditionAttributes(conversation.Attributes));
        AddTextArray(command, "participants", participantValues);
        AddText(command, "channel_kind", conversation.ChannelThread?.Channel);
        AddText(command, "channel_thread", conversation.ChannelThread?.ExternalThreadId);
        AddInstant(command, "created_at", conversation.CreatedAt);
        AddInstant(command, "last_activity_at", conversation.LastActivityAt);
        AddInstant(command, "waiting_since", conversation.WaitingSince);
        AddInstant(command, "first_reply_at", conversation.FirstReplyAt);
        AddInstant(command, "last_incoming_at", conversation.LastIncomingAt);
        AddInstant(command, "last_outgoing_at", conversation.LastOutgoingAt);
        AddInt64(command, "last_sequence", conversation.LastSequence);
        AddText(command, "redirects", ConversationJsonCodec.SerializeRedirects(conversation.ContactRedirects));
    }

    private static MessageId? MessageOf(TimelineEvent item) => item.Event switch
    {
        MessageEvent message => message.Id,
        NoteEvent note => note.Id,
        AssignmentChanged or TeamChanged or StatusChanged or PriorityChanged or LabelsChanged or AttributeChanged or ParticipantChanged or ContactChanged
            or SystemEvent or ExtensionEvent => null,
    };

    /// <summary>Distinguishes a missed create or compare-and-set (not found, or conflict with the current revision) after rolling back.</summary>
    private static async Task<ConversationWriteResult> MissAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ConversationId id, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_conversations WHERE {ConversationKey}", connection);
        AddScope(command, scope);
        AddText(command, "conversation", id.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision
            ? new ConversationWriteConflict(revision)
            : ConversationWriteNotFound.Instance;
    }

    private static void RequireChangeScope(TicketScope scope, ConversationChange? change)
    {
        if (change is not null && change.Scope != scope)
            throw new ArgumentException("The outbox change must carry the write scope.", nameof(change));
    }
}
