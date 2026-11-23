using System.Buffers;
using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Events.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IEventLog"/> and <see cref="IEventSubscriptionStore"/> over an application-owned
/// <see cref="NpgsqlDataSource"/>. Tables live in the data source's current schema and are created only by <see cref="InitializeAsync"/>.
/// Appends lock the scope's head row (<c>FOR UPDATE</c>), skip event ids that already exist and insert the rest with gapless sequences in
/// one transaction, so concurrent appenders serialize per workspace (head rows have fillfactor 50 so their updates stay HOT). Delivery claims lock due subscriptions with <c>FOR UPDATE SKIP
/// LOCKED</c> over a partial due index and bump their fence; completion, failure and dead-lettering are fenced on the fence and the lease,
/// which uses the store's <see cref="TimeProvider"/> (clients need synchronized clocks). Claims skip suspended subscriptions and are ordered by
/// claim time, which completion resets (round-robin). Retention prunes rows recorded before the cutoff through a BRIN index (not strictly
/// oldest first; delivery records any gap it meets). Subscription limits are checked under a scope
/// advisory lock. Every statement is parameterized; rows that fail to decode throw.
/// </summary>
public sealed class PostgresEventStore : IEventLog, IEventSubscriptionStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + ScopeWhere;
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeParameters = "@tenant, @project, @environment, @app, @workspace";
    private const string SubscriptionWhere = "subscription_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@subscription) AND subscription_id=@subscription AND " + ScopeWhere;
    private const string EventColumns = "sequence, event_id, type, occurred_at, recorded_at, actor_kind, actor_id, conversation_id, inbox_id, team_id, assignee_id, contact_id, data, format_version";
    private const string SubscriptionColumns = "tenant_id, project_id, environment_id, app_id, workspace_id, subscription_id, format_version, name, sink_kind, sink_target, filter, principal_id, "
        + "privacy, enabled, batch_size, revision, created_at, delivery_cursor, attempts, available_at, last_error, last_delivered_at, fence, lease_until, generation, suspended";
    private const string SubscriptionColumnsS = "s.tenant_id, s.project_id, s.environment_id, s.app_id, s.workspace_id, s.subscription_id, s.format_version, s.name, s.sink_kind, s.sink_target, s.filter, s.principal_id, "
        + "s.privacy, s.enabled, s.batch_size, s.revision, s.created_at, s.delivery_cursor, s.attempts, s.available_at, s.last_error, s.last_delivered_at, s.fence, s.lease_until, s.generation, s.suspended";
    private const string LetterColumns = "subscription_id, sequence, event_id, type, attempts, error, dead_lettered_at, format_version";

    private const string DeleteSubscriptionSql = $"""
        WITH removed AS (
            DELETE FROM tf_event_subscriptions WHERE {SubscriptionWhere} AND revision=@expected
            RETURNING {SubscriptionColumns}
        ),
        cleared AS (
            DELETE FROM tf_event_dead_letters d
            USING removed r
            WHERE d.subscription_key = tf_key_v1(r.tenant_id, r.project_id, r.environment_id, r.app_id, r.workspace_id, r.subscription_id)
        )
        SELECT {SubscriptionColumns} FROM removed
        """;

    private const string ReplaySubscriptionSql = $"""
        WITH updated AS (
            UPDATE tf_event_subscriptions SET delivery_cursor=@after, attempts=0, available_at=@now, last_error=NULL, fence=fence+1, lease_until=NULL, generation=generation+1,
                suspended=false WHERE {SubscriptionWhere}
            RETURNING 1
        ),
        cleared AS (
            DELETE FROM tf_event_dead_letters
            WHERE subscription_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@subscription) AND sequence > @after
              AND EXISTS (SELECT 1 FROM updated)
        )
        SELECT count(*) FROM updated
        """;

    private const string DeadLetterWithLettersSql = $"""
        WITH updated AS (
            UPDATE tf_event_subscriptions SET delivery_cursor=GREATEST(delivery_cursor, @cursor), attempts=0, last_error=@error, available_at=@available, lease_until=NULL
            WHERE {SubscriptionWhere} AND fence=@fence AND lease_until > @now
            RETURNING 1
        ),
        inserted AS (
            INSERT INTO tf_event_dead_letters({ScopeColumns}, {LetterColumns})
            SELECT {ScopeParameters}, @subscription, t.sequence, t.event_id, t.type, t.attempts, t.error, t.dead_lettered_at, 1
            FROM updated CROSS JOIN unnest(@sequences, @ids, @types, @attempts, @errors, @times) AS t(sequence, event_id, type, attempts, error, dead_lettered_at)
            ON CONFLICT (subscription_key, sequence) DO UPDATE SET event_id=excluded.event_id, type=excluded.type, attempts=excluded.attempts, error=excluded.error,
                dead_lettered_at=excluded.dead_lettered_at
        )
        SELECT count(*) FROM updated
        """;

    private const string DeadLetterEmptySql = $"""
        UPDATE tf_event_subscriptions SET delivery_cursor=GREATEST(delivery_cursor, @cursor), attempts=0, last_error=@error, available_at=@available, lease_until=NULL
        WHERE {SubscriptionWhere} AND fence=@fence AND lease_until > @now
        """;

    private const string AppendEventsCteSql = $"""
        WITH input AS (
            SELECT ord, event_id, type, occurred_at, actor_kind, actor_id, conversation_id, inbox_id, team_id, assignee_id, contact_id, data
            FROM unnest(@ids, @types, @occurred, @actor_kinds, @actor_ids, @conversations, @inboxes, @teams, @assignees, @contacts, @data) WITH ORDINALITY
                AS t(event_id, type, occurred_at, actor_kind, actor_id, conversation_id, inbox_id, team_id, assignee_id, contact_id, data, ord)
        ),
        novel AS (
            SELECT i.*, @head + row_number() OVER (ORDER BY i.ord) AS sequence
            FROM input i
            WHERE NOT EXISTS (
                SELECT 1 FROM tf_events e
                WHERE e.scope_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND e.event_id = i.event_id
            )
        ),
        inserted AS (
            INSERT INTO tf_events({ScopeColumns}, {EventColumns})
            SELECT {ScopeParameters}, n.sequence, n.event_id, n.type, n.occurred_at, @recorded, n.actor_kind, n.actor_id, n.conversation_id, n.inbox_id, n.team_id,
                n.assignee_id, n.contact_id, n.data, 1
            FROM novel n
            RETURNING sequence, event_id
        ),
        updated_head AS (
            UPDATE tf_event_heads SET last_sequence = @head + (SELECT count(*) FROM novel)
            WHERE {ScopeKeyWhere} AND EXISTS (SELECT 1 FROM novel)
        )
        SELECT sequence, event_id FROM inserted ORDER BY sequence
        """;

    private readonly NpgsqlDataSource source;
    private readonly TimeProvider time;

    public PostgresEventStore(NpgsqlDataSource dataSource, TimeProvider? time = null)
    {
        source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Explicitly applies <see cref="PostgresEventMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresEventMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<EventAppendResult> AppendAsync(TicketScope scope, IReadOnlyList<IntegrationEventDraft> drafts, DateTimeOffset recordedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IEventLog.RequireDrafts(drafts);
        recordedAt = EventText.Instant(recordedAt);

        // Deduplicate drafts in memory before opening a connection or taking the head lock.
        var uniqueDrafts = new List<IntegrationEventDraft>(drafts.Count);
        var byId = new Dictionary<Guid, IntegrationEventDraft>(drafts.Count);
        var inBatchDuplicates = 0;
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            if (byId.TryAdd(draft.Event.Id, draft))
                uniqueDrafts.Add(draft);
            else
                inBatchDuplicates++;
        }

        var count = uniqueDrafts.Count;
        var ids = new Guid[count];
        var types = new string[count];
        var occurred = new DateTimeOffset[count];
        var actorKinds = new string?[count];
        var actorIds = new string?[count];
        var conversations = new string?[count];
        var inboxes = new string?[count];
        var teams = new string?[count];
        var assignees = new string?[count];
        var contacts = new string?[count];
        var data = new string[count];
        for (var i = 0; i < count; i++)
        {
            var draft = uniqueDrafts[i];
            ids[i] = draft.Event.Id;
            types[i] = draft.Event.Type;
            occurred[i] = draft.Event.Time;
            actorKinds[i] = draft.Event.Actor?.Kind;
            actorIds[i] = draft.Event.Actor?.Id;
            conversations[i] = draft.Resource.Conversation?.Value;
            inboxes[i] = draft.Resource.Inbox?.Value;
            teams[i] = draft.Resource.Team?.Value;
            assignees[i] = draft.Resource.Assignee?.Value;
            contacts[i] = draft.Resource.Contact?.Value;
            data[i] = IntegrationEventCodec.EncodeData(draft.Event.Data);
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long head;
        await using (var lockCommand = new NpgsqlCommand($"""
            INSERT INTO tf_event_heads({ScopeColumns}, last_sequence) VALUES({ScopeParameters}, 0) ON CONFLICT (scope_key) DO NOTHING;
            SELECT last_sequence FROM tf_event_heads WHERE {ScopeKeyWhere} FOR UPDATE;
            """, connection, transaction))
        {
            AddScope(lockCommand, scope);
            head = (long)(await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        var appended = new List<StoredIntegrationEvent>(count);
        await using (var command = new NpgsqlCommand(AppendEventsCteSql, connection, transaction))
        {
            AddScope(command, scope);
            AddLong(command, "head", head);
            AddTime(command, "recorded", recordedAt);
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = ids });
            command.Parameters.Add(new NpgsqlParameter<string[]>("types", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = types });
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = occurred });
            AddNullableTextArray(command, "actor_kinds", actorKinds);
            AddNullableTextArray(command, "actor_ids", actorIds);
            AddNullableTextArray(command, "conversations", conversations);
            AddNullableTextArray(command, "inboxes", inboxes);
            AddNullableTextArray(command, "teams", teams);
            AddNullableTextArray(command, "assignees", assignees);
            AddNullableTextArray(command, "contacts", contacts);
            command.Parameters.Add(new NpgsqlParameter<string[]>("data", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = data });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var seq = reader.GetInt64(0);
                var eventId = reader.GetGuid(1);
                var draft = byId[eventId];
                appended.Add(new StoredIntegrationEvent(scope, seq, draft.Event, draft.Resource, recordedAt));
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var duplicates = inBatchDuplicates + (count - appended.Count);
        return new EventAppendResult(appended, duplicates);
    }

    public async Task<IReadOnlyList<StoredIntegrationEvent>> ReadAsync(TicketScope scope, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventLog.MaximumRead);
        await using var command = source.CreateCommand($"SELECT {EventColumns} FROM tf_events WHERE {ScopeKeyWhere} AND sequence > @after ORDER BY sequence LIMIT @limit");
        AddScope(command, scope);
        AddLong(command, "after", afterSequence);
        AddInt(command, "limit", limit);
        var events = new List<StoredIntegrationEvent>(Math.Min(limit, 64));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            events.Add(ReadEvent(reader, scope));
        return events;
    }

    public async Task<EventLogHead> GetHeadAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT (SELECT last_sequence FROM tf_event_heads WHERE {ScopeKeyWhere}), (SELECT min(sequence) FROM tf_events WHERE {ScopeKeyWhere})
            """);
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new EventLogHead(reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(0) ? 0 : reader.GetInt64(0));
    }

    public async Task<int> PruneAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventLog.MaximumPrune);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_events WHERE ctid IN (SELECT ctid FROM tf_events WHERE recorded_at < @before LIMIT @limit FOR UPDATE SKIP LOCKED)
            """);
        AddTime(command, "before", olderThan);
        AddInt(command, "limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> GetCheckpointAsync(TicketScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IEventLog.RequireCheckpointName(name);
        await using var command = source.CreateCommand($"SELECT value FROM tf_event_checkpoints WHERE {ScopeKeyWhere} AND name=@name");
        AddScope(command, scope);
        AddText(command, "name", name);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    public async Task<bool> SetCheckpointAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IEventLog.RequireCheckpointName(name);
        await using var command = source.CreateCommand(expected is null
            ? $"INSERT INTO tf_event_checkpoints({ScopeColumns}, name, value) VALUES({ScopeParameters}, @name, @value) ON CONFLICT (scope_key, name) DO NOTHING"
            : $"UPDATE tf_event_checkpoints SET value=@value WHERE {ScopeKeyWhere} AND name=@name AND value=@expected");
        AddScope(command, scope);
        AddText(command, "name", name);
        AddLong(command, "value", value);
        if (expected is { } current)
            AddLong(command, "expected", current);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<StoredEventSubscription?> GetAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default) =>
        (await GetRowAsync(scope, id, cancellationToken).ConfigureAwait(false))?.Subscription;

    public async Task<(StoredEventSubscription Subscription, EventDeliveryState State)?> GetWithStateAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default) =>
        await GetRowAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } row ? (row.Subscription, row.State) : null;

    public async Task<IReadOnlyList<StoredEventSubscription>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {SubscriptionColumns} FROM tf_event_subscriptions WHERE {ScopeKeyWhere} ORDER BY subscription_id COLLATE \"C\"");
        AddScope(command, scope);
        var list = new List<StoredEventSubscription>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(ReadSubscription(reader).Subscription);
        return list;
    }

    public async Task<IReadOnlyList<(StoredEventSubscription Subscription, EventDeliveryState State)>> ListWithStateAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {SubscriptionColumns} FROM tf_event_subscriptions WHERE {ScopeKeyWhere} ORDER BY subscription_id COLLATE \"C\"");
        AddScope(command, scope);
        var list = new List<(StoredEventSubscription, EventDeliveryState)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ReadSubscription(reader);
            list.Add((row.Subscription, row.State));
        }
        return list;
    }

    public async Task<EventWriteResult<StoredEventSubscription>> CreateAsync(TicketScope scope, EventSubscription subscription, long cursor, DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        createdAt = EventText.Instant(createdAt);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter<string[]>("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { TypedValue = ["ticketflow.events", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value] });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        bool inserted;
        bool duplicate;
        await using (var create = new NpgsqlCommand($"""
            WITH existing AS (
                SELECT 1 FROM tf_event_subscriptions WHERE {SubscriptionWhere}
            ),
            cnt AS (
                SELECT count(*) AS n FROM tf_event_subscriptions WHERE {ScopeKeyWhere}
            ),
            ins AS (
                INSERT INTO tf_event_subscriptions({ScopeColumns}, subscription_id, format_version, name, sink_kind, sink_target, filter, principal_id, privacy, enabled, batch_size,
                    revision, created_at, delivery_cursor, attempts, available_at, last_error, last_delivered_at, fence, lease_until)
                SELECT {ScopeParameters}, @subscription, 1, @name, @sink_kind, @sink_target, @filter, @principal, @privacy, @enabled, @batch, 1, @created, @cursor, 0, @created, NULL, NULL, 0, NULL
                WHERE NOT EXISTS (SELECT 1 FROM existing) AND (SELECT n FROM cnt) < @max
                ON CONFLICT (subscription_key) DO NOTHING
                RETURNING 1
            )
            SELECT EXISTS(SELECT 1 FROM ins), EXISTS(SELECT 1 FROM existing)
            """, connection, transaction))
        {
            BindSubscription(create, scope, subscription);
            AddTime(create, "created", createdAt);
            AddLong(create, "cursor", cursor);
            AddLong(create, "max", IEventSubscriptionStore.MaximumSubscriptionsPerWorkspace);
            await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            inserted = reader.GetBoolean(0);
            duplicate = reader.GetBoolean(1);
        }
        if (inserted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new EventWriteApplied<StoredEventSubscription>(new StoredEventSubscription(scope, subscription, 1, createdAt));
        }
        return new EventWriteRejected(duplicate ? EventWriteRejection.Duplicate : EventWriteRejection.LimitReached);
    }

    public async Task<EventWriteResult<StoredEventSubscription>> UpdateAsync(TicketScope scope, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(subscription);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            UPDATE tf_event_subscriptions SET name=@name, sink_kind=@sink_kind, sink_target=@sink_target, filter=@filter, principal_id=@principal, privacy=@privacy,
                enabled=@enabled, batch_size=@batch, revision=revision+1, attempts=0, suspended=false
            WHERE {SubscriptionWhere} AND revision=@expected RETURNING created_at
            """, connection))
        {
            BindSubscription(command, scope, subscription);
            AddLong(command, "expected", expectedRevision);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime created)
                return new EventWriteApplied<StoredEventSubscription>(new StoredEventSubscription(scope, subscription, expectedRevision + 1, Instant(created)));
        }
        return await SubscriptionRevisionAsync(connection, scope, subscription.Id, cancellationToken).ConfigureAwait(false) is { } current
            ? new EventWriteConflict(current)
            : EventWriteNotFound.Instance;
    }

    public async Task<EventWriteResult<StoredEventSubscription>> DeleteAsync(TicketScope scope, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(DeleteSubscriptionSql, connection))
        {
            AddScope(command, scope);
            AddText(command, "subscription", id.Value);
            AddLong(command, "expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new EventWriteApplied<StoredEventSubscription>(ReadSubscription(reader).Subscription);
        }
        return await SubscriptionRevisionAsync(connection, scope, id, cancellationToken).ConfigureAwait(false) is { } current
            ? new EventWriteConflict(current)
            : EventWriteNotFound.Instance;
    }

    public async Task<EventDeliveryState?> GetStateAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default) =>
        (await GetRowAsync(scope, id, cancellationToken).ConfigureAwait(false))?.State;

    public async Task<IReadOnlyList<EventDeliveryClaim>> ClaimAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventSubscriptionStore.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > IEventSubscriptionStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease));
        await using var command = source.CreateCommand($"""
            WITH due AS (
                SELECT s.subscription_key, h.last_sequence FROM tf_event_subscriptions s
                JOIN tf_event_heads h ON h.scope_key = s.scope_key
                WHERE s.enabled AND NOT s.suspended AND s.available_at <= @now AND (s.lease_until IS NULL OR s.lease_until <= @now) AND h.last_sequence > s.delivery_cursor
                ORDER BY s.available_at
                LIMIT @limit
                FOR UPDATE OF s SKIP LOCKED)
            UPDATE tf_event_subscriptions s SET fence = s.fence + 1, lease_until = @until FROM due WHERE s.subscription_key = due.subscription_key
            RETURNING {SubscriptionColumnsS}, due.last_sequence
            """);
        AddTime(command, "now", now);
        AddTime(command, "until", EventText.Instant(now + lease));
        AddInt(command, "limit", limit);
        var claims = new List<EventDeliveryClaim>(Math.Min(limit, 32));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ReadSubscription(reader);
            var headSequence = reader.GetInt64(26);
            claims.Add(new EventDeliveryClaim(row.Subscription, row.State, row.Fence, row.LeaseUntil!.Value) { HeadSequence = headSequence });
        }
        if (claims.Count > 1)
            claims.Sort(static (a, b) => a.State.AvailableAt.CompareTo(b.State.AvailableAt));
        return claims;
    }

    public async Task<bool> CompleteAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, DateTimeOffset? deliveredAt, DateTimeOffset availableAt, string? note = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        var validNote = note is null ? null : IEventSubscriptionStore.RequireError(note);
        await using var command = source.CreateCommand($"""
            UPDATE tf_event_subscriptions SET delivery_cursor=GREATEST(delivery_cursor, @cursor), attempts=0, last_error=@note,
                last_delivered_at=COALESCE(@delivered, last_delivered_at), available_at=@available, lease_until=NULL
            WHERE {SubscriptionWhere} AND fence=@fence AND lease_until > @now
            """);
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        AddLong(command, "fence", fence);
        AddTime(command, "now", time.GetUtcNow());
        AddLong(command, "cursor", cursor);
        AddTime(command, "delivered", EventText.Instant(deliveredAt));
        AddTime(command, "available", EventText.Instant(availableAt));
        AddText(command, "note", validNote);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> SuspendAsync(TicketScope scope, EventSubscriptionId id, long fence, string error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        var validError = IEventSubscriptionStore.RequireError(error);
        await using var command = source.CreateCommand($"""
            UPDATE tf_event_subscriptions SET attempts=0, last_error=@error, suspended=true, lease_until=NULL
            WHERE {SubscriptionWhere} AND fence=@fence AND lease_until > @now
            """);
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        AddLong(command, "fence", fence);
        AddTime(command, "now", time.GetUtcNow());
        AddText(command, "error", validError);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> FailAsync(TicketScope scope, EventSubscriptionId id, long fence, int attempts, DateTimeOffset retryAt, string error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var validError = IEventSubscriptionStore.RequireError(error);
        await using var command = source.CreateCommand($"""
            UPDATE tf_event_subscriptions SET attempts=@attempts, available_at=@retry, last_error=@error, lease_until=NULL
            WHERE {SubscriptionWhere} AND fence=@fence AND lease_until > @now
            """);
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        AddLong(command, "fence", fence);
        AddTime(command, "now", time.GetUtcNow());
        AddInt(command, "attempts", attempts);
        AddTime(command, "retry", EventText.Instant(retryAt));
        AddText(command, "error", validError);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeadLetterAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, IReadOnlyList<EventDeadLetter> letters, DateTimeOffset availableAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        ArgumentNullException.ThrowIfNull(letters);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        for (var i = 0; i < letters.Count; i++)
            IEventSubscriptionStore.RequireError(letters[i].Error);
        if (letters.Count == 0)
        {
            await using var cmd = source.CreateCommand(DeadLetterEmptySql);
            AddScope(cmd, scope);
            AddText(cmd, "subscription", id.Value);
            AddLong(cmd, "cursor", cursor);
            AddLong(cmd, "fence", fence);
            AddText(cmd, "error", null);
            AddTime(cmd, "now", time.GetUtcNow());
            AddTime(cmd, "available", EventText.Instant(availableAt));
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        var count = letters.Count;
        var sequences = new long[count];
        var ids = new Guid[count];
        var types = new string[count];
        var attempts = new int[count];
        var errors = new string[count];
        var times = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            var letter = letters[i];
            sequences[i] = letter.Sequence;
            ids[i] = letter.EventId;
            types[i] = letter.Type;
            attempts[i] = letter.Attempts;
            errors[i] = letter.Error;
            times[i] = EventText.Instant(letter.DeadLetteredAt);
        }
        await using var command = source.CreateCommand(DeadLetterWithLettersSql);
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        AddLong(command, "cursor", cursor);
        AddLong(command, "fence", fence);
        AddText(command, "error", letters[0].Error);
        AddTime(command, "now", time.GetUtcNow());
        AddTime(command, "available", EventText.Instant(availableAt));
        command.Parameters.Add(new NpgsqlParameter<long[]>("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = sequences });
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = ids });
        command.Parameters.Add(new NpgsqlParameter<string[]>("types", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = types });
        command.Parameters.Add(new NpgsqlParameter<int[]>("attempts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { TypedValue = attempts });
        command.Parameters.Add(new NpgsqlParameter<string[]>("errors", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = errors });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = times });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    public async Task<bool> ReplayAsync(TicketScope scope, EventSubscriptionId id, long afterSequence, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        await using var command = source.CreateCommand(ReplaySubscriptionSql);
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        AddLong(command, "after", afterSequence);
        AddTime(command, "now", EventText.Instant(now));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    public async Task<IReadOnlyList<EventDeadLetter>> ListDeadLettersAsync(TicketScope scope, EventSubscriptionId id, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventSubscriptionStore.MaximumDeadLetterRead);
        await using var command = source.CreateCommand($"""
            SELECT {LetterColumns} FROM tf_event_dead_letters
            WHERE subscription_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@subscription) AND subscription_id=@subscription AND {ScopeWhere} AND sequence > @after
            ORDER BY sequence LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        AddLong(command, "after", afterSequence);
        AddInt(command, "limit", limit);
        var letters = new List<EventDeadLetter>(Math.Min(limit, 32));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt16(7) != 1)
                throw new InvalidOperationException("Unsupported dead letter format.");
            letters.Add(new EventDeadLetter(new EventSubscriptionId(reader.GetString(0)), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetString(5), Instant(reader.GetDateTime(6))));
        }
        return letters;
    }

    public async Task<int> PruneDeadLettersAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IEventLog.MaximumPrune);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_event_dead_letters WHERE ctid IN (
                SELECT ctid FROM tf_event_dead_letters WHERE dead_lettered_at < @before ORDER BY dead_lettered_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            """);
        AddTime(command, "before", olderThan);
        AddInt(command, "limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record SubscriptionRow(StoredEventSubscription Subscription, EventDeliveryState State, long Fence, DateTimeOffset? LeaseUntil);

    private static async Task<long?> SubscriptionRevisionAsync(NpgsqlConnection connection, TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_event_subscriptions WHERE {SubscriptionWhere}", connection);
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private async Task<SubscriptionRow?> GetRowAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        await using var command = source.CreateCommand($"SELECT {SubscriptionColumns} FROM tf_event_subscriptions WHERE {SubscriptionWhere}");
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSubscription(reader) : null;
    }

    private static SubscriptionRow ReadSubscription(NpgsqlDataReader reader)
    {
        try
        {
            var scope = new TicketScope(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), new WorkspaceId(reader.GetString(4)));
            if (reader.GetInt16(6) != 1)
                throw new FormatException("Unsupported subscription format.");
            var subscription = new EventSubscription(new EventSubscriptionId(reader.GetString(5)), reader.GetString(7), new EventSinkRef(reader.GetString(8), reader.GetString(9)),
                DecodeFilter(reader.GetString(10)), new ApiClientId(reader.GetString(11)), (EventPrivacy)reader.GetInt16(12), reader.GetBoolean(13), reader.GetInt32(14));
            var stored = new StoredEventSubscription(scope, subscription, reader.GetInt64(15), Instant(reader.GetDateTime(16)));
            var state = new EventDeliveryState(reader.GetInt64(17), reader.GetInt32(18), Instant(reader.GetDateTime(19)), reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.IsDBNull(21) ? null : Instant(reader.GetDateTime(21)), reader.GetInt64(24), reader.GetBoolean(25));
            return new SubscriptionRow(stored, state, reader.GetInt64(22), reader.IsDBNull(23) ? null : Instant(reader.GetDateTime(23)));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException)
        {
            throw new InvalidOperationException("A stored event subscription could not be decoded.", error);
        }
    }

    private static StoredIntegrationEvent ReadEvent(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(13) != 1)
                throw new FormatException("Unsupported event format.");
            var type = reader.GetString(2);
            var actor = reader.IsDBNull(5) ? null : new EventActor(reader.GetString(5), reader.GetString(6));
            var data = IntegrationEventCodec.DecodeData(type, reader.GetString(12));
            var resource = new EventResource(
                reader.IsDBNull(7) ? null : new ConversationId(reader.GetString(7)),
                reader.IsDBNull(8) ? null : new InboxId(reader.GetString(8)),
                reader.IsDBNull(9) ? null : new TeamId(reader.GetString(9)),
                reader.IsDBNull(10) ? null : new UserId(reader.GetString(10)),
                reader.IsDBNull(11) ? null : new ContactId(reader.GetString(11)));
            return new StoredIntegrationEvent(scope, reader.GetInt64(0), new IntegrationEvent(reader.GetGuid(1), Instant(reader.GetDateTime(3)), actor, data), resource,
                Instant(reader.GetDateTime(4)));
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            throw new InvalidOperationException("A stored integration event could not be decoded.", error);
        }
    }

    private static void BindSubscription(NpgsqlCommand command, TicketScope scope, EventSubscription subscription)
    {
        AddScope(command, scope);
        AddText(command, "subscription", subscription.Id.Value);
        AddText(command, "name", subscription.Name);
        AddText(command, "sink_kind", subscription.Sink.Kind);
        AddText(command, "sink_target", subscription.Sink.Target);
        AddText(command, "filter", EncodeFilter(subscription.Filter));
        AddText(command, "principal", subscription.Principal.Value);
        AddShort(command, "privacy", (short)subscription.Privacy);
        AddBool(command, "enabled", subscription.Enabled);
        AddInt(command, "batch", subscription.BatchSize);
    }

    /// <summary>The persisted filter: <c>{"v":1,"types":[…],"inboxes":[…],"teams":[…]}</c>.</summary>
    internal static string EncodeFilter(EventSubscriptionFilter filter)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, 1);
            writer.WriteStartArray("types"u8);
            for (var i = 0; i < filter.Types.Count; i++)
                writer.WriteStringValue(filter.Types[i].Value);
            writer.WriteEndArray();
            writer.WriteStartArray("inboxes"u8);
            for (var i = 0; i < filter.Inboxes.Count; i++)
                writer.WriteStringValue(filter.Inboxes[i].Value);
            writer.WriteEndArray();
            writer.WriteStartArray("teams"u8);
            for (var i = 0; i < filter.Teams.Count; i++)
                writer.WriteStringValue(filter.Teams[i].Value);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static EventSubscriptionFilter DecodeFilter(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Unsupported filter format.");
        var propCount = 0;
        foreach (var _ in root.EnumerateObject())
            propCount++;
        if (propCount != 4 || root.GetProperty("v"u8).GetInt32() != 1)
            throw new FormatException("Unsupported filter format.");
        var typesEl = root.GetProperty("types"u8);
        var types = new EventTypePattern[typesEl.GetArrayLength()];
        var idx = 0;
        foreach (var item in typesEl.EnumerateArray())
            types[idx++] = new EventTypePattern(item.GetString() ?? throw new FormatException("Expected a string."));
        var inboxesEl = root.GetProperty("inboxes"u8);
        var inboxes = new InboxId[inboxesEl.GetArrayLength()];
        idx = 0;
        foreach (var item in inboxesEl.EnumerateArray())
            inboxes[idx++] = new InboxId(item.GetString() ?? throw new FormatException("Expected a string."));
        var teamsEl = root.GetProperty("teams"u8);
        var teams = new TeamId[teamsEl.GetArrayLength()];
        idx = 0;
        foreach (var item in teamsEl.EnumerateArray())
            teams[idx++] = new TeamId(item.GetString() ?? throw new FormatException("Expected a string."));
        return new EventSubscriptionFilter(types, inboxes, teams);
    }

    private static DateTimeOffset Instant(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static EventSubscriptionId Require(EventSubscriptionId id) => id.IsInitialized ? id : throw new ArgumentException("A subscription identifier is required.", nameof(id));

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(value is not null
            ? new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value }
            : new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value });

    private static void AddNullableTextArray(NpgsqlCommand command, string name, string?[] values) =>
        command.Parameters.Add(new NpgsqlParameter<string?[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values });

    private static void AddTime(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(value is { } instant
            ? new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = instant }
            : new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });

    private static void AddInt(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddLong(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddShort(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddBool(NpgsqlCommand command, string name, bool value) =>
        command.Parameters.Add(new NpgsqlParameter<bool>(name, NpgsqlDbType.Boolean) { TypedValue = value });
}
