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
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The snapshot row first: the revision (and timeline head) fence takes the row lock that serializes this conversation's writers.
        var head = (write.FirstSequence ?? conversation.LastSequence + 1) - 1;
        long revision;
        await using (var put = new NpgsqlCommand(write.ExpectedRevision is null ? InsertConversation : UpdateConversation, connection, transaction))
        {
            BindConversation(put, scope, conversation);
            if (write.ExpectedRevision is { } expected)
            {
                put.Parameters.AddWithValue("expected", expected);
                put.Parameters.AddWithValue("head", head);
            }
            if (await put.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long written)
                return await MissAsync(connection, transaction, scope, conversation.Id, cancellationToken).ConfigureAwait(false);
            revision = written;
        }

        if (write.Events.Count > 0)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO tf_conversation_events(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, sequence, event_id, format_version,
                    kind, message_id, occurred_at, actor_kind, actor_id, payload)
                SELECT @tenant, @project, @environment, @app, @workspace, @conversation, sequence, event_id, 1, kind, message_id, occurred_at, actor_kind, actor_id, payload
                FROM unnest(@sequences, @events, @kinds, @messages, @occurred, @actor_kinds, @actor_ids, @payloads)
                    AS item(sequence, event_id, kind, message_id, occurred_at, actor_kind, actor_id, payload)
                """, connection, transaction);
            AddScope(insert, scope);
            AddText(insert, "conversation", conversation.Id.Value);
            insert.Parameters.Add(new NpgsqlParameter("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = write.Events.Select(item => item.Sequence).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("events", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = write.Events.Select(item => item.Id).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = write.Events.Select(item => (short)item.Event.Kind).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("messages", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = write.Events.Select(item => MessageOf(item)?.Value).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = write.Events.Select(item => item.OccurredAt).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("actor_kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = write.Events.Select(item => (short)item.Actor.Kind).ToArray() });
            AddTextArray(insert, "actor_ids", write.Events.Select(item => item.Actor.Id));
            AddTextArray(insert, "payloads", write.Events.Select(item => ConversationJsonCodec.SerializeEvent(item.Event)));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in write.Events)
        {
            if (item.Event.Value is not MessageEvent message)
                continue;
            await using var insert = new NpgsqlCommand("""
                INSERT INTO tf_conversation_messages(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, message_id, inbox_id, format_version,
                    sequence, direction, external_id, delivery_state, delivery_failure, delivery_updated_at, delivery_revision)
                VALUES(@tenant, @project, @environment, @app, @workspace, @conversation, @message, @inbox, 1, @sequence, @direction, @external, @state, @failure, @updated, 1)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            AddScope(insert, scope);
            AddText(insert, "conversation", conversation.Id.Value);
            AddText(insert, "message", message.Id.Value);
            AddText(insert, "inbox", conversation.Inbox.Value);
            insert.Parameters.AddWithValue("sequence", item.Sequence);
            insert.Parameters.AddWithValue("direction", (short)message.Direction);
            AddText(insert, "external", message.ExternalId);
            insert.Parameters.AddWithValue("state", (short)message.Delivery.State);
            AddText(insert, "failure", message.Delivery.FailureCode);
            AddInstant(insert, "updated", message.Delivery.UpdatedAt);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                continue;
            // A committed row holds the message identifier or the external identifier: report which after rolling back.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            if (await LoadMessageAsync(connection, null, scope, conversation.Id, message.Id, forUpdate: false, cancellationToken).ConfigureAwait(false) is not null)
                return new ConversationWriteRejected(ConversationWriteRejection.DuplicateMessageId);
            if (message.ExternalId is { } external && await FindExternalAsync(connection, null, scope, conversation.Inbox, external, cancellationToken).ConfigureAwait(false) is { } holder)
                return new ConversationDuplicateMessage(holder);
            throw new InvalidOperationException("A conversation message row could not be written.");
        }

        var entry = write.Change is null ? null : await AppendAsync(connection, transaction, write.Change, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationWriteApplied(new StoredConversation(scope, conversation, revision), write.Events, entry);
    }

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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await LoadAsync(connection, transaction, scope, id, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return ConversationWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new ConversationWriteConflict(existing.Revision);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_conversations WHERE {ConversationKey} AND revision=@expected", connection, transaction))
        {
            AddScope(delete, scope);
            AddText(delete, "conversation", id.Value);
            delete.Parameters.AddWithValue("expected", expectedRevision);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var entry = await AppendAsync(connection, transaction, change, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationWriteApplied(existing, [], entry);
    }

    public async Task<MessageDeliveryWriteResult> UpdateDeliveryAsync(TicketScope scope, MessageDeliveryWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(write);
        RequireChangeScope(scope, write.Change);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Locking the conversation row orders this outbox entry after the conversation's earlier writes.
        if (await LoadAsync(connection, transaction, scope, write.Conversation, forUpdate: true, cancellationToken).ConfigureAwait(false) is null)
            return ConversationWriteNotFound.Instance;
        var current = await LoadMessageAsync(connection, transaction, scope, write.Conversation, write.Message, forUpdate: true, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return ConversationWriteNotFound.Instance;
        if (current.DeliveryRevision != write.ExpectedDeliveryRevision)
            return new MessageDeliveryConflict(current);
        if (write.Change.Inbox != current.Inbox)
            throw new ArgumentException("The change must name the message's inbox.", nameof(write));
        var externalId = current.ExternalId;
        if (write.ExternalId is { } requested && requested != current.ExternalId)
        {
            if (current.ExternalId is not null)
                throw new ArgumentException("A message's external identifier cannot be replaced.", nameof(write));
            externalId = requested;
        }
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_conversation_messages SET delivery_state=@state, delivery_failure=@failure, delivery_updated_at=@updated, external_id=@external,
                delivery_revision=delivery_revision+1
            WHERE message_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@message) AND conversation_id=@conversation AND message_id=@message
              AND {ScopeWhere} AND delivery_revision=@expected
            """, connection, transaction))
        {
            AddScope(update, scope);
            AddText(update, "conversation", write.Conversation.Value);
            AddText(update, "message", write.Message.Value);
            update.Parameters.AddWithValue("state", (short)write.Delivery.State);
            AddText(update, "failure", write.Delivery.FailureCode);
            AddInstant(update, "updated", write.Delivery.UpdatedAt);
            AddText(update, "external", externalId);
            update.Parameters.AddWithValue("expected", write.ExpectedDeliveryRevision);
            try
            {
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var holder = await FindExternalAsync(connection, null, scope, current.Inbox, externalId!, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A message external identifier conflicted but no holder was found.");
                return new ConversationDuplicateMessage(holder);
            }
        }
        var entry = await AppendAsync(connection, transaction, write.Change, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MessageDeliveryApplied(current with { Delivery = write.Delivery, ExternalId = externalId, DeliveryRevision = current.DeliveryRevision + 1 }, entry);
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

    private static void BindConversation(NpgsqlCommand command, TicketScope scope, Conversation conversation)
    {
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Id.Value);
        AddText(command, "inbox", conversation.Inbox.Value);
        AddText(command, "contact", conversation.Contact.Value);
        command.Parameters.AddWithValue("status", (short)conversation.Status.Kind);
        AddInstant(command, "snoozed_until", conversation.Status.SnoozedUntil);
        command.Parameters.AddWithValue("priority", (short)conversation.Priority);
        AddText(command, "assignee", conversation.Assignee?.Value);
        AddText(command, "team", conversation.Team?.Value);
        AddTextArray(command, "labels", conversation.Labels.Select(label => label.Value));
        AddText(command, "attributes", ConversationJsonCodec.SerializeAttributes(conversation.Attributes));
        AddText(command, "access_attributes", ConversationJsonCodec.SerializeConditionAttributes(conversation.Attributes));
        AddTextArray(command, "participants", conversation.Participants.Select(user => user.Value));
        AddText(command, "channel_kind", conversation.ChannelThread?.Channel);
        AddText(command, "channel_thread", conversation.ChannelThread?.ExternalThreadId);
        AddInstant(command, "created_at", conversation.CreatedAt);
        AddInstant(command, "last_activity_at", conversation.LastActivityAt);
        AddInstant(command, "waiting_since", conversation.WaitingSince);
        AddInstant(command, "first_reply_at", conversation.FirstReplyAt);
        AddInstant(command, "last_incoming_at", conversation.LastIncomingAt);
        AddInstant(command, "last_outgoing_at", conversation.LastOutgoingAt);
        command.Parameters.AddWithValue("last_sequence", conversation.LastSequence);
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
