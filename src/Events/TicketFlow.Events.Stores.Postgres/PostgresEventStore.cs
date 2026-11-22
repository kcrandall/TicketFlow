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
    private const string LetterColumns = "subscription_id, sequence, event_id, type, attempts, error, dead_lettered_at, format_version";

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
        var encoded = drafts.Select(draft => (Draft: draft, Data: IntegrationEventCodec.EncodeData(draft.Event.Data))).ToList();
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long head;
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO tf_event_heads({ScopeColumns}, last_sequence) VALUES({ScopeParameters}, 0) ON CONFLICT (scope_key) DO NOTHING;
            SELECT last_sequence FROM tf_event_heads WHERE {ScopeKeyWhere} FOR UPDATE;
            """, connection, transaction))
        {
            AddScope(command, scope);
            head = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        var existing = new HashSet<Guid>();
        await using (var command = new NpgsqlCommand($"SELECT event_id FROM tf_events WHERE {ScopeKeyWhere} AND event_id = ANY(@ids)", connection, transaction))
        {
            AddScope(command, scope);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = encoded.Select(item => item.Draft.Event.Id).Distinct().ToArray() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                existing.Add(reader.GetGuid(0));
        }
        var appended = new List<StoredIntegrationEvent>();
        var rows = new List<(StoredIntegrationEvent Stored, string Data)>();
        var duplicates = 0;
        foreach (var (draft, data) in encoded)
        {
            if (!existing.Add(draft.Event.Id))
            {
                duplicates++;
                continue;
            }
            var stored = new StoredIntegrationEvent(scope, head + rows.Count + 1, draft.Event, draft.Resource, recordedAt);
            rows.Add((stored, data));
            appended.Add(stored);
        }
        if (rows.Count > 0)
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO tf_events({ScopeColumns}, {EventColumns})
                SELECT {ScopeParameters}, t.sequence, t.event_id, t.type, t.occurred_at, @recorded, t.actor_kind, t.actor_id, t.conversation_id, t.inbox_id, t.team_id,
                    t.assignee_id, t.contact_id, t.data, 1
                FROM unnest(@sequences, @ids, @types, @occurred, @actor_kinds, @actor_ids, @conversations, @inboxes, @teams, @assignees, @contacts, @data)
                    AS t(sequence, event_id, type, occurred_at, actor_kind, actor_id, conversation_id, inbox_id, team_id, assignee_id, contact_id, data);
                UPDATE tf_event_heads SET last_sequence=@head WHERE {ScopeKeyWhere};
                """, connection, transaction);
            AddScope(command, scope);
            AddTime(command, "recorded", recordedAt);
            command.Parameters.Add(new NpgsqlParameter("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(row => row.Stored.Sequence).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = rows.Select(row => row.Stored.Event.Id).ToArray() });
            AddTextArray(command, "types", rows.Select(row => row.Stored.Event.Type));
            command.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = rows.Select(row => row.Stored.Event.Time).ToArray() });
            AddTextArray(command, "actor_kinds", rows.Select(row => row.Stored.Event.Actor?.Kind));
            AddTextArray(command, "actor_ids", rows.Select(row => row.Stored.Event.Actor?.Id));
            AddTextArray(command, "conversations", rows.Select(row => row.Stored.Resource.Conversation?.Value));
            AddTextArray(command, "inboxes", rows.Select(row => row.Stored.Resource.Inbox?.Value));
            AddTextArray(command, "teams", rows.Select(row => row.Stored.Resource.Team?.Value));
            AddTextArray(command, "assignees", rows.Select(row => row.Stored.Resource.Assignee?.Value));
            AddTextArray(command, "contacts", rows.Select(row => row.Stored.Resource.Contact?.Value));
            AddTextArray(command, "data", rows.Select(row => row.Data));
            command.Parameters.AddWithValue("head", head + rows.Count);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        command.Parameters.AddWithValue("after", afterSequence);
        command.Parameters.AddWithValue("limit", limit);
        var events = new List<StoredIntegrationEvent>();
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
        command.Parameters.AddWithValue("limit", limit);
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
        command.Parameters.AddWithValue("value", value);
        if (expected is { } current)
            command.Parameters.AddWithValue("expected", current);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<StoredEventSubscription?> GetAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken = default) =>
        (await GetRowAsync(scope, id, cancellationToken).ConfigureAwait(false))?.Subscription;

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
            lockCommand.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
                { Value = (string[])["ticketflow.events", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value] });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_event_subscriptions WHERE {ScopeKeyWhere}", connection, transaction))
        {
            AddScope(count, scope);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= IEventSubscriptionStore.MaximumSubscriptionsPerWorkspace)
                return new EventWriteRejected(EventWriteRejection.LimitReached);
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_event_subscriptions({ScopeColumns}, subscription_id, format_version, name, sink_kind, sink_target, filter, principal_id, privacy, enabled, batch_size,
                revision, created_at, delivery_cursor, attempts, available_at, last_error, last_delivered_at, fence, lease_until)
            VALUES({ScopeParameters}, @subscription, 1, @name, @sink_kind, @sink_target, @filter, @principal, @privacy, @enabled, @batch, 1, @created, @cursor, 0, @created, NULL, NULL, 0, NULL)
            ON CONFLICT (subscription_key) DO NOTHING
            """, connection, transaction))
        {
            BindSubscription(insert, scope, subscription);
            AddTime(insert, "created", createdAt);
            insert.Parameters.AddWithValue("cursor", cursor);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return new EventWriteRejected(EventWriteRejection.Duplicate);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventWriteApplied<StoredEventSubscription>(new StoredEventSubscription(scope, subscription, 1, createdAt));
    }

    public async Task<EventWriteResult<StoredEventSubscription>> UpdateAsync(TicketScope scope, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(subscription);
        await using var command = source.CreateCommand($"""
            UPDATE tf_event_subscriptions SET name=@name, sink_kind=@sink_kind, sink_target=@sink_target, filter=@filter, principal_id=@principal, privacy=@privacy,
                enabled=@enabled, batch_size=@batch, revision=revision+1, attempts=0, suspended=false
            WHERE {SubscriptionWhere} AND revision=@expected RETURNING created_at
            """);
        BindSubscription(command, scope, subscription);
        command.Parameters.AddWithValue("expected", expectedRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DateTime created)
            return new EventWriteApplied<StoredEventSubscription>(new StoredEventSubscription(scope, subscription, expectedRevision + 1, Instant(created)));
        return await GetAsync(scope, subscription.Id, cancellationToken).ConfigureAwait(false) is { } current
            ? new EventWriteConflict(current.Revision)
            : EventWriteNotFound.Instance;
    }

    public async Task<EventWriteResult<StoredEventSubscription>> DeleteAsync(TicketScope scope, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        StoredEventSubscription? removed = null;
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_event_subscriptions WHERE {SubscriptionWhere} AND revision=@expected RETURNING {SubscriptionColumns}", connection, transaction))
        {
            AddScope(command, scope);
            AddText(command, "subscription", id.Value);
            command.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                removed = ReadSubscription(reader).Subscription;
        }
        if (removed is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } current ? new EventWriteConflict(current.Revision) : EventWriteNotFound.Instance;
        }
        await using (var letters = new NpgsqlCommand("DELETE FROM tf_event_dead_letters WHERE subscription_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@subscription)",
            connection, transaction))
        {
            AddScope(letters, scope);
            AddText(letters, "subscription", id.Value);
            await letters.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EventWriteApplied<StoredEventSubscription>(removed);
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
                SELECT s.subscription_key FROM tf_event_subscriptions s
                JOIN tf_event_heads h ON h.scope_key = s.scope_key
                WHERE s.enabled AND NOT s.suspended AND s.available_at <= @now AND (s.lease_until IS NULL OR s.lease_until <= @now) AND h.last_sequence > s.delivery_cursor
                ORDER BY s.available_at
                LIMIT @limit
                FOR UPDATE OF s SKIP LOCKED)
            UPDATE tf_event_subscriptions s SET fence = s.fence + 1, lease_until = @until FROM due WHERE s.subscription_key = due.subscription_key
            RETURNING {string.Join(", ", SubscriptionColumns.Split(", ").Select(column => "s." + column))}
            """);
        AddTime(command, "now", now);
        AddTime(command, "until", EventText.Instant(now + lease));
        command.Parameters.AddWithValue("limit", limit);
        var claims = new List<EventDeliveryClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = ReadSubscription(reader);
            claims.Add(new EventDeliveryClaim(row.Subscription, row.State, row.Fence, row.LeaseUntil!.Value));
        }
        return [.. claims.OrderBy(claim => claim.State.AvailableAt)];
    }

    public Task<bool> CompleteAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, DateTimeOffset? deliveredAt, DateTimeOffset availableAt, string? note = null,
        CancellationToken cancellationToken = default) =>
        FencedAsync(scope, id, fence, """
            delivery_cursor=GREATEST(delivery_cursor, @cursor), attempts=0, last_error=@note, last_delivered_at=COALESCE(@delivered, last_delivered_at), available_at=@available,
                lease_until=NULL
            """, command =>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(cursor);
            command.Parameters.AddWithValue("cursor", cursor);
            AddTime(command, "delivered", EventText.Instant(deliveredAt));
            AddTime(command, "available", EventText.Instant(availableAt));
            AddText(command, "note", note is null ? null : IEventSubscriptionStore.RequireError(note));
        }, cancellationToken);

    public Task<bool> SuspendAsync(TicketScope scope, EventSubscriptionId id, long fence, string error, CancellationToken cancellationToken = default) =>
        FencedAsync(scope, id, fence, "attempts=0, last_error=@error, suspended=true, lease_until=NULL",
            command => AddText(command, "error", IEventSubscriptionStore.RequireError(error)), cancellationToken);

    public Task<bool> FailAsync(TicketScope scope, EventSubscriptionId id, long fence, int attempts, DateTimeOffset retryAt, string error, CancellationToken cancellationToken = default) =>
        FencedAsync(scope, id, fence, "attempts=@attempts, available_at=@retry, last_error=@error, lease_until=NULL", command =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
            command.Parameters.AddWithValue("attempts", attempts);
            AddTime(command, "retry", EventText.Instant(retryAt));
            AddText(command, "error", IEventSubscriptionStore.RequireError(error));
        }, cancellationToken);

    public async Task<bool> DeadLetterAsync(TicketScope scope, EventSubscriptionId id, long fence, long cursor, IReadOnlyList<EventDeadLetter> letters, DateTimeOffset availableAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        ArgumentNullException.ThrowIfNull(letters);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        foreach (var letter in letters)
            IEventSubscriptionStore.RequireError(letter.Error);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            UPDATE tf_event_subscriptions SET delivery_cursor=GREATEST(delivery_cursor, @cursor), attempts=0, last_error=@error, available_at=@available, lease_until=NULL
            WHERE {SubscriptionWhere} AND fence=@fence AND lease_until > @now
            """, connection, transaction))
        {
            AddScope(command, scope);
            AddText(command, "subscription", id.Value);
            command.Parameters.AddWithValue("cursor", cursor);
            command.Parameters.AddWithValue("fence", fence);
            AddText(command, "error", letters.Count > 0 ? letters[0].Error : null);
            AddTime(command, "now", time.GetUtcNow());
            AddTime(command, "available", EventText.Instant(availableAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return false;
        }
        if (letters.Count > 0)
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO tf_event_dead_letters({ScopeColumns}, {LetterColumns})
                SELECT {ScopeParameters}, @subscription, t.sequence, t.event_id, t.type, t.attempts, t.error, t.dead_lettered_at, 1
                FROM unnest(@sequences, @ids, @types, @attempts, @errors, @times) AS t(sequence, event_id, type, attempts, error, dead_lettered_at)
                ON CONFLICT (subscription_key, sequence) DO UPDATE SET event_id=excluded.event_id, type=excluded.type, attempts=excluded.attempts, error=excluded.error,
                    dead_lettered_at=excluded.dead_lettered_at
                """, connection, transaction);
            AddScope(insert, scope);
            AddText(insert, "subscription", id.Value);
            insert.Parameters.Add(new NpgsqlParameter("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = letters.Select(letter => letter.Sequence).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = letters.Select(letter => letter.EventId).ToArray() });
            AddTextArray(insert, "types", letters.Select(letter => letter.Type));
            insert.Parameters.Add(new NpgsqlParameter("attempts", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = letters.Select(letter => letter.Attempts).ToArray() });
            AddTextArray(insert, "errors", letters.Select(letter => letter.Error));
            insert.Parameters.Add(new NpgsqlParameter("times", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
                { Value = letters.Select(letter => EventText.Instant(letter.DeadLetteredAt)).ToArray() });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ReplayAsync(TicketScope scope, EventSubscriptionId id, long afterSequence, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            UPDATE tf_event_subscriptions SET delivery_cursor=@after, attempts=0, available_at=@now, last_error=NULL, fence=fence+1, lease_until=NULL, generation=generation+1,
                suspended=false WHERE {SubscriptionWhere}
            """, connection, transaction))
        {
            AddScope(command, scope);
            AddText(command, "subscription", id.Value);
            command.Parameters.AddWithValue("after", afterSequence);
            AddTime(command, "now", EventText.Instant(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return false;
        }
        await using (var letters = new NpgsqlCommand("""
            DELETE FROM tf_event_dead_letters WHERE subscription_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@subscription) AND sequence > @after
            """, connection, transaction))
        {
            AddScope(letters, scope);
            AddText(letters, "subscription", id.Value);
            letters.Parameters.AddWithValue("after", afterSequence);
            await letters.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
        command.Parameters.AddWithValue("after", afterSequence);
        command.Parameters.AddWithValue("limit", limit);
        var letters = new List<EventDeadLetter>();
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
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> FencedAsync(TicketScope scope, EventSubscriptionId id, long fence, string set, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        await using var command = source.CreateCommand($"UPDATE tf_event_subscriptions SET {set} WHERE {SubscriptionWhere} AND fence=@fence AND lease_until > @now");
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        command.Parameters.AddWithValue("fence", fence);
        AddTime(command, "now", time.GetUtcNow());
        bind(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private sealed record SubscriptionRow(StoredEventSubscription Subscription, EventDeliveryState State, long Fence, DateTimeOffset? LeaseUntil);

    private async Task<SubscriptionRow?> GetRowAsync(TicketScope scope, EventSubscriptionId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id);
        await using var command = source.CreateCommand($"SELECT {SubscriptionColumns} FROM tf_event_subscriptions WHERE {SubscriptionWhere}");
        AddScope(command, scope);
        AddText(command, "subscription", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            var resource = new EventResource(Text(7) is { } conversation ? new ConversationId(conversation) : null, Text(8) is { } inbox ? new InboxId(inbox) : null,
                Text(9) is { } team ? new TeamId(team) : null, Text(10) is { } assignee ? new UserId(assignee) : null, Text(11) is { } contact ? new ContactId(contact) : null);
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
        command.Parameters.AddWithValue("privacy", (short)subscription.Privacy);
        command.Parameters.AddWithValue("enabled", subscription.Enabled);
        command.Parameters.AddWithValue("batch", subscription.BatchSize);
    }

    /// <summary>The persisted filter: <c>{"v":1,"types":[…],"inboxes":[…],"teams":[…]}</c>.</summary>
    internal static string EncodeFilter(EventSubscriptionFilter filter)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("types");
            foreach (var pattern in filter.Types)
                writer.WriteStringValue(pattern.Value);
            writer.WriteEndArray();
            writer.WriteStartArray("inboxes");
            foreach (var inbox in filter.Inboxes)
                writer.WriteStringValue(inbox.Value);
            writer.WriteEndArray();
            writer.WriteStartArray("teams");
            foreach (var team in filter.Teams)
                writer.WriteStringValue(team.Value);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static EventSubscriptionFilter DecodeFilter(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4 || root.GetProperty("v").GetInt32() != 1)
            throw new FormatException("Unsupported filter format.");
        static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString() ?? throw new FormatException("Expected a string."));
        return new EventSubscriptionFilter(Strings(root.GetProperty("types")).Select(value => new EventTypePattern(value)).ToList(),
            Strings(root.GetProperty("inboxes")).Select(value => new InboxId(value)).ToList(), Strings(root.GetProperty("teams")).Select(value => new TeamId(value)).ToList());
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
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string?> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.Select(value => (object?)value ?? DBNull.Value).ToArray() });

    private static void AddTime(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value is { } instant ? instant : DBNull.Value });
}
