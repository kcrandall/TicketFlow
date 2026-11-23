using System.Buffers;
using System.Collections.Frozen;
using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Core;

namespace TicketFlow.Routing.Stores.Postgres;

public sealed partial class PostgresRoutingStore
{
    private const string QueueColumns = """
        q.conversation_id, q.format_version, q.inbox_id, q.team_id, q.contact_id, q.labels, q.access_attributes::text, q.waiting_since, q.conversation_revision,
        q.target_inbox_id, q.queued_at, q.overflow_stage, q.wait_exceeded, q.next_attempt_at, q.attempts
        """;

    private const string UnaliasedQueueColumns = """
        conversation_id, format_version, inbox_id, team_id, contact_id, labels, access_attributes::text, waiting_since, conversation_revision,
        target_inbox_id, queued_at, overflow_stage, wait_exceeded, next_attempt_at, attempts
        """;

    private const string QueueKey = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;
    private const string QueueKeyQ = "q.conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND q.conversation_id=@conversation AND " + ScopeWhereQ;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedAttrBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedAttrWriter;

    private static readonly FrozenDictionary<string, string> EmptyAttributes = FrozenDictionary<string, string>.Empty;

    private static readonly PostgresAccessFilterColumns QueueFilterColumns = new()
    {
        TenantId = "q.tenant_id",
        ProjectId = "q.project_id",
        EnvironmentId = "q.environment_id",
        AppId = "q.app_id",
        WorkspaceId = "q.workspace_id",
        TeamId = "q.team_id",
        InboxId = "q.inbox_id",
        AssigneeId = "q.assignee_id",
        Labels = "q.labels",
        Attributes = "q.access_attributes",
    };

    public async Task<StoredRoutingQueueEntry> EnqueueAsync(TicketScope scope, RoutingQueueSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(snapshot);
        var labelValues = ExtractLabels(snapshot.Labels);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var upsert = new NpgsqlCommand($"""
            WITH clk AS (SELECT {NowSql} AS now)
            INSERT INTO tf_routing_queue({ScopeColumns}, conversation_id, format_version, inbox_id, team_id, assignee_id, contact_id, labels, access_attributes,
                waiting_since, conversation_revision, target_inbox_id, queued_at, overflow_stage, wait_exceeded, next_attempt_at, attempts, fence, lease_until)
            VALUES({ScopeValues}, @conversation, 1, @inbox, @team, NULL, @contact, @labels, @attributes::jsonb, @waiting, @revision, @inbox, (SELECT now FROM clk), 0, false, (SELECT now FROM clk), 0, 0, NULL)
            ON CONFLICT (conversation_key) DO UPDATE SET inbox_id=EXCLUDED.inbox_id, team_id=EXCLUDED.team_id, contact_id=EXCLUDED.contact_id,
                labels=EXCLUDED.labels, access_attributes=EXCLUDED.access_attributes, waiting_since=EXCLUDED.waiting_since,
                conversation_revision=EXCLUDED.conversation_revision
            WHERE tf_routing_queue.conversation_revision <= EXCLUDED.conversation_revision
            RETURNING {UnaliasedQueueColumns}
            """, connection))
        {
            AddScope(upsert, scope);
            AddText(upsert, "conversation", snapshot.Conversation.Value);
            AddText(upsert, "inbox", snapshot.Inbox.Value);
            AddText(upsert, "team", snapshot.Team?.Value);
            AddText(upsert, "contact", snapshot.Contact.Value);
            AddTextArray(upsert, "labels", labelValues);
            AddText(upsert, "attributes", SerializeFlatAttributes(snapshot.AccessAttributes));
            AddInstant(upsert, "waiting", snapshot.WaitingSince);
            AddLong(upsert, "revision", snapshot.ConversationRevision);
            AddInstant(upsert, "now", OverrideNow());
            await using var reader = await upsert.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoredRoutingQueueEntry(scope, ReadQueue(reader, 0));
        }
        return await LoadQueueAsync(connection, scope, snapshot.Conversation, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The queue entry vanished during the write.");
    }

    public async Task<bool> RemoveAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"DELETE FROM tf_routing_queue WHERE {QueueKey} AND conversation_revision <= @revision");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddLong(command, "revision", conversationRevision);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    async Task<StoredRoutingQueueEntry?> IRoutingQueueStore.GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        await using var command = source.CreateCommand($"SELECT {QueueColumns} FROM tf_routing_queue q WHERE {QueueKeyQ}");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new StoredRoutingQueueEntry(scope, ReadQueue(reader, 0)) : null;
    }

    public async Task<RoutingQueuePage> ListAsync(TicketScope scope, RoutingQueueQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var translated = PostgresAccessFilterTranslator.Translate(filter, QueueFilterColumns).OrDenyAll();
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        for (var i = 0; i < translated.Parameters.Count; i++)
            command.Parameters.Add(translated.Parameters[i].Clone());
        var sql = new StringBuilder(512);
        sql.Append("SELECT ").Append(QueueColumns).Append(" FROM tf_routing_queue q WHERE q.scope_key=").Append(ScopeKey).Append(" AND ").Append(ScopeWhereQ);
        if (query.Inbox is { } inbox)
        {
            sql.Append(" AND q.target_inbox_id=@target");
            AddText(command, "target", inbox.Value);
        }
        if (query.Team is { } team)
        {
            sql.Append(" AND q.team_id=@team");
            AddText(command, "team", team.Value);
        }
        if (query.After is { } after)
        {
            sql.Append(" AND (q.waiting_since, q.conversation_id) > (@after_at, @after_id COLLATE \"C\")");
            AddInstant(command, "after_at", after.WaitingSince);
            AddText(command, "after_id", after.Conversation.Value);
        }
        AddInt(command, "limit", query.Limit + 1);
        sql.Append(" AND (").Append(translated.Sql).Append(") ORDER BY q.waiting_since, q.conversation_id LIMIT @limit");
        command.CommandText = sql.ToString();
        var rows = new List<StoredRoutingQueueEntry>(Math.Min(query.Limit + 1, 64));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new StoredRoutingQueueEntry(scope, ReadQueue(reader, 0)));
        if (rows.Count <= query.Limit)
            return new RoutingQueuePage(rows, null);
        rows.RemoveAt(query.Limit);
        var last = rows[^1];
        return new RoutingQueuePage(rows, new RoutingQueueCursor(last.Entry.Snapshot.WaitingSince, last.Entry.Conversation));
    }

    public async Task<IReadOnlyList<RoutingQueueClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingQueueStore.MaximumClaim);
        RequireLease(lease);
        await using var command = source.CreateCommand($"""
            -- Taken in (next_attempt_at, conversation_key) order, served by tf_routing_queue_due_v2; re-sorted by waiting time below.
            WITH clk AS (SELECT {NowSql} AS now),
            due AS (
                SELECT conversation_key FROM tf_routing_queue
                WHERE next_attempt_at <= (SELECT now FROM clk) AND (lease_until IS NULL OR lease_until <= (SELECT now FROM clk))
                ORDER BY next_attempt_at, conversation_key LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_routing_queue q SET fence=nextval('tf_routing_fences'), lease_until=(SELECT now FROM clk) + @lease
            FROM due WHERE q.conversation_key=due.conversation_key
            RETURNING {QueueColumns}, q.tenant_id, q.project_id, q.environment_id, q.app_id, q.workspace_id, q.fence, q.lease_until
            """);
        AddInstant(command, "now", OverrideNow());
        AddInterval(command, "lease", lease);
        AddInt(command, "limit", limit);
        var claims = new List<RoutingQueueClaim>(Math.Min(limit, 32));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            claims.Add(new RoutingQueueClaim(ReadScope(reader, 15), ReadQueue(reader, 0), reader.GetInt64(20), Instant(reader.GetFieldValue<DateTimeOffset>(21))));
        if (claims.Count > 1)
            claims.Sort(static (a, b) =>
            {
                var cmp = a.Entry.Snapshot.WaitingSince.CompareTo(b.Entry.Snapshot.WaitingSince);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Entry.Conversation.Value, b.Entry.Conversation.Value);
            });
        return claims;
    }

    public async Task<bool> CompleteAttemptAsync(TicketScope scope, ConversationId conversation, long fence, RoutingQueueUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        ArgumentNullException.ThrowIfNull(update);
        await using var command = source.CreateCommand($"""
            UPDATE tf_routing_queue SET next_attempt_at=@next, target_inbox_id=@target, overflow_stage=@stage, wait_exceeded=@exceeded, attempts=attempts+1, lease_until=NULL
            WHERE {QueueKey} AND fence=@fence AND lease_until > {NowSql}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInstant(command, "next", update.NextAttemptAt);
        AddText(command, "target", update.TargetInbox.Value);
        AddInt(command, "stage", update.OverflowStage);
        AddBool(command, "exceeded", update.WaitExceeded);
        AddLong(command, "fence", fence);
        AddInstant(command, "now", OverrideNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> NudgeAsync(TicketScope scope, InboxId? targetInbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (targetInbox == default(InboxId))
            throw new ArgumentException("The inbox must be initialized when supplied.", nameof(targetInbox));
        var sql = targetInbox is null
            ? $"WITH clk AS (SELECT {NowSql} AS now) UPDATE tf_routing_queue SET next_attempt_at=(SELECT now FROM clk) WHERE scope_key={ScopeKey} AND {ScopeWhere} AND next_attempt_at > (SELECT now FROM clk)"
            : $"WITH clk AS (SELECT {NowSql} AS now) UPDATE tf_routing_queue SET next_attempt_at=(SELECT now FROM clk) WHERE scope_key={ScopeKey} AND {ScopeWhere} AND next_attempt_at > (SELECT now FROM clk) AND target_inbox_id=@target";
        await using var command = source.CreateCommand(sql);
        AddScope(command, scope);
        if (targetInbox is { } target)
            AddText(command, "target", target.Value);
        AddInstant(command, "now", OverrideNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredRoutingQueueEntry?> LoadQueueAsync(NpgsqlConnection connection, TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {QueueColumns} FROM tf_routing_queue q WHERE {QueueKeyQ}", connection);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new StoredRoutingQueueEntry(scope, ReadQueue(reader, 0)) : null;
    }

    private static string[] ExtractLabels(IReadOnlyList<LabelId> labels)
    {
        if (labels.Count == 0)
            return [];
        var arr = new string[labels.Count];
        for (var i = 0; i < labels.Count; i++)
            arr[i] = labels[i].Value;
        return arr;
    }

    private static string SerializeFlatAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        if (attributes.Count == 0)
            return "{}";
        var buffer = sharedAttrBuffer;
        if (buffer is null)
            sharedAttrBuffer = buffer = new ArrayBufferWriter<byte>(256);
        else
            buffer.ResetWrittenCount();
        var writer = sharedAttrWriter;
        if (writer is null)
            sharedAttrWriter = writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);
        writer.WriteStartObject();
        foreach (var (key, value) in attributes)
            writer.WriteString(key, value);
        writer.WriteEndObject();
        writer.Flush();
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        if (buffer.Capacity > 65536)
            sharedAttrBuffer = null;
        return json;
    }

    private static IReadOnlyDictionary<string, string> DeserializeFlatAttributes(string json)
    {
        if (string.Equals(json, "{}", StringComparison.Ordinal))
            return EmptyAttributes;
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("Attributes are required.");
        Dictionary<string, string>? result = null;
        foreach (var prop in document.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                throw new FormatException("Attribute values must be strings.");
            result ??= new Dictionary<string, string>(StringComparer.Ordinal);
            result[prop.Name] = prop.Value.GetString()!;
        }
        return result ?? (IReadOnlyDictionary<string, string>)EmptyAttributes;
    }

    private static RoutingQueueEntry ReadQueue(NpgsqlDataReader reader, int offset)
    {
        try
        {
            if (reader.GetInt16(offset + 1) != 1)
                throw new FormatException("Unsupported queue format version.");
            var attributes = DeserializeFlatAttributes(reader.GetString(offset + 6));
            var rawLabels = reader.GetFieldValue<string[]>(offset + 5);
            LabelId[] labels = [];
            if (rawLabels.Length > 0)
            {
                labels = new LabelId[rawLabels.Length];
                for (var i = 0; i < rawLabels.Length; i++)
                    labels[i] = new LabelId(rawLabels[i]);
            }
            var snapshot = new RoutingQueueSnapshot(new ConversationId(reader.GetString(offset)), new InboxId(reader.GetString(offset + 2)),
                NullableText(reader, offset + 3) is { } team ? new TeamId(team) : null, new ContactId(reader.GetString(offset + 4)),
                labels, attributes, reader.GetFieldValue<DateTimeOffset>(offset + 7), reader.GetInt64(offset + 8));
            return new RoutingQueueEntry(snapshot, new InboxId(reader.GetString(offset + 9)), reader.GetFieldValue<DateTimeOffset>(offset + 10), reader.GetInt32(offset + 11),
                reader.GetBoolean(offset + 12), reader.GetFieldValue<DateTimeOffset>(offset + 13), reader.GetInt32(offset + 14));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException("A stored routing queue entry is invalid or incompatible.", error);
        }
    }
}
