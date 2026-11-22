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
    private const string ScopeArgs = "@tenant,@project,@environment,@app,@workspace";
    private const string PartitionWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app";
    private const string SessionColumns = "session_id, configuration_id, inbox_id, visitor_id, token_hash, previous_token_hash, previous_valid_until, created_at, token_issued_at, expires_at, "
        + "identity_namespace, identity_value, identity_method, identity_verified_at, display_name, prechat, revoked, revision, format_version, "
        + "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string LinkColumns = "session_id, conversation_id, linked_at, read_sequence, format_version";

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
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadWidgetAsync(connection, null, scope, configurationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LiveChatWriteResult<StoredLiveChatWidget>> CreateAsync(TicketScope scope, LiveChatWidget widget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(widget);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.livechat.widgets", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value], cancellationToken).ConfigureAwait(false);
        if (await LoadWidgetAsync(connection, transaction, scope, widget.ConfigurationId, cancellationToken).ConfigureAwait(false) is { } existing)
            return new LiveChatWriteConflict(existing.Revision);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_livechat_widgets WHERE scope_key=tf_key_v1({ScopeArgs}) AND {ScopeWhere}", connection, transaction))
        {
            AddScope(count, scope);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= ILiveChatWidgetStore.MaximumWidgetsPerScope)
                return new LiveChatWriteRejected(LiveChatWriteRejection.CapacityExceeded);
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tf_livechat_widgets (tenant_id, project_id, environment_id, app_id, workspace_id, configuration_id, format_version, inbox_id, config, revision)
            VALUES (@tenant,@project,@environment,@app,@workspace,@configuration,1,@inbox,@config,1)
            """, connection, transaction);
        AddScope(insert, scope);
        AddWidget(insert, widget);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LiveChatWriteApplied<StoredLiveChatWidget>(new StoredLiveChatWidget(scope, widget, 1));
    }

    public async Task<LiveChatWriteResult<StoredLiveChatWidget>> UpdateAsync(TicketScope scope, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default)
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
        command.Parameters.AddWithValue("revision", expectedRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new LiveChatWriteApplied<StoredLiveChatWidget>(new StoredLiveChatWidget(scope, widget, revision));
        var current = await LoadWidgetAsync(connection, null, scope, widget.ConfigurationId, cancellationToken).ConfigureAwait(false);
        return current is null ? LiveChatWriteNotFound.Instance : new LiveChatWriteConflict(current.Revision);
    }

    public async Task<LiveChatWriteResult<StoredLiveChatWidget>> DeleteAsync(TicketScope scope, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var current = await LoadWidgetAsync(connection, null, scope, configurationId, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return LiveChatWriteNotFound.Instance;
        await using var command = new NpgsqlCommand($"""
            DELETE FROM tf_livechat_widgets
            WHERE widget_key=tf_key_v1({ScopeArgs},@configuration) AND configuration_id=@configuration AND {ScopeWhere} AND revision=@revision
            """, connection);
        AddScope(command, scope);
        AddText(command, "configuration", configurationId);
        command.Parameters.AddWithValue("revision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 && current.Revision == expectedRevision)
            return new LiveChatWriteApplied<StoredLiveChatWidget>(current);
        var now = await LoadWidgetAsync(connection, null, scope, configurationId, cancellationToken).ConfigureAwait(false);
        return now is null ? LiveChatWriteNotFound.Instance : new LiveChatWriteConflict(now.Revision);
    }

    private static async Task<StoredLiveChatWidget?> LoadWidgetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, string configurationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT configuration_id, inbox_id, config, revision, format_version FROM tf_livechat_widgets
            WHERE widget_key=tf_key_v1({ScopeArgs},@configuration) AND configuration_id=@configuration AND {ScopeWhere}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "configuration", configurationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(4) != 1)
                throw new FormatException("Unsupported widget format version.");
            return new StoredLiveChatWidget(scope, LiveChatCodec.DecodeWidget(reader.GetString(0), new InboxId(reader.GetString(1)), reader.GetString(2)), reader.GetInt64(3));
        }, "widget");
    }

    // ---- Sessions ----

    public async Task<LiveChatWriteResult<StoredVisitorSession>> CreateAsync(TicketScope scope, VisitorSession session, CancellationToken cancellationToken = default)
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
            return new LiveChatWriteApplied<StoredVisitorSession>(new StoredVisitorSession(scope, session, 1));
        var existing = await LoadSessionAsync(connection, AccessPartition.From(scope), session.Id, cancellationToken).ConfigureAwait(false);
        return new LiveChatWriteConflict(existing?.Revision);
    }

    public async Task<StoredVisitorSession?> GetAsync(AccessPartition partition, VisitorSessionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (!id.IsInitialized)
            throw new ArgumentException("A session identifier is required.", nameof(id));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadSessionAsync(connection, partition, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LiveChatWriteResult<StoredVisitorSession>> UpdateAsync(TicketScope scope, VisitorSession session, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var current = await LoadSessionAsync(connection, AccessPartition.From(scope), session.Id, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Scope != scope)
            return LiveChatWriteNotFound.Instance;
        var existing = current.Session;
        if (existing.ConfigurationId != session.ConfigurationId || existing.Inbox != session.Inbox || existing.VisitorId != session.VisitorId || existing.CreatedAt != session.CreatedAt)
            throw new ArgumentException("Session identity fields are immutable.", nameof(session));
        await using var command = new NpgsqlCommand($"""
            UPDATE tf_livechat_sessions SET token_hash=@token, previous_token_hash=@previous, previous_valid_until=@previous_until, token_issued_at=@issued,
                expires_at=@expires, identity_namespace=@identity_namespace, identity_value=@identity_value, identity_method=@identity_method,
                identity_verified_at=@identity_at, display_name=@display, prechat=@prechat, revoked=@revoked, revision=revision+1
            WHERE session_key=tf_key_v1(@tenant,@project,@environment,@app,@session) AND session_id=@session AND {ScopeWhere} AND revision=@revision
            RETURNING revision
            """, connection);
        AddScope(command, scope);
        AddSession(command, session);
        command.Parameters.AddWithValue("revision", expectedRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new LiveChatWriteApplied<StoredVisitorSession>(new StoredVisitorSession(scope, session, revision));
        var now = await LoadSessionAsync(connection, AccessPartition.From(scope), session.Id, cancellationToken).ConfigureAwait(false);
        return now is null || now.Scope != scope ? LiveChatWriteNotFound.Instance : new LiveChatWriteConflict(now.Revision);
    }

    public async Task<LiveChatWriteResult<VisitorConversationLink>> LinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, DateTimeOffset linkedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.livechat.links", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, session.Value], cancellationToken).ConfigureAwait(false);
        await using (var exists = new NpgsqlCommand($"""
            SELECT 1 FROM tf_livechat_sessions WHERE session_key=tf_key_v1(@tenant,@project,@environment,@app,@session) AND session_id=@session AND {ScopeWhere}
            """, connection, transaction))
        {
            AddScope(exists, scope);
            AddText(exists, "session", session.Value);
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                return LiveChatWriteNotFound.Instance;
        }
        if (await LoadLinkAsync(connection, transaction, scope, session, conversation, cancellationToken).ConfigureAwait(false) is { } existing)
            return new LiveChatWriteApplied<VisitorConversationLink>(existing);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_livechat_links WHERE session_scope_key=tf_key_v1({ScopeArgs},@session) AND session_id=@session AND {ScopeWhere}",
            connection, transaction))
        {
            AddScope(count, scope);
            AddText(count, "session", session.Value);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= ILiveChatSessionStore.MaximumLinksPerSession)
                return new LiveChatWriteRejected(LiveChatWriteRejection.CapacityExceeded);
        }
        var link = new VisitorConversationLink(session, conversation, LiveChatText.Instant(linkedAt), 0);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tf_livechat_links (tenant_id, project_id, environment_id, app_id, workspace_id, session_id, conversation_id, format_version, linked_at, read_sequence, session_ref)
            VALUES (@tenant,@project,@environment,@app,@workspace,@session,@conversation,1,@linked,0,tf_key_v1(@tenant,@project,@environment,@app,@session))
            """, connection, transaction);
        AddScope(insert, scope);
        AddText(insert, "session", session.Value);
        AddText(insert, "conversation", conversation.Value);
        AddInstant(insert, "linked", link.LinkedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LiveChatWriteApplied<VisitorConversationLink>(link);
    }

    public async Task<VisitorConversationLink?> GetLinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadLinkAsync(connection, null, scope, session, conversation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VisitorConversationLink>> ListLinksAsync(TicketScope scope, VisitorSessionId session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!session.IsInitialized)
            throw new ArgumentException("A session identifier is required.", nameof(session));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT {LinkColumns} FROM tf_livechat_links
            WHERE session_scope_key=tf_key_v1({ScopeArgs},@session) AND session_id=@session AND {ScopeWhere}
            ORDER BY linked_at DESC, conversation_id DESC LIMIT {ILiveChatSessionStore.MaximumLinksPerSession}
            """, connection);
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
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT session_id FROM tf_livechat_links
            WHERE conversation_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
            ORDER BY linked_at DESC, session_id LIMIT @limit
            """, connection);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var sessions = new List<VisitorSessionId>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            sessions.Add(Decode(() => new VisitorSessionId(reader.GetString(0)), "link"));
        return sessions;
    }

    public async Task<LiveChatWriteResult<VisitorConversationLink>> AdvanceCursorAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, long readSequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        ArgumentOutOfRangeException.ThrowIfNegative(readSequence);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE tf_livechat_links SET read_sequence=GREATEST(read_sequence, @sequence)
            WHERE link_key=tf_key_v1({ScopeArgs},@session,@conversation) AND session_id=@session AND conversation_id=@conversation AND {ScopeWhere}
            RETURNING {LinkColumns}
            """, connection);
        AddScope(command, scope);
        AddText(command, "session", session.Value);
        AddText(command, "conversation", conversation.Value);
        command.Parameters.AddWithValue("sequence", readSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new LiveChatWriteApplied<VisitorConversationLink>(ReadLink(reader))
            : LiveChatWriteNotFound.Instance;
    }

    public async Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ILiveChatSessionStore.MaximumPrune);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            DELETE FROM tf_livechat_sessions WHERE session_key IN (
                SELECT session_key FROM tf_livechat_sessions WHERE expires_at < @cutoff ORDER BY expires_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            """, connection);
        AddInstant(command, "cutoff", expiredBefore);
        command.Parameters.AddWithValue("limit", limit);
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
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
        }, "session");
    }

    private static async Task<VisitorConversationLink?> LoadLinkAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, VisitorSessionId session,
        ConversationId conversation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {LinkColumns} FROM tf_livechat_links
            WHERE link_key=tf_key_v1({ScopeArgs},@session,@conversation) AND session_id=@session AND conversation_id=@conversation AND {ScopeWhere}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "session", session.Value);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLink(reader) : null;
    }

    private static VisitorConversationLink ReadLink(NpgsqlDataReader reader) => Decode(() =>
    {
        if (reader.GetInt16(4) != 1)
            throw new FormatException("Unsupported link format version.");
        return new VisitorConversationLink(new VisitorSessionId(reader.GetString(0)), new ConversationId(reader.GetString(1)), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt64(3));
    }, "link");

    // ---- Helpers ----

    private static void AddSession(NpgsqlCommand command, VisitorSession session)
    {
        AddText(command, "session", session.Id.Value);
        command.Parameters.Add(new NpgsqlParameter("token", NpgsqlDbType.Bytea) { Value = session.TokenHash.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("previous", NpgsqlDbType.Bytea) { Value = session.PreviousTokenHash is { } previous ? previous.ToArray() : DBNull.Value });
        AddInstant(command, "previous_until", session.PreviousTokenValidUntil);
        AddInstant(command, "issued", session.TokenIssuedAt);
        AddInstant(command, "expires", session.ExpiresAt);
        AddText(command, "identity_namespace", session.Identity?.Namespace);
        AddText(command, "identity_value", session.Identity?.Value);
        command.Parameters.Add(new NpgsqlParameter("identity_method", NpgsqlDbType.Smallint) { Value = session.Identity is { } identity ? (short)identity.Method : DBNull.Value });
        AddInstant(command, "identity_at", session.Identity?.VerifiedAt);
        AddText(command, "display", session.DisplayName);
        AddText(command, "prechat", LiveChatCodec.EncodePrechat(session.Prechat));
        command.Parameters.Add(new NpgsqlParameter("revoked", NpgsqlDbType.Boolean) { Value = session.Revoked });
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
        command.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = parts });
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
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = (object?)value ?? DBNull.Value });

    private static void RequireIds(VisitorSessionId session, ConversationId conversation)
    {
        if (!session.IsInitialized || conversation == default)
            throw new ArgumentException("Session and conversation identifiers are required.");
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException($"A stored live-chat {description} is invalid or incompatible.", error);
        }
    }
}
