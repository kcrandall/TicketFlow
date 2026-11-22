using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IChannelEndpointStore"/>, <see cref="IChannelConversationStateStore"/>,
/// <see cref="IChannelThreadStore"/> and <see cref="IChannelDispatchStore"/> over an application-owned <see cref="NpgsqlDataSource"/>.
/// Tables live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is
/// parameterized and confined to one full scope, except the partition-wide endpoint address lookup. Writes are compare-and-set on
/// revision; endpoint capacity and reference pruning run under transaction-scoped advisory locks; address uniqueness is a partial
/// unique index. Rows that fail to decode throw.
/// </summary>
public sealed class PostgresChannelStore : IChannelEndpointStore, IChannelConversationStateStore, IChannelThreadStore, IChannelDispatchStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeArgs = "@tenant,@project,@environment,@app,@workspace";
    private const string EndpointColumns = "channel_kind, configuration_id, inbox_id, address, revision, format_version, tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string StateColumns = "conversation_id, inbox_id, channel_kind, recipient_kind, recipient_namespace, recipient_value, external_thread_id, subject, last_incoming_at, last_incoming_external_id, revision, format_version";
    private const string DispatchColumns = "conversation_id, message_id, inbox_id, channel_kind, state, attempts, updated_at, next_attempt_at, external_message_id, code, revision, format_version";

    private readonly NpgsqlDataSource source;

    public PostgresChannelStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Explicitly applies <see cref="PostgresChannelMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent). Never called implicitly.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresChannelMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    // ---- Endpoints ----

    public async Task<StoredChannelEndpoint?> GetAsync(TicketScope scope, ChannelKind kind, string configurationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadEndpointAsync(connection, null, scope, kind, configurationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredChannelEndpoint?> FindByAddressAsync(AccessPartition partition, ChannelKind kind, string address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(address);
        await using var command = source.CreateCommand($"""
            SELECT {EndpointColumns} FROM tf_channel_endpoints
            WHERE address_key=tf_key_v1(@tenant,@project,@environment,@app,@kind,@address) AND address=@address AND channel_kind=@kind
              AND tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app
            """);
        AddText(command, "tenant", partition.TenantId);
        AddText(command, "project", partition.ProjectId);
        AddText(command, "environment", partition.EnvironmentId);
        AddText(command, "app", partition.AppId);
        AddText(command, "kind", Kind(kind));
        AddText(command, "address", address);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEndpoint(reader, null) : null;
    }

    public async Task<ChannelWriteResult<StoredChannelEndpoint>> CreateAsync(TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(endpoint);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.channels.endpoints", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value], cancellationToken).ConfigureAwait(false);
        if (await LoadEndpointAsync(connection, transaction, scope, endpoint.Kind, endpoint.ConfigurationId, cancellationToken).ConfigureAwait(false) is { } existing)
            return new ChannelWriteConflict(existing.Revision);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_channel_endpoints WHERE scope_key=tf_key_v1({ScopeArgs}) AND {ScopeWhere}", connection, transaction))
        {
            AddScope(count, scope);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= IChannelEndpointStore.MaximumEndpointsPerScope)
                return new ChannelWriteRejected(ChannelWriteRejection.CapacityExceeded);
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tf_channel_endpoints (tenant_id, project_id, environment_id, app_id, workspace_id, channel_kind, configuration_id, format_version, inbox_id, address, revision)
            VALUES (@tenant,@project,@environment,@app,@workspace,@kind,@configuration,1,@inbox,@address,1)
            """, connection, transaction);
        AddScope(insert, scope);
        AddEndpoint(insert, endpoint);
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName == "tf_channel_endpoints_address")
        {
            return new ChannelWriteRejected(ChannelWriteRejection.AddressTaken);
        }
        return new ChannelWriteApplied<StoredChannelEndpoint>(new StoredChannelEndpoint(scope, endpoint, 1));
    }

    public async Task<ChannelWriteResult<StoredChannelEndpoint>> UpdateAsync(TicketScope scope, ChannelEndpoint endpoint, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(endpoint);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            UPDATE tf_channel_endpoints SET inbox_id=@inbox, address=@address, revision=revision+1
            WHERE endpoint_key=tf_key_v1({ScopeArgs},@kind,@configuration) AND channel_kind=@kind AND configuration_id=@configuration AND {ScopeWhere}
              AND revision=@revision
            RETURNING revision
            """, connection);
        AddScope(command, scope);
        AddEndpoint(command, endpoint);
        command.Parameters.AddWithValue("revision", expectedRevision);
        try
        {
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new ChannelWriteApplied<StoredChannelEndpoint>(new StoredChannelEndpoint(scope, endpoint, revision));
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName == "tf_channel_endpoints_address")
        {
            return new ChannelWriteRejected(ChannelWriteRejection.AddressTaken);
        }
        var current = await LoadEndpointAsync(connection, null, scope, endpoint.Kind, endpoint.ConfigurationId, cancellationToken).ConfigureAwait(false);
        return current is null ? ChannelWriteNotFound.Instance : new ChannelWriteConflict(current.Revision);
    }

    public async Task<ChannelWriteResult<StoredChannelEndpoint>> DeleteAsync(TicketScope scope, ChannelKind kind, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            DELETE FROM tf_channel_endpoints
            WHERE endpoint_key=tf_key_v1({ScopeArgs},@kind,@configuration) AND channel_kind=@kind AND configuration_id=@configuration AND {ScopeWhere}
              AND revision=@revision
            RETURNING {EndpointColumns}
            """, connection);
        AddScope(command, scope);
        AddText(command, "kind", Kind(kind));
        AddText(command, "configuration", configurationId);
        command.Parameters.AddWithValue("revision", expectedRevision);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new ChannelWriteApplied<StoredChannelEndpoint>(ReadEndpoint(reader, scope));
        var current = await LoadEndpointAsync(connection, null, scope, kind, configurationId, cancellationToken).ConfigureAwait(false);
        return current is null ? ChannelWriteNotFound.Instance : new ChannelWriteConflict(current.Revision);
    }

    private static async Task<StoredChannelEndpoint?> LoadEndpointAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ChannelKind kind,
        string configurationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {EndpointColumns} FROM tf_channel_endpoints
            WHERE endpoint_key=tf_key_v1({ScopeArgs},@kind,@configuration) AND channel_kind=@kind AND configuration_id=@configuration AND {ScopeWhere}
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "kind", Kind(kind));
        AddText(command, "configuration", configurationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEndpoint(reader, scope) : null;
    }

    private static StoredChannelEndpoint ReadEndpoint(NpgsqlDataReader reader, TicketScope? scope) => Decode(() =>
    {
        if (reader.GetInt16(5) != 1)
            throw new FormatException("Unsupported endpoint format version.");
        var stored = scope ?? new TicketScope(reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), new WorkspaceId(reader.GetString(10)));
        return new StoredChannelEndpoint(stored, new ChannelEndpoint(new ChannelKind(reader.GetString(0)), reader.GetString(1), new InboxId(reader.GetString(2)),
            NullableText(reader, 3)), reader.GetInt64(4));
    }, "endpoint");

    private static void AddEndpoint(NpgsqlCommand command, ChannelEndpoint endpoint)
    {
        AddText(command, "kind", endpoint.Kind.Value);
        AddText(command, "configuration", endpoint.ConfigurationId);
        AddText(command, "inbox", endpoint.Inbox.Value);
        AddText(command, "address", endpoint.Address);
    }

    // ---- Conversation state ----

    public async Task<StoredChannelConversationState?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadStateAsync(connection, scope, conversation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelWriteResult<StoredChannelConversationState>> WriteAsync(TicketScope scope, ChannelConversationState state, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = expectedRevision is null
            ? """
              INSERT INTO tf_channel_conversation_states (tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, format_version, inbox_id, channel_kind,
                  recipient_kind, recipient_namespace, recipient_value, external_thread_id, subject, last_incoming_at, last_incoming_external_id, revision)
              VALUES (@tenant,@project,@environment,@app,@workspace,@conversation,1,@inbox,@kind,@recipientKind,@recipientNamespace,@recipientValue,@thread,@subject,@lastAt,@lastId,1)
              ON CONFLICT (state_key) DO NOTHING
              RETURNING revision
              """
            : $"""
              UPDATE tf_channel_conversation_states SET recipient_kind=@recipientKind, recipient_namespace=@recipientNamespace, recipient_value=@recipientValue,
                  external_thread_id=@thread, subject=@subject, last_incoming_at=@lastAt, last_incoming_external_id=@lastId, revision=revision+1
              WHERE state_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
                AND revision=@revision AND inbox_id=@inbox AND channel_kind=@kind
              RETURNING revision
              """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddScope(command, scope);
        AddText(command, "conversation", state.Conversation.Value);
        AddText(command, "inbox", state.Inbox.Value);
        AddText(command, "kind", state.Channel.Value);
        command.Parameters.AddWithValue("recipientKind", (short)state.Recipient.Kind);
        AddText(command, "recipientNamespace", state.Recipient.Namespace);
        AddText(command, "recipientValue", state.Recipient.Value);
        AddText(command, "thread", state.ExternalThreadId);
        AddText(command, "subject", state.Subject);
        AddInstant(command, "lastAt", state.LastIncomingAt);
        AddText(command, "lastId", state.LastIncomingExternalId);
        command.Parameters.AddWithValue("revision", expectedRevision ?? 0);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new ChannelWriteApplied<StoredChannelConversationState>(new StoredChannelConversationState(scope, state, revision));
        var current = await LoadStateAsync(connection, scope, state.Conversation, cancellationToken).ConfigureAwait(false);
        return new ChannelWriteConflict(current?.Revision);
    }

    async Task<bool> IChannelConversationStateStore.DeleteAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        await using var command = source.CreateCommand($"DELETE FROM tf_channel_conversation_states WHERE state_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}");
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static async Task<StoredChannelConversationState?> LoadStateAsync(NpgsqlConnection connection, TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {StateColumns} FROM tf_channel_conversation_states
            WHERE state_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
            """, connection);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(11) != 1)
                throw new FormatException("Unsupported state format version.");
            var recipient = new ChannelRecipient(Defined((ContactIdentityKind)reader.GetInt16(3)), reader.GetString(5), NullableText(reader, 4));
            return new StoredChannelConversationState(scope, new ChannelConversationState(new ConversationId(reader.GetString(0)), new InboxId(reader.GetString(1)),
                new ChannelKind(reader.GetString(2)), recipient, NullableText(reader, 6), NullableText(reader, 7), NullableInstant(reader, 8), NullableText(reader, 9)),
                reader.GetInt64(10));
        }, "conversation state");
    }

    // ---- References ----

    public async Task<int> AddAsync(TicketScope scope, InboxId inbox, ConversationId conversation, IReadOnlyList<ChannelMessageReference> references, DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > IChannelThreadStore.MaximumReferencesPerCall || references.Any(reference => reference is null))
            throw new ArgumentException($"At most {IChannelThreadStore.MaximumReferencesPerCall} references per call.", nameof(references));
        if (references.Count == 0)
            return 0;
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.channels.references", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value, conversation.Value],
            cancellationToken).ConfigureAwait(false);
        var added = 0;
        foreach (var reference in references)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO tf_channel_references (tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id, conversation_id, reference, format_version,
                    direction, message_id, recorded_at)
                VALUES (@tenant,@project,@environment,@app,@workspace,@inbox,@conversation,@reference,1,@direction,@message,@at)
                ON CONFLICT (reference_key) DO NOTHING
                """, connection, transaction);
            AddScope(insert, scope);
            AddText(insert, "inbox", inbox.Value);
            AddText(insert, "conversation", conversation.Value);
            AddText(insert, "reference", reference.Reference);
            insert.Parameters.AddWithValue("direction", (short)reference.Direction);
            AddText(insert, "message", reference.Message?.Value);
            AddInstant(insert, "at", ChannelConversationState.Microseconds(recordedAt));
            added += await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var prune = new NpgsqlCommand($"""
            DELETE FROM tf_channel_references WHERE conversation_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
              AND sequence < (SELECT sequence FROM tf_channel_references WHERE conversation_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation
                  AND {ScopeWhere} ORDER BY sequence DESC OFFSET @keep LIMIT 1)
            """, connection, transaction))
        {
            AddScope(prune, scope);
            AddText(prune, "conversation", conversation.Value);
            prune.Parameters.AddWithValue("keep", IChannelThreadStore.MaximumReferencesPerConversation - 1);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    public async Task<ConversationId?> FindConversationAsync(TicketScope scope, InboxId inbox, IReadOnlyList<string> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > IChannelThreadStore.MaximumReferencesPerCall)
            throw new ArgumentException($"At most {IChannelThreadStore.MaximumReferencesPerCall} references per call.", nameof(references));
        var candidates = references.Where(reference => reference is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
            return null;
        await using var command = source.CreateCommand($"""
            SELECT reference, conversation_id FROM tf_channel_references
            WHERE reference_key IN (SELECT tf_key_v1({ScopeArgs},@inbox,candidate) FROM unnest(@references) AS candidate)
              AND reference = ANY(@references) AND inbox_id=@inbox AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "inbox", inbox.Value);
        command.Parameters.Add(new NpgsqlParameter("references", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = candidates });
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                found[reader.GetString(0)] = reader.GetString(1);
        foreach (var candidate in candidates)
            if (found.TryGetValue(candidate, out var conversation))
                return new ConversationId(conversation);
        return null;
    }

    public async Task<IReadOnlyList<StoredChannelMessageReference>> ListAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        await using var command = source.CreateCommand($"""
            SELECT inbox_id, reference, direction, message_id, sequence, recorded_at, format_version FROM tf_channel_references
            WHERE conversation_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
            ORDER BY sequence
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        var references = new List<StoredChannelMessageReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            references.Add(Decode(() =>
            {
                if (reader.GetInt16(6) != 1)
                    throw new FormatException("Unsupported reference format version.");
                var message = NullableText(reader, 3);
                return new StoredChannelMessageReference(conversation, new InboxId(reader.GetString(0)), new ChannelMessageReference(reader.GetString(1),
                    Defined((MessageDirection)reader.GetInt16(2)), message is null ? null : new MessageId(message)), reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(5));
            }, "reference"));
        return references;
    }

    // ---- Dispatches ----

    public async Task<StoredChannelDispatch?> GetAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadDispatchAsync(connection, scope, conversation, message, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelWriteResult<StoredChannelDispatch>> WriteAsync(TicketScope scope, ChannelDispatch dispatch, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(dispatch);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = expectedRevision is null
            ? """
              INSERT INTO tf_channel_dispatches (tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, message_id, format_version, inbox_id, channel_kind,
                  state, attempts, updated_at, next_attempt_at, external_message_id, code, revision)
              VALUES (@tenant,@project,@environment,@app,@workspace,@conversation,@message,1,@inbox,@kind,@state,@attempts,@updated,@next,@external,@code,1)
              ON CONFLICT (dispatch_key) DO NOTHING
              RETURNING revision
              """
            : $"""
              UPDATE tf_channel_dispatches SET state=@state, attempts=@attempts, updated_at=@updated, next_attempt_at=@next, external_message_id=@external, code=@code,
                  revision=revision+1
              WHERE dispatch_key=tf_key_v1({ScopeArgs},@conversation,@message) AND conversation_id=@conversation AND message_id=@message AND {ScopeWhere}
                AND revision=@revision AND inbox_id=@inbox AND channel_kind=@kind
              RETURNING revision
              """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddScope(command, scope);
        AddText(command, "conversation", dispatch.Conversation.Value);
        AddText(command, "message", dispatch.Message.Value);
        AddText(command, "inbox", dispatch.Inbox.Value);
        AddText(command, "kind", dispatch.Channel.Value);
        command.Parameters.AddWithValue("state", (short)dispatch.State);
        command.Parameters.AddWithValue("attempts", dispatch.Attempts);
        AddInstant(command, "updated", dispatch.UpdatedAt);
        AddInstant(command, "next", dispatch.NextAttemptAt);
        AddText(command, "external", dispatch.ExternalMessageId);
        AddText(command, "code", dispatch.Code);
        command.Parameters.AddWithValue("revision", expectedRevision ?? 0);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new ChannelWriteApplied<StoredChannelDispatch>(new StoredChannelDispatch(scope, dispatch, revision));
        var current = await LoadDispatchAsync(connection, scope, dispatch.Conversation, dispatch.Message, cancellationToken).ConfigureAwait(false);
        return new ChannelWriteConflict(current?.Revision);
    }

    private static async Task<StoredChannelDispatch?> LoadDispatchAsync(NpgsqlConnection connection, TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken)
    {
        if (message == default)
            throw new ArgumentException("A message identifier is required.", nameof(message));
        await using var command = new NpgsqlCommand($"""
            SELECT {DispatchColumns} FROM tf_channel_dispatches
            WHERE dispatch_key=tf_key_v1({ScopeArgs},@conversation,@message) AND conversation_id=@conversation AND message_id=@message AND {ScopeWhere}
            """, connection);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "message", message.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(11) != 1)
                throw new FormatException("Unsupported dispatch format version.");
            return new StoredChannelDispatch(scope, new ChannelDispatch(new ConversationId(reader.GetString(0)), new MessageId(reader.GetString(1)), new InboxId(reader.GetString(2)),
                new ChannelKind(reader.GetString(3)), Defined((ChannelDispatchState)reader.GetInt16(4)), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6),
                NullableInstant(reader, 7), NullableText(reader, 8), NullableText(reader, 9)), reader.GetInt64(10));
        }, "dispatch");
    }

    // ---- Helpers ----

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] parts, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = parts });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Kind(ChannelKind kind) => kind.IsInitialized ? kind.Value : throw new ArgumentException("A channel kind is required.", nameof(kind));

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
            throw new InvalidOperationException($"A stored channel {description} is invalid or incompatible.", error);
        }
    }
}
