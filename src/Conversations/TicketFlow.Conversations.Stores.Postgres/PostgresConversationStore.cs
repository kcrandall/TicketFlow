using System.Data;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Conversations.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IConversationStore"/>, <see cref="IConversationOutbox"/> and
/// <see cref="IConversationAttributeDefinitionStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables live in the
/// data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized and confined
/// to one full scope (the outbox is store-wide; entries carry their scope). A conversation write — revision-fenced snapshot update
/// (which also fences the timeline head), event rows, message rows (unique external id per inbox), outbox entry and per-consumer
/// deliveries — commits in one transaction. Lists use keyset pagination over indexed columns with the translated
/// <see cref="AccessFilter"/> in the same statement. Rows that fail to decode throw. Lease times come from the supplied
/// <see cref="TimeProvider"/>, so every client must use a synchronized clock.
/// </summary>
public sealed partial class PostgresConversationStore : IConversationStore, IConversationOutbox, IConversationAttributeDefinitionStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeWhereC = "c.tenant_id=@tenant AND c.project_id=@project AND c.environment_id=@environment AND c.app_id=@app AND c.workspace_id=@workspace";
    private const string ScopeWhereE = "e.tenant_id=@tenant AND e.project_id=@project AND e.environment_id=@environment AND e.app_id=@app AND e.workspace_id=@workspace";
    private const string ScopeWhereM = "m.tenant_id=@tenant AND m.project_id=@project AND m.environment_id=@environment AND m.app_id=@app AND m.workspace_id=@workspace";
    private const string ScopeKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace)";
    private const string ConversationKey = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;
    private const string ConversationKeyC = "c.conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND c.conversation_id=@conversation AND " + ScopeWhereC;

    private const string ConversationColumns = """
        c.conversation_id, c.format_version, c.inbox_id, c.contact_id, c.status, c.snoozed_until, c.priority, c.assignee_id, c.team_id, c.labels,
        c.attributes::text, c.participants, c.channel_kind, c.channel_thread_id, c.created_at, c.last_activity_at, c.waiting_since, c.first_reply_at,
        c.last_incoming_at, c.last_outgoing_at, c.last_sequence, c.contact_redirects::text, c.revision
        """;

    private const string UnaliasedConversationColumns = """
        conversation_id, format_version, inbox_id, contact_id, status, snoozed_until, priority, assignee_id, team_id, labels,
        attributes::text, participants, channel_kind, channel_thread_id, created_at, last_activity_at, waiting_since, first_reply_at,
        last_incoming_at, last_outgoing_at, last_sequence, contact_redirects::text, revision
        """;

    private const string DeletedConversationColumns = """
        d.conversation_id, d.format_version, d.inbox_id, d.contact_id, d.status, d.snoozed_until, d.priority, d.assignee_id, d.team_id, d.labels,
        d.attributes, d.participants, d.channel_kind, d.channel_thread_id, d.created_at, d.last_activity_at, d.waiting_since, d.first_reply_at,
        d.last_incoming_at, d.last_outgoing_at, d.last_sequence, d.contact_redirects, d.revision
        """;

    private const string MessageColumns = "conversation_id, inbox_id, message_id, sequence, direction, external_id, delivery_state, delivery_failure, delivery_updated_at, delivery_revision, format_version";
    private const string MessageColumnsM = "m.conversation_id, m.inbox_id, m.message_id, m.sequence, m.direction, m.external_id, m.delivery_state, m.delivery_failure, m.delivery_updated_at, m.delivery_revision, m.format_version";

    private static readonly Dictionary<ConversationId, StoredConversation> EmptyConversations = [];

    private static readonly PostgresAccessFilterColumns FilterColumns = new()
    {
        TenantId = "c.tenant_id",
        ProjectId = "c.project_id",
        EnvironmentId = "c.environment_id",
        AppId = "c.app_id",
        WorkspaceId = "c.workspace_id",
        TeamId = "c.team_id",
        InboxId = "c.inbox_id",
        AssigneeId = "c.assignee_id",
        Labels = "c.labels",
        Attributes = "c.access_attributes",
    };

    private readonly NpgsqlDataSource source;
    private readonly TimeProvider time;

    public PostgresConversationStore(NpgsqlDataSource dataSource, TimeProvider? time = null)
    {
        source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Explicitly applies <see cref="PostgresConversationMigrations.Catalog"/> through <see cref="PostgresMigrationRunner"/>
    /// (advisory-locked, checksum-verified, idempotent). Never called implicitly.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresConversationMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<StoredConversation?> GetAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        await using var command = source.CreateCommand($"SELECT {ConversationColumns} FROM tf_conversations c WHERE {ConversationKeyC}");
        AddScope(command, scope);
        AddText(command, "conversation", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConversation(reader, scope) : null;
    }

    public async Task<IReadOnlyDictionary<ConversationId, StoredConversation>> GetManyAsync(TicketScope scope, IReadOnlyList<ConversationId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return EmptyConversations;
        if (ids.Count == 1)
        {
            var single = await GetAsync(scope, ids[0], cancellationToken).ConfigureAwait(false);
            return single is null ? EmptyConversations : new Dictionary<ConversationId, StoredConversation>(1) { [single.Id] = single };
        }
        var idValues = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            RequireId(ids[i]);
            idValues[i] = ids[i].Value;
        }
        await using var command = source.CreateCommand($"""
            SELECT {ConversationColumns}
            FROM unnest(@ids) AS k(id)
            JOIN tf_conversations c ON c.conversation_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace,k.id) AND c.conversation_id = k.id
            WHERE {ScopeWhereC}
            """);
        AddScope(command, scope);
        AddTextArray(command, "ids", idValues);
        var results = new Dictionary<ConversationId, StoredConversation>(ids.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var stored = ReadConversation(reader, scope);
            results[stored.Id] = stored;
        }
        return results;
    }

    private const string TimelineSelectWithNotes = $"""
        SELECT e.sequence, e.event_id, e.occurred_at, e.actor_kind, e.actor_id, e.payload, e.format_version,
            m.external_id, m.delivery_state, m.delivery_failure, m.delivery_updated_at
        FROM tf_conversation_events e
        LEFT JOIN tf_conversation_messages m ON e.kind = 1 AND m.message_key = e.message_key AND m.message_id = e.message_id
        WHERE e.conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND e.conversation_id=@conversation
          AND {ScopeWhereE}
          AND e.sequence > @after
        ORDER BY e.sequence LIMIT @limit
        """;

    private const string TimelineSelectWithoutNotes = $"""
        SELECT e.sequence, e.event_id, e.occurred_at, e.actor_kind, e.actor_id, e.payload, e.format_version,
            m.external_id, m.delivery_state, m.delivery_failure, m.delivery_updated_at
        FROM tf_conversation_events e
        LEFT JOIN tf_conversation_messages m ON e.kind = 1 AND m.message_key = e.message_key AND m.message_id = e.message_id
        WHERE e.conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND e.conversation_id=@conversation
          AND {ScopeWhereE}
          AND e.sequence > @after AND e.kind <> 2
        ORDER BY e.sequence LIMIT @limit
        """;

    public async Task<IReadOnlyList<TimelineEvent>> ReadTimelineAsync(TicketScope scope, ConversationId id, long afterSequence, int limit, bool includeNotes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationStore.MaximumTimelineRead);
        await using var command = source.CreateCommand(includeNotes ? TimelineSelectWithNotes : TimelineSelectWithoutNotes);
        AddScope(command, scope);
        AddText(command, "conversation", id.Value);
        AddInt64(command, "after", afterSequence);
        AddInt32(command, "limit", limit);
        var events = new List<TimelineEvent>(Math.Min(limit, 64));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            events.Add(ReadTimelineEvent(reader, id));
        return events;
    }

    public async Task<StoredMessage?> GetMessageAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        if (message == default)
            throw new ArgumentException("A message identifier is required.", nameof(message));
        await using var command = source.CreateCommand($"""
            SELECT {MessageColumns} FROM tf_conversation_messages
            WHERE message_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@message) AND conversation_id=@conversation AND message_id=@message
              AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "message", message.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader, scope) : null;
    }

    public async Task<StoredMessage?> FindMessageByExternalIdAsync(TicketScope scope, InboxId inbox, string externalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (inbox == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(inbox));
        ArgumentNullException.ThrowIfNull(externalId);
        await using var command = source.CreateCommand($"""
            SELECT {MessageColumns} FROM tf_conversation_messages
            WHERE external_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@inbox,@external) AND inbox_id=@inbox AND external_id=@external AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "inbox", inbox.Value);
        AddText(command, "external", externalId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader, scope) : null;
    }

    public async Task<StoredConversation?> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(thread);
        await using var command = source.CreateCommand($"""
            SELECT {ConversationColumns} FROM tf_conversations c
            WHERE c.thread_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@inbox,@channel,@thread)
              AND {ScopeWhereC}
              AND c.inbox_id=@inbox AND c.channel_kind=@channel AND c.channel_thread_id=@thread AND c.status <> 5
            ORDER BY c.created_at DESC, c.conversation_id DESC LIMIT 1
            """);
        AddScope(command, scope);
        AddText(command, "inbox", inbox.Value);
        AddText(command, "channel", thread.Channel);
        AddText(command, "thread", thread.ExternalThreadId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConversation(reader, scope) : null;
    }

    public async Task<ConversationPage> ListAsync(TicketScope scope, ConversationListQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        await using var command = CreateListCommand(scope, query, filter);
        var rows = new List<StoredConversation>(Math.Min(query.Limit + 1, 64));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadConversation(reader, scope));
        if (rows.Count <= query.Limit)
            return new ConversationPage(rows, null);
        rows.RemoveAt(query.Limit);
        var last = rows[^1];
        return new ConversationPage(rows, new ConversationListCursor(last.Conversation.LastActivityAt, last.Id));
    }

    /// <summary>
    /// The list statement: criteria as equality predicates matching the <c>(scope_key, criterion[, status], activity)</c> indexes, a label
    /// criterion driven from the <c>tf_conversation_labels</c> side table, the keyset cursor as a row comparison and the translated access
    /// filter in the same statement. Internal so plan tests can EXPLAIN the exact SQL.
    /// </summary>
    internal NpgsqlCommand CreateListCommand(TicketScope scope, ConversationListQuery query, AccessFilter filter)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var translated = PostgresAccessFilterTranslator.Translate(filter, FilterColumns).OrDenyAll();
        var command = source.CreateCommand();
        AddScope(command, scope);
        for (var i = 0; i < translated.Parameters.Count; i++)
            command.Parameters.Add(translated.Parameters[i].Clone());
        var sql = new StringBuilder(512);
        sql.Append("SELECT ").Append(ConversationColumns);
        var order = query.Label is null ? 'c' : 'l';
        if (query.Label is { } label)
        {
            sql.Append(" FROM tf_conversation_labels l JOIN tf_conversations c ON c.conversation_key=l.conversation_key AND c.conversation_id=l.conversation_id WHERE l.scope_key=")
                .Append(ScopeKey).Append(" AND l.label=@label AND ").Append(ScopeWhereC);
            AddText(command, "label", label.Value);
        }
        else
            sql.Append(" FROM tf_conversations c WHERE c.scope_key=").Append(ScopeKey).Append(" AND ").Append(ScopeWhereC);
        if (query.Inbox is { } inbox)
        {
            sql.Append(" AND c.inbox_id=@inbox");
            AddText(command, "inbox", inbox.Value);
        }
        if (query.Status is { } status)
        {
            sql.Append(" AND c.status=@status");
            AddInt16(command, "status", (short)status);
        }
        if (query.Assignee is { } assignee)
        {
            sql.Append(" AND c.assignee_id=@assignee");
            AddText(command, "assignee", assignee.Value);
        }
        if (query.Unassigned)
            sql.Append(" AND c.assignee_id IS NULL");
        if (query.Team is { } team)
        {
            sql.Append(" AND c.team_id=@team");
            AddText(command, "team", team.Value);
        }
        if (query.Contact is { } contact)
        {
            sql.Append(" AND c.contact_id=@contact");
            AddText(command, "contact", contact.Value);
        }
        if (query.After is { } after)
        {
            sql.Append(" AND (").Append(order).Append(".last_activity_at, ").Append(order).Append(".conversation_id) < (@after_at, @after_id COLLATE \"C\")");
            AddInstant(command, "after_at", after.LastActivityAt);
            AddText(command, "after_id", after.Conversation.Value);
        }
        AddInt32(command, "limit", query.Limit + 1);
        sql.Append(" AND (").Append(translated.Sql).Append(") ORDER BY ").Append(order).Append(".last_activity_at DESC, ").Append(order).Append(".conversation_id DESC LIMIT @limit");
        command.CommandText = sql.ToString();
        return command;
    }

    private static async Task<StoredMessage?> LoadMessageAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ConversationId conversation,
        MessageId message, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {MessageColumns} FROM tf_conversation_messages
            WHERE message_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@message) AND conversation_id=@conversation AND message_id=@message
              AND {ScopeWhere}{(forUpdate ? " FOR UPDATE" : "")}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "message", message.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader, scope) : null;
    }

    private static async Task<StoredMessage?> FindExternalAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, InboxId inbox, string externalId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {MessageColumns} FROM tf_conversation_messages
            WHERE external_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@inbox,@external) AND inbox_id=@inbox AND external_id=@external AND {ScopeWhere}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "inbox", inbox.Value);
        AddText(command, "external", externalId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader, scope) : null;
    }

    private static TimelineEvent ReadTimelineEvent(NpgsqlDataReader reader, ConversationId id)
    {
        try
        {
            if (reader.GetInt16(6) != 1)
                throw new FormatException("Unsupported event format version.");
            var payload = ConversationJsonCodec.DeserializeEvent(reader.GetString(5));
            if (payload.Value is MessageEvent message)
            {
                if (reader.IsDBNull(8))
                    throw new FormatException("A message event has no message row.");
                payload = message.WithDelivery(new MessageDelivery((MessageDeliveryState)reader.GetInt16(8), reader.GetFieldValue<DateTimeOffset>(10),
                    NullableText(reader, 9)), NullableText(reader, 7));
            }
            return new TimelineEvent(id, reader.GetInt64(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2),
                new AccessActor((AccessActorKind)reader.GetInt16(3), reader.GetString(4)), payload);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored conversation timeline event is invalid or incompatible.", error);
        }
    }

    private static StoredConversation ReadConversation(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported conversation format version.");
            var channel = NullableText(reader, 12);
            var rawLabels = reader.GetFieldValue<string[]>(9);
            LabelId[] labels = [];
            if (rawLabels.Length > 0)
            {
                labels = new LabelId[rawLabels.Length];
                for (var i = 0; i < rawLabels.Length; i++)
                    labels[i] = new LabelId(rawLabels[i]);
            }
            var rawParticipants = reader.GetFieldValue<string[]>(11);
            UserId[] participants = [];
            if (rawParticipants.Length > 0)
            {
                participants = new UserId[rawParticipants.Length];
                for (var i = 0; i < rawParticipants.Length; i++)
                    participants[i] = new UserId(rawParticipants[i]);
            }
            var conversation = new Conversation(
                new ConversationId(reader.GetString(0)),
                new InboxId(reader.GetString(2)),
                new ContactId(reader.GetString(3)),
                ConversationStatus.FromKind((ConversationStatusKind)reader.GetInt16(4), NullableInstant(reader, 5)),
                Defined((ConversationPriority)reader.GetInt16(6)),
                NullableText(reader, 7) is { } assignee ? new UserId(assignee) : null,
                NullableText(reader, 8) is { } team ? new TeamId(team) : null,
                labels,
                ConversationJsonCodec.DeserializeAttributes(reader.GetString(10)),
                participants,
                channel is null ? null : new ChannelThreadRef(channel, reader.GetString(13)),
                reader.GetFieldValue<DateTimeOffset>(14),
                reader.GetFieldValue<DateTimeOffset>(15),
                NullableInstant(reader, 16),
                NullableInstant(reader, 17),
                NullableInstant(reader, 18),
                NullableInstant(reader, 19),
                reader.GetInt64(20),
                ConversationJsonCodec.DeserializeRedirects(reader.GetString(21)));
            return new StoredConversation(scope, conversation, reader.GetInt64(22));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored conversation conversation is invalid or incompatible.", error);
        }
    }

    private static StoredMessage ReadMessage(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(10) != 1)
                throw new FormatException("Unsupported message format version.");
            var direction = (MessageDirection)reader.GetInt16(4);
            if (!Enum.IsDefined(direction))
                throw new FormatException("Unknown message direction.");
            return new StoredMessage(scope, new ConversationId(reader.GetString(0)), new InboxId(reader.GetString(1)), new MessageId(reader.GetString(2)), reader.GetInt64(3),
                direction, NullableText(reader, 5), new MessageDelivery((MessageDeliveryState)reader.GetInt16(6), reader.GetFieldValue<DateTimeOffset>(8), NullableText(reader, 7)),
                reader.GetInt64(9));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored conversation message is invalid or incompatible.", error);
        }
    }

    private static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new FormatException("Unknown enumeration value.");

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableInstant(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddScope(NpgsqlCommand command, TicketScope scope) => AddScope(command.Parameters, scope);

    private static void AddScope(NpgsqlParameterCollection parameters, TicketScope scope)
    {
        AddText(parameters, "tenant", scope.TenantId);
        AddText(parameters, "project", scope.ProjectId);
        AddText(parameters, "environment", scope.EnvironmentId);
        AddText(parameters, "app", scope.AppId);
        AddText(parameters, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) => AddText(command.Parameters, name, value);

    private static void AddText(NpgsqlParameterCollection parameters, string name, string? value) =>
        parameters.Add(value is null
            ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value }
            : new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });

    private static void AddTextArray(NpgsqlCommand command, string name, string[] values) => AddTextArray(command.Parameters, name, values);

    private static void AddTextArray(NpgsqlParameterCollection parameters, string name, string[] values) =>
        parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset value) => AddInstant(command.Parameters, name, value);

    private static void AddInstant(NpgsqlParameterCollection parameters, string name, DateTimeOffset value) =>
        parameters.Add(new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) => AddInstant(command.Parameters, name, value);

    private static void AddInstant(NpgsqlParameterCollection parameters, string name, DateTimeOffset? value) =>
        parameters.Add(value is { } instant
            ? new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = instant }
            : new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });

    private static void AddInt16(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddInt16(NpgsqlParameterCollection parameters, string name, short value) =>
        parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddInt32(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddInt64(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddInt64(NpgsqlParameterCollection parameters, string name, long value) =>
        parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddGuid(NpgsqlParameterCollection parameters, string name, Guid value) =>
        parameters.Add(new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid) { TypedValue = value });

    private static void AddBool(NpgsqlCommand command, string name, bool value) =>
        command.Parameters.Add(new NpgsqlParameter<bool>(name, NpgsqlDbType.Boolean) { TypedValue = value });

    private static void RequireId(ConversationId id)
    {
        if (id == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(id));
    }
}
