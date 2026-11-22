using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
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

    private const string QueueKey = "conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation AND " + ScopeWhere;

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
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var upsert = new NpgsqlCommand($"""
            INSERT INTO tf_routing_queue({ScopeColumns}, conversation_id, format_version, inbox_id, team_id, assignee_id, contact_id, labels, access_attributes,
                waiting_since, conversation_revision, target_inbox_id, queued_at, overflow_stage, wait_exceeded, next_attempt_at, attempts, fence, lease_until)
            VALUES({ScopeValues}, @conversation, 1, @inbox, @team, NULL, @contact, @labels, @attributes::jsonb, @waiting, @revision, @inbox, @now, 0, false, @now, 0, 0, NULL)
            ON CONFLICT (conversation_key) DO UPDATE SET inbox_id=EXCLUDED.inbox_id, team_id=EXCLUDED.team_id, contact_id=EXCLUDED.contact_id,
                labels=EXCLUDED.labels, access_attributes=EXCLUDED.access_attributes, waiting_since=EXCLUDED.waiting_since,
                conversation_revision=EXCLUDED.conversation_revision
            WHERE tf_routing_queue.conversation_revision <= EXCLUDED.conversation_revision
            """, connection))
        {
            AddScope(upsert, scope);
            AddText(upsert, "conversation", snapshot.Conversation.Value);
            AddText(upsert, "inbox", snapshot.Inbox.Value);
            AddText(upsert, "team", snapshot.Team?.Value);
            AddText(upsert, "contact", snapshot.Contact.Value);
            AddTextArray(upsert, "labels", snapshot.Labels.Select(label => label.Value));
            AddText(upsert, "attributes", JsonSerializer.Serialize(snapshot.AccessAttributes.ToDictionary(StringComparer.Ordinal)));
            AddInstant(upsert, "waiting", snapshot.WaitingSince);
            AddLong(upsert, "revision", snapshot.ConversationRevision);
            AddInstant(upsert, "now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadQueueAsync(connection, scope, conversation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RoutingQueuePage> ListAsync(TicketScope scope, RoutingQueueQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        var translated = PostgresAccessFilterTranslator.Translate(filter, QueueFilterColumns).OrDenyAll();
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        foreach (var parameter in translated.Parameters)
            command.Parameters.Add(parameter.Clone());
        var criteria = new List<string>();
        if (query.Inbox is { } inbox)
        {
            criteria.Add("q.target_inbox_id=@target");
            AddText(command, "target", inbox.Value);
        }
        if (query.Team is { } team)
        {
            criteria.Add("q.team_id=@team");
            AddText(command, "team", team.Value);
        }
        if (query.After is { } after)
        {
            criteria.Add("(q.waiting_since, q.conversation_id) > (@after_at, @after_id COLLATE \"C\")");
            AddInstant(command, "after_at", after.WaitingSince);
            AddText(command, "after_id", after.Conversation.Value);
        }
        AddInt(command, "limit", query.Limit + 1);
        command.CommandText = $"""
            SELECT {QueueColumns} FROM tf_routing_queue q
            WHERE q.scope_key={ScopeKey} AND q.{ScopeWhere.Replace(" AND ", " AND q.", StringComparison.Ordinal)}
              {string.Concat(criteria.Select(item => " AND " + item))}
              AND ({translated.Sql})
            ORDER BY q.waiting_since, q.conversation_id LIMIT @limit
            """;
        var rows = new List<StoredRoutingQueueEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new StoredRoutingQueueEntry(scope, ReadQueue(reader, 0)));
        if (rows.Count <= query.Limit)
            return new RoutingQueuePage(rows, null);
        var page = rows[..query.Limit];
        return new RoutingQueuePage(page, new RoutingQueueCursor(page[^1].Entry.Snapshot.WaitingSince, page[^1].Entry.Conversation));
    }

    public async Task<IReadOnlyList<RoutingQueueClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IRoutingQueueStore.MaximumClaim);
        RequireLease(lease);
        var now = await NowAsync(cancellationToken).ConfigureAwait(false);
        await using var command = source.CreateCommand($"""
            -- Taken in (next_attempt_at, conversation_key) order, served by tf_routing_queue_due_v2; re-sorted by waiting time below.
            WITH due AS (
                SELECT conversation_key FROM tf_routing_queue
                WHERE next_attempt_at <= @now AND (lease_until IS NULL OR lease_until <= @now)
                ORDER BY next_attempt_at, conversation_key LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_routing_queue q SET fence=nextval('tf_routing_fences'), lease_until=@until
            FROM due WHERE q.conversation_key=due.conversation_key
            RETURNING {QueueColumns}, q.tenant_id, q.project_id, q.environment_id, q.app_id, q.workspace_id, q.fence
            """);
        AddInstant(command, "now", now);
        AddInstant(command, "until", now + lease);
        AddInt(command, "limit", limit);
        var claims = new List<RoutingQueueClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            claims.Add(new RoutingQueueClaim(ReadScope(reader, 15), ReadQueue(reader, 0), reader.GetInt64(20), now + lease));
        return [.. claims.OrderBy(claim => claim.Entry.Snapshot.WaitingSince).ThenBy(claim => claim.Entry.Conversation.Value, StringComparer.Ordinal)];
    }

    public async Task<bool> CompleteAttemptAsync(TicketScope scope, ConversationId conversation, long fence, RoutingQueueUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireConversation(conversation);
        ArgumentNullException.ThrowIfNull(update);
        await using var command = source.CreateCommand($"""
            UPDATE tf_routing_queue SET next_attempt_at=@next, target_inbox_id=@target, overflow_stage=@stage, wait_exceeded=@exceeded, attempts=attempts+1, lease_until=NULL
            WHERE {QueueKey} AND fence=@fence AND lease_until > @now
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInstant(command, "next", update.NextAttemptAt);
        AddText(command, "target", update.TargetInbox.Value);
        AddInt(command, "stage", update.OverflowStage);
        command.Parameters.AddWithValue("exceeded", update.WaitExceeded);
        AddLong(command, "fence", fence);
        AddInstant(command, "now", await NowAsync(cancellationToken).ConfigureAwait(false));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> NudgeAsync(TicketScope scope, InboxId? targetInbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (targetInbox == default(InboxId))
            throw new ArgumentException("The inbox must be initialized when supplied.", nameof(targetInbox));
        await using var command = source.CreateCommand($"""
            UPDATE tf_routing_queue SET next_attempt_at=@now
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND next_attempt_at > @now AND (@target::text IS NULL OR target_inbox_id=@target)
            """);
        AddScope(command, scope);
        AddText(command, "target", targetInbox?.Value);
        AddInstant(command, "now", await NowAsync(cancellationToken).ConfigureAwait(false));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredRoutingQueueEntry?> LoadQueueAsync(NpgsqlConnection connection, TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {QueueColumns} FROM tf_routing_queue q WHERE q.{QueueKey.Replace(" AND ", " AND q.", StringComparison.Ordinal)}",
            connection);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new StoredRoutingQueueEntry(scope, ReadQueue(reader, 0)) : null;
    }

    private static RoutingQueueEntry ReadQueue(NpgsqlDataReader reader, int offset) => Decode(() =>
    {
        if (reader.GetInt16(offset + 1) != 1)
            throw new FormatException("Unsupported queue format version.");
        var attributes = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(offset + 6)) ?? throw new FormatException("Attributes are required.");
        var snapshot = new RoutingQueueSnapshot(new ConversationId(reader.GetString(offset)), new InboxId(reader.GetString(offset + 2)),
            NullableText(reader, offset + 3) is { } team ? new TeamId(team) : null, new ContactId(reader.GetString(offset + 4)),
            reader.GetFieldValue<string[]>(offset + 5).Select(label => new LabelId(label)), attributes, reader.GetFieldValue<DateTimeOffset>(offset + 7),
            reader.GetInt64(offset + 8));
        return new RoutingQueueEntry(snapshot, new InboxId(reader.GetString(offset + 9)), reader.GetFieldValue<DateTimeOffset>(offset + 10), reader.GetInt32(offset + 11),
            reader.GetBoolean(offset + 12), reader.GetFieldValue<DateTimeOffset>(offset + 13), reader.GetInt32(offset + 14));
    }, "queue entry");
}
