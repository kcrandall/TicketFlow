using System.Data;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Realtime;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Channels.LiveChat.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="ILiveChatWidgetStore"/> and <see cref="ILiveChatSessionStore"/> over an
/// application-owned <see cref="NpgsqlDataSource"/>. Tables live in the data source's current schema and are created only by
/// <see cref="InitializeAsync"/>. Every statement is parameterized and confined to one full scope, except the partition-wide session
/// lookup and the store-wide expiry prune. Writes are compare-and-set on revision; widget capacity and link capacity run under
/// transaction-scoped advisory locks. Rows that fail to decode throw.
/// </summary>
public sealed class PostgresLiveChatStore : ILiveChatWidgetStore, ILiveChatSessionStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeWhereL = "l.tenant_id=@tenant AND l.project_id=@project AND l.environment_id=@environment AND l.app_id=@app AND l.workspace_id=@workspace";
    private const string ScopeWhereS = "s.tenant_id=@tenant AND s.project_id=@project AND s.environment_id=@environment AND s.app_id=@app AND s.workspace_id=@workspace";
    private const string ScopeArgs = "@tenant,@project,@environment,@app,@workspace";
    private const string PartitionWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app";
    private const string SessionColumns = "session_id, configuration_id, inbox_id, visitor_id, token_hash, previous_token_hash, previous_valid_until, created_at, token_issued_at, expires_at, "
        + "identity_namespace, identity_value, identity_method, identity_verified_at, display_name, prechat, revoked, revision, format_version, "
        + "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string LinkColumns = "session_id, conversation_id, linked_at, read_sequence, format_version";

    private const string CreateWidgetSql = $"""
        WITH existing AS (
            SELECT revision FROM tf_livechat_widgets
            WHERE widget_key=tf_key_v1({ScopeArgs},@configuration) AND configuration_id=@configuration AND {ScopeWhere}
        ),
        inserted AS (
            INSERT INTO tf_livechat_widgets (tenant_id, project_id, environment_id, app_id, workspace_id, configuration_id, format_version, inbox_id, config, revision)
            SELECT @tenant,@project,@environment,@app,@workspace,@configuration,1,@inbox,@config,1
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_livechat_widgets WHERE scope_key=tf_key_v1({ScopeArgs}) AND {ScopeWhere}) < @max
            RETURNING revision
        )
        SELECT (SELECT revision FROM inserted), (SELECT revision FROM existing)
        """;

    private const string LinkSessionSql = $"""
        WITH sess AS (
            SELECT 1 FROM tf_livechat_sessions
            WHERE session_key=tf_key_v1(@tenant,@project,@environment,@app,@session) AND session_id=@session AND {ScopeWhere}
        ),
        existing AS (
            SELECT {LinkColumns} FROM tf_livechat_links
            WHERE link_key=tf_key_v1({ScopeArgs},@session,@conversation) AND session_id=@session AND conversation_id=@conversation AND {ScopeWhere}
        ),
        inserted AS (
            INSERT INTO tf_livechat_links (tenant_id, project_id, environment_id, app_id, workspace_id, session_id, conversation_id, format_version, linked_at, read_sequence, session_ref)
            SELECT @tenant,@project,@environment,@app,@workspace,@session,@conversation,1,@linked,0,tf_key_v1(@tenant,@project,@environment,@app,@session)
            WHERE EXISTS (SELECT 1 FROM sess)
              AND NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_livechat_links WHERE session_scope_key=tf_key_v1({ScopeArgs},@session) AND session_id=@session AND {ScopeWhere}) < @max
            RETURNING {LinkColumns}
        )
        SELECT EXISTS (SELECT 1 FROM sess),
               COALESCE(e.session_id, i.session_id),
               COALESCE(e.conversation_id, i.conversation_id),
               COALESCE(e.linked_at, i.linked_at),
               COALESCE(e.read_sequence, i.read_sequence),
               COALESCE(e.format_version, i.format_version),
               (i.session_id IS NOT NULL)
        FROM (SELECT 1) AS dummy
        LEFT JOIN existing e ON true
        LEFT JOIN inserted i ON true
        """;

    private readonly NpgsqlDataSource source;

    public PostgresLiveChatStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Explicitly applies <see cref="PostgresLiveChatMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent). Never called implicitly.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresLiveChatMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    // ---- Widgets ----

    public async Task<StoredLiveChatWidget?> GetAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        await using var command = source.CreateCommand($"""
            SELECT configuration_id, inbox_id, config, revision, format_version FROM tf_livechat_widgets
            WHERE widget_key=tf_key_v1({ScopeArgs},@configuration) AND configuration_id=@configuration AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "configuration", configurationId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadWidget(reader, scope) : null;
    }

    public async Task<StoreWriteResult<StoredLiveChatWidget, LiveChatWriteRejection>> CreateAsync(TicketScope scope, LiveChatWidget widget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(widget);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.livechat.widgets", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value], cancellationToken).ConfigureAwait(false);
        long? insertedRevision;
        long? existingRevision;
        await using (var create = new NpgsqlCommand(CreateWidgetSql, connection, transaction))
        {
            AddScope(create, scope);
            AddWidget(create, widget);
            AddInt64(create, "max", ILiveChatWidgetStore.MaximumWidgetsPerScope);
            await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            insertedRevision = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(0);
            existingRevision = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(1);
        }
        if (insertedRevision is { } rev)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoreApplied<StoredLiveChatWidget>(new StoredLiveChatWidget(scope, widget, rev));
        }
        return existingRevision is { } existing
            ? new StoreConflict(existing)
            : new StoreRejected<LiveChatWriteRejection>(LiveChatWriteRejection.CapacityExceeded);
    }

    public async Task<StoreWriteResult<StoredLiveChatWidget, LiveChatWriteRejection>> UpdateAsync(TicketScope scope, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(widget);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE tf_livechat_widgets SET inbox_id=@inbox, config=@config, revision=revision+1
            WHERE widget_key=tf_key_v1({ScopeArgs},@configuration) AND configuration_id=@configuration AND {ScopeWhere} AND revision=@revision
            RETURNING revision
            """, connection);
        AddScope(command, scope);
        AddWidget(command, widget);
        AddInt64(command, "revision", expectedRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new StoreApplied<StoredLiveChatWidget>(new StoredLiveChatWidget(scope, widget, revision));
        var current = await LoadWidgetAsync(connection, null, scope, widget.ConfigurationId, cancellationToken).ConfigureAwait(false);
        return current is null ? StoreNotFound.Instance : new StoreConflict(current.Revision);
    }

    public async Task<StoreWriteResult<StoredLiveChatWidget, LiveChatWriteRejection>> DeleteAsync(TicketScope scope, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            DELETE FROM tf_livechat_widgets
            WHERE widget_key=tf_key_v1({ScopeArgs},@configuration) AND configuration_id=@configuration AND {ScopeWhere} AND revision=@revision
            RETURNING configuration_id, inbox_id, config, revision, format_version
            """, connection))
        {
            AddScope(command, scope);
            AddText(command, "configuration", configurationId);
            AddInt64(command, "revision", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredLiveChatWidget>(ReadWidget(reader, scope));
        }
        var now = await LoadWidgetAsync(connection, null, scope, configurationId, cancellationToken).ConfigureAwait(false);
        return now is null ? StoreNotFound.Instance : new StoreConflict(now.Revision);
    }

    private static async Task<StoredLiveChatWidget?> LoadWidgetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, string configurationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT configuration_id, inbox_id, config, revision, format_version FROM tf_livechat_widgets
            WHERE widget_key=tf_key_v1({ScopeArgs},@configuration) AND configuration_id=@configuration AND {ScopeWhere}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "configuration", configurationId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadWidget(reader, scope) : null;
    }

    private static StoredLiveChatWidget ReadWidget(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(4) != 1)
                throw new FormatException("Unsupported widget format version.");
            return new StoredLiveChatWidget(scope, LiveChatCodec.DecodeWidget(reader.GetString(0), new InboxId(reader.GetString(1)), reader.GetString(2)), reader.GetInt64(3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored live-chat widget is invalid or incompatible.", error);
        }
    }

    // ---- Sessions ----

    public async Task<StoreWriteResult<StoredVisitorSession, LiveChatWriteRejection>> CreateAsync(TicketScope scope, VisitorSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            INSERT INTO tf_livechat_sessions (tenant_id, project_id, environment_id, app_id, workspace_id, session_id, format_version, configuration_id, inbox_id, visitor_id,
                token_hash, previous_token_hash, previous_valid_until, created_at, token_issued_at, expires_at, identity_namespace, identity_value, identity_method,
                identity_verified_at, display_name, prechat, revoked, revision)
            VALUES (@tenant,@project,@environment,@app,@workspace,@session,1,@configuration,@inbox,@visitor,@token,@previous,@previous_until,@created,@issued,@expires,
                @identity_namespace,@identity_value,@identity_method,@identity_at,@display,@prechat,@revoked,1)
            ON CONFLICT (session_key) DO NOTHING
            """, connection);
        AddScope(command, scope);
        AddSession(command, session);
        AddInstant(command, "created", session.CreatedAt);
        AddText(command, "configuration", session.ConfigurationId);
        AddText(command, "inbox", session.Inbox.Value);
        AddText(command, "visitor", session.VisitorId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            return new StoreApplied<StoredVisitorSession>(new StoredVisitorSession(scope, session, 1));
        var existing = await LoadSessionAsync(connection, AccessPartition.From(scope), session.Id, cancellationToken).ConfigureAwait(false);
        return new StoreConflict(existing?.Revision);
    }

    public async Task<StoredVisitorSession?> GetAsync(AccessPartition partition, VisitorSessionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (!id.IsInitialized)
            throw new ArgumentException("A session identifier is required.", nameof(id));
        await using var command = source.CreateCommand($"""
            SELECT {SessionColumns} FROM tf_livechat_sessions
            WHERE session_key=tf_key_v1(@tenant,@project,@environment,@app,@session) AND session_id=@session AND {PartitionWhere}
            """);
        AddText(command, "tenant", partition.TenantId);
        AddText(command, "project", partition.ProjectId);
        AddText(command, "environment", partition.EnvironmentId);
        AddText(command, "app", partition.AppId);
        AddText(command, "session", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    public async Task<StoreWriteResult<StoredVisitorSession, LiveChatWriteRejection>> UpdateAsync(TicketScope scope, VisitorSession session, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            UPDATE tf_livechat_sessions SET token_hash=@token, previous_token_hash=@previous, previous_valid_until=@previous_until, token_issued_at=@issued,
                expires_at=@expires, identity_namespace=@identity_namespace, identity_value=@identity_value, identity_method=@identity_method,
                identity_verified_at=@identity_at, display_name=@display, prechat=@prechat, revoked=@revoked, revision=revision+1
            WHERE session_key=tf_key_v1(@tenant,@project,@environment,@app,@session) AND session_id=@session AND {ScopeWhere}
              AND configuration_id=@configuration AND inbox_id=@inbox AND visitor_id=@visitor AND created_at=@created AND revision=@revision
            RETURNING revision
            """, connection))
        {
            AddScope(command, scope);
            AddSession(command, session);
            AddText(command, "configuration", session.ConfigurationId);
            AddText(command, "inbox", session.Inbox.Value);
            AddText(command, "visitor", session.VisitorId);
            AddInstant(command, "created", session.CreatedAt);
            AddInt64(command, "revision", expectedRevision);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new StoreApplied<StoredVisitorSession>(new StoredVisitorSession(scope, session, revision));
        }
        var current = await LoadSessionAsync(connection, AccessPartition.From(scope), session.Id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Scope != scope)
            return StoreNotFound.Instance;
        var existing = current.Session;
        if (!string.Equals(existing.ConfigurationId, session.ConfigurationId, StringComparison.Ordinal) || existing.Inbox != session.Inbox || !string.Equals(existing.VisitorId, session.VisitorId, StringComparison.Ordinal) || existing.CreatedAt != session.CreatedAt)
            throw new ArgumentException("Session identity fields are immutable.", nameof(session));
        return new StoreConflict(current.Revision);
    }

    public async Task<StoreWriteResult<VisitorConversationLink, LiveChatWriteRejection>> LinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, DateTimeOffset linkedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        var normalizedLinkedAt = LiveChatText.Instant(linkedAt);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.livechat.links", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, session.Value], cancellationToken).ConfigureAwait(false);
        bool hasSession;
        VisitorConversationLink? resultLink = null;
        var wasInserted = false;
        await using (var command = new NpgsqlCommand(LinkSessionSql, connection, transaction))
        {
            AddScope(command, scope);
            AddText(command, "session", session.Value);
            AddText(command, "conversation", conversation.Value);
            AddInstant(command, "linked", normalizedLinkedAt);
            AddInt64(command, "max", ILiveChatSessionStore.MaximumLinksPerSession);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            hasSession = reader.GetBoolean(0);
            if (!await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt16(5) != 1)
                    throw new InvalidOperationException("A stored live-chat link is invalid or incompatible.");
                resultLink = new VisitorConversationLink(new VisitorSessionId(reader.GetString(1)), new ConversationId(reader.GetString(2)),
                    await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false), reader.GetInt64(4));
                wasInserted = reader.GetBoolean(6);
            }
        }
        if (!hasSession)
            return StoreNotFound.Instance;
        if (resultLink is null)
            return new StoreRejected<LiveChatWriteRejection>(LiveChatWriteRejection.CapacityExceeded);
        if (wasInserted)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<VisitorConversationLink>(resultLink);
    }

    public async Task<VisitorConversationLink?> GetLinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        await using var command = source.CreateCommand($"""
            SELECT {LinkColumns} FROM tf_livechat_links
            WHERE link_key=tf_key_v1({ScopeArgs},@session,@conversation) AND session_id=@session AND conversation_id=@conversation AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "session", session.Value);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLink(reader) : null;
    }

    public async Task<IReadOnlyList<VisitorConversationLink>> ListLinksAsync(TicketScope scope, VisitorSessionId session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!session.IsInitialized)
            throw new ArgumentException("A session identifier is required.", nameof(session));
        await using var command = source.CreateCommand($"""
            SELECT {LinkColumns} FROM tf_livechat_links
            WHERE session_scope_key=tf_key_v1({ScopeArgs},@session) AND session_id=@session AND {ScopeWhere}
            ORDER BY linked_at DESC, conversation_id DESC LIMIT {ILiveChatSessionStore.MaximumLinksPerSession}
            """);
        AddScope(command, scope);
        AddText(command, "session", session.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var links = new List<VisitorConversationLink>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            links.Add(ReadLink(reader));
        return links;
    }

    public async Task<IReadOnlyList<VisitorSessionId>> FindSessionsAsync(TicketScope scope, ConversationId conversation, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IVisitorSessionDirectory.MaximumSessions);
        await using var command = source.CreateCommand($"""
            SELECT session_id FROM tf_livechat_links
            WHERE conversation_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
            ORDER BY linked_at DESC, session_id LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInt32(command, "limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<VisitorSessionId>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(ReadSessionId(reader));
        return sessions;
    }

    public async Task<IReadOnlyList<VisitorSessionId>> FindLiveSessionsAsync(TicketScope scope, ConversationId conversation, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IVisitorSessionDirectory.MaximumSessions);
        await using var command = source.CreateCommand($"""
            SELECT l.session_id
            FROM tf_livechat_links l
            JOIN tf_livechat_sessions s ON s.session_key = l.session_ref AND s.session_id = l.session_id
            WHERE l.conversation_key=tf_key_v1({ScopeArgs},@conversation) AND l.conversation_id=@conversation
              AND {ScopeWhereL} AND {ScopeWhereS}
              AND NOT s.revoked AND s.expires_at > @now
            ORDER BY l.linked_at DESC, l.session_id LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInstant(command, "now", now);
        AddInt32(command, "limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<VisitorSessionId>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(ReadSessionId(reader));
        return sessions;
    }

    public async Task<StoreWriteResult<VisitorConversationLink, LiveChatWriteRejection>> AdvanceCursorAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, long readSequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        ArgumentOutOfRangeException.ThrowIfNegative(readSequence);
        await using var command = source.CreateCommand($"""
            UPDATE tf_livechat_links SET read_sequence=GREATEST(read_sequence, @sequence)
            WHERE link_key=tf_key_v1({ScopeArgs},@session,@conversation) AND session_id=@session AND conversation_id=@conversation AND {ScopeWhere}
            RETURNING {LinkColumns}
            """);
        AddScope(command, scope);
        AddText(command, "session", session.Value);
        AddText(command, "conversation", conversation.Value);
        AddInt64(command, "sequence", readSequence);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoreApplied<VisitorConversationLink>(ReadLink(reader))
            : StoreNotFound.Instance;
    }

    public async Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ILiveChatSessionStore.MaximumPrune);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_livechat_sessions WHERE session_key IN (
                SELECT session_key FROM tf_livechat_sessions WHERE expires_at < @cutoff ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            """);
        AddInstant(command, "cutoff", expiredBefore);
        AddInt32(command, "limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredVisitorSession?> LoadSessionAsync(NpgsqlConnection connection, AccessPartition partition, VisitorSessionId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {SessionColumns} FROM tf_livechat_sessions
            WHERE session_key=tf_key_v1(@tenant,@project,@environment,@app,@session) AND session_id=@session AND {PartitionWhere}
            """, connection);
        AddText(command, "tenant", partition.TenantId);
        AddText(command, "project", partition.ProjectId);
        AddText(command, "environment", partition.EnvironmentId);
        AddText(command, "app", partition.AppId);
        AddText(command, "session", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    private static StoredVisitorSession ReadSession(NpgsqlDataReader reader)
    {
        try
        {
            if (reader.GetInt16(18) != 1)
                throw new FormatException("Unsupported session format version.");
            VerifiedVisitorIdentity? identity = reader.IsDBNull(11) ? null
                : new VerifiedVisitorIdentity(reader.GetString(10), reader.GetString(11), Defined((VisitorVerificationMethod)reader.GetInt16(12)), reader.GetFieldValue<DateTimeOffset>(13));
            var session = new VisitorSession(new VisitorSessionId(reader.GetString(0)), reader.GetString(1), new InboxId(reader.GetString(2)), reader.GetString(3),
                reader.GetFieldValue<byte[]>(4),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(5) ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(reader.GetFieldValue<byte[]>(5)),
                reader.IsDBNull(6) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(6),
                identity, reader.IsDBNull(14) ? null : reader.GetString(14), LiveChatCodec.DecodePrechat(reader.GetString(15)), reader.GetBoolean(16));
            var scope = new TicketScope(reader.GetString(19), reader.GetString(20), reader.GetString(21), reader.GetString(22), new WorkspaceId(reader.GetString(23)));
            return new StoredVisitorSession(scope, session, reader.GetInt64(17));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored live-chat session is invalid or incompatible.", error);
        }
    }

    private static VisitorConversationLink ReadLink(NpgsqlDataReader reader)
    {
        try
        {
            if (reader.GetInt16(4) != 1)
                throw new FormatException("Unsupported link format version.");
            return new VisitorConversationLink(new VisitorSessionId(reader.GetString(0)), new ConversationId(reader.GetString(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt64(3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored live-chat link is invalid or incompatible.", error);
        }
    }

    private static VisitorSessionId ReadSessionId(NpgsqlDataReader reader)
    {
        try
        {
            return new VisitorSessionId(reader.GetString(0));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored live-chat link is invalid or incompatible.", error);
        }
    }

    // ---- Helpers ----

    private static void AddSession(NpgsqlCommand command, VisitorSession session)
    {
        AddText(command, "session", session.Id.Value);
        command.Parameters.Add(new NpgsqlParameter<byte[]>("token", NpgsqlDbType.Bytea) { TypedValue = session.TokenHash.ToArray() });
        command.Parameters.Add(session.PreviousTokenHash is { } previous
            ? new NpgsqlParameter<byte[]>("previous", NpgsqlDbType.Bytea) { TypedValue = previous.ToArray() }
            : new NpgsqlParameter("previous", NpgsqlDbType.Bytea) { Value = DBNull.Value });
        AddInstant(command, "previous_until", session.PreviousTokenValidUntil);
        AddInstant(command, "issued", session.TokenIssuedAt);
        AddInstant(command, "expires", session.ExpiresAt);
        AddText(command, "identity_namespace", session.Identity?.Namespace);
        AddText(command, "identity_value", session.Identity?.Value);
        command.Parameters.Add(session.Identity is { } identity
            ? new NpgsqlParameter<short>("identity_method", NpgsqlDbType.Smallint) { TypedValue = (short)identity.Method }
            : new NpgsqlParameter("identity_method", NpgsqlDbType.Smallint) { Value = DBNull.Value });
        AddInstant(command, "identity_at", session.Identity?.VerifiedAt);
        AddText(command, "display", session.DisplayName);
        AddText(command, "prechat", LiveChatCodec.EncodePrechat(session.Prechat));
        command.Parameters.Add(new NpgsqlParameter<bool>("revoked", NpgsqlDbType.Boolean) { TypedValue = session.Revoked });
    }

    private static void AddWidget(NpgsqlCommand command, LiveChatWidget widget)
    {
        AddText(command, "configuration", widget.ConfigurationId);
        AddText(command, "inbox", widget.Inbox.Value);
        AddText(command, "config", LiveChatCodec.EncodeWidget(widget));
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] parts, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]>("parts", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = parts });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new FormatException("Unknown enumeration value.");

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(value is null
            ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value }
            : new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(value is { } instant
            ? new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = instant }
            : new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });

    private static void AddInt32(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddInt64(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void RequireIds(VisitorSessionId session, ConversationId conversation)
    {
        if (!session.IsInitialized || conversation == default)
            throw new ArgumentException("Session and conversation identifiers are required.", !session.IsInitialized ? nameof(session) : nameof(conversation));
    }
}
