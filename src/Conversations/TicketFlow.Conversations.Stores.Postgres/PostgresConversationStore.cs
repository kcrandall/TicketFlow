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
    private const string ScopeKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace)";
    private const string ConversationKey = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;

    private const string ConversationColumns = """
        c.conversation_id, c.format_version, c.inbox_id, c.contact_id, c.status, c.snoozed_until, c.priority, c.assignee_id, c.team_id, c.labels,
        c.attributes::text, c.participants, c.channel_kind, c.channel_thread_id, c.created_at, c.last_activity_at, c.waiting_since, c.first_reply_at,
        c.last_incoming_at, c.last_outgoing_at, c.last_sequence, c.contact_redirects::text, c.revision
        """;

    private const string MessageColumns = "conversation_id, inbox_id, message_id, sequence, direction, external_id, delivery_state, delivery_failure, delivery_updated_at, delivery_revision, format_version";

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
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, null, scope, id, forUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TimelineEvent>> ReadTimelineAsync(TicketScope scope, ConversationId id, long afterSequence, int limit, bool includeNotes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConversationStore.MaximumTimelineRead);
        await using var command = source.CreateCommand($"""
            SELECT e.sequence, e.event_id, e.occurred_at, e.actor_kind, e.actor_id, e.payload, e.format_version,
                m.external_id, m.delivery_state, m.delivery_failure, m.delivery_updated_at
            FROM tf_conversation_events e
            LEFT JOIN tf_conversation_messages m ON e.kind = 1 AND m.message_key = e.message_key AND m.message_id = e.message_id
            WHERE e.conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND e.conversation_id=@conversation
              AND e.{ScopeWhere.Replace(" AND ", " AND e.", StringComparison.Ordinal)}
              AND e.sequence > @after AND (@notes OR e.kind <> 2)
            ORDER BY e.sequence LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "conversation", id.Value);
        command.Parameters.AddWithValue("after", afterSequence);
        command.Parameters.AddWithValue("notes", includeNotes);
        command.Parameters.AddWithValue("limit", limit);
        var events = new List<TimelineEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            events.Add(Decode(() =>
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
            }, "timeline event"));
        return events;
    }

    public async Task<StoredMessage?> GetMessageAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        if (message == default)
            throw new ArgumentException("A message identifier is required.", nameof(message));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadMessageAsync(connection, null, scope, conversation, message, forUpdate: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredMessage?> FindMessageByExternalIdAsync(TicketScope scope, InboxId inbox, string externalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (inbox == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(inbox));
        ArgumentNullException.ThrowIfNull(externalId);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindExternalAsync(connection, null, scope, inbox, externalId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredConversation?> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(thread);
        await using var command = source.CreateCommand($"""
            SELECT {ConversationColumns} FROM tf_conversations c
            WHERE c.thread_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@inbox,@channel,@thread)
              AND c.{ScopeWhere.Replace(" AND ", " AND c.", StringComparison.Ordinal)}
              AND c.inbox_id=@inbox AND c.channel_kind=@channel AND c.channel_thread_id=@thread AND c.status <> 5
            ORDER BY c.created_at DESC, c.conversation_id DESC LIMIT 1
            """);
        AddScope(command, scope);
        AddText(command, "inbox", inbox.Value);
        AddText(command, "channel", thread.Channel);
        AddText(command, "thread", thread.ExternalThreadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConversation(reader, scope) : null;
    }

    public async Task<ConversationPage> ListAsync(TicketScope scope, ConversationListQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var translated = PostgresAccessFilterTranslator.Translate(filter, FilterColumns).OrDenyAll();
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        foreach (var parameter in translated.Parameters)
            command.Parameters.Add(parameter.Clone());
        var criteria = new List<string>();
        void Criterion(string sql, string name, object value, NpgsqlDbType type)
        {
            criteria.Add(sql);
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        }
        if (query.Inbox is { } inbox)
            Criterion("c.inbox_id=@inbox", "inbox", inbox.Value, NpgsqlDbType.Text);
        if (query.Status is { } status)
            Criterion("c.status=@status", "status", (short)status, NpgsqlDbType.Smallint);
        if (query.Assignee is { } assignee)
            Criterion("c.assignee_id=@assignee", "assignee", assignee.Value, NpgsqlDbType.Text);
        if (query.Unassigned)
            criteria.Add("c.assignee_id IS NULL");
        if (query.Team is { } team)
            Criterion("c.team_id=@team", "team", team.Value, NpgsqlDbType.Text);
        if (query.Label is { } label)
            Criterion("c.labels @> ARRAY[@label]::text[]", "label", label.Value, NpgsqlDbType.Text);
        if (query.Contact is { } contact)
            Criterion("c.contact_id=@contact", "contact", contact.Value, NpgsqlDbType.Text);
        if (query.After is { } after)
        {
            criteria.Add("(c.last_activity_at, c.conversation_id) < (@after_at, @after_id COLLATE \"C\")");
            command.Parameters.Add(new NpgsqlParameter("after_at", NpgsqlDbType.TimestampTz) { Value = after.LastActivityAt });
            AddText(command, "after_id", after.Conversation.Value);
        }
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        command.CommandText = $"""
            SELECT {ConversationColumns} FROM tf_conversations c
            WHERE c.scope_key={ScopeKey} AND c.{ScopeWhere.Replace(" AND ", " AND c.", StringComparison.Ordinal)}
              {string.Concat(criteria.Select(item => " AND " + item))}
              AND ({translated.Sql})
            ORDER BY c.last_activity_at DESC, c.conversation_id DESC LIMIT @limit
            """;
        var rows = new List<StoredConversation>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadConversation(reader, scope));
        if (rows.Count <= query.Limit)
            return new ConversationPage(rows, null);
        var page = rows[..query.Limit];
        return new ConversationPage(page, new ConversationListCursor(page[^1].Conversation.LastActivityAt, page[^1].Id));
    }

    private static async Task<StoredConversation?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ConversationId id, bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {ConversationColumns} FROM tf_conversations c
            WHERE c.{ConversationKey.Replace(" AND ", " AND c.", StringComparison.Ordinal)}{(forUpdate ? " FOR UPDATE" : "")}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "conversation", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadConversation(reader, scope) : null;
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader, scope) : null;
    }

    private static StoredConversation ReadConversation(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported conversation format version.");
        var channel = NullableText(reader, 12);
        var conversation = new Conversation(
            new ConversationId(reader.GetString(0)),
            new InboxId(reader.GetString(2)),
            new ContactId(reader.GetString(3)),
            ConversationStatus.FromKind((ConversationStatusKind)reader.GetInt16(4), NullableInstant(reader, 5)),
            Defined((ConversationPriority)reader.GetInt16(6)),
            NullableText(reader, 7) is { } assignee ? new UserId(assignee) : null,
            NullableText(reader, 8) is { } team ? new TeamId(team) : null,
            reader.GetFieldValue<string[]>(9).Select(label => new LabelId(label)),
            ConversationJsonCodec.DeserializeAttributes(reader.GetString(10)),
            reader.GetFieldValue<string[]>(11).Select(user => new UserId(user)),
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
    }, "conversation");

    private static StoredMessage ReadMessage(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(10) != 1)
            throw new FormatException("Unsupported message format version.");
        var direction = (MessageDirection)reader.GetInt16(4);
        if (!Enum.IsDefined(direction))
            throw new FormatException("Unknown message direction.");
        return new StoredMessage(scope, new ConversationId(reader.GetString(0)), new InboxId(reader.GetString(1)), new MessageId(reader.GetString(2)), reader.GetInt64(3),
            direction, NullableText(reader, 5), new MessageDelivery((MessageDeliveryState)reader.GetInt16(6), reader.GetFieldValue<DateTimeOffset>(8), NullableText(reader, 7)),
            reader.GetInt64(9));
    }, "message");

    private static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new FormatException("Unknown enumeration value.");

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableInstant(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value ?? DBNull.Value });

    private static void RequireId(ConversationId id)
    {
        if (id == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(id));
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException($"A stored conversation {description} is invalid or incompatible.", error);
        }
    }
}
