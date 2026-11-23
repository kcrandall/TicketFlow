using System.Data;
using System.Text;
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
    private const string StateColumns = "conversation_id, inbox_id, channel_kind, recipient_kind, recipient_namespace, recipient_value, external_thread_id, subject, last_incoming_at, last_incoming_external_id, revision, format_version, contact_id";
    private const string DispatchColumns = "conversation_id, message_id, inbox_id, channel_kind, state, attempts, updated_at, next_attempt_at, external_message_id, code, revision, format_version";

    private const string CreateEndpointSql = $"""
        WITH existing AS (
            SELECT revision FROM tf_channel_endpoints
            WHERE endpoint_key=tf_key_v1({ScopeArgs},@kind,@configuration) AND channel_kind=@kind AND configuration_id=@configuration AND {ScopeWhere}
        ),
        inserted AS (
            INSERT INTO tf_channel_endpoints (tenant_id, project_id, environment_id, app_id, workspace_id, channel_kind, configuration_id, format_version, inbox_id, address, revision)
            SELECT @tenant,@project,@environment,@app,@workspace,@kind,@configuration,1,@inbox,@address,1
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_channel_endpoints WHERE scope_key=tf_key_v1({ScopeArgs}) AND {ScopeWhere}) < @max
            RETURNING revision
        )
        SELECT (SELECT revision FROM inserted), (SELECT revision FROM existing)
        """;

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
        await using var command = source.CreateCommand($"""
            SELECT {EndpointColumns} FROM tf_channel_endpoints
            WHERE endpoint_key=tf_key_v1({ScopeArgs},@kind,@configuration) AND channel_kind=@kind AND configuration_id=@configuration AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "kind", Kind(kind));
        AddText(command, "configuration", configurationId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEndpoint(reader, scope) : null;
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
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEndpoint(reader, null) : null;
    }

    public async Task<IReadOnlyList<StoredChannelEndpoint>> ListByAddressPrefixAsync(AccessPartition partition, ChannelKind kind, string prefix, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IChannelEndpointStore.MaximumPrefixMatches);
        string sql;
        string? upper = null;
        if (prefix.Length == 0)
        {
            sql = $"""
                SELECT {EndpointColumns} FROM tf_channel_endpoints
                WHERE tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND channel_kind=@kind
                  AND address IS NOT NULL
                ORDER BY address LIMIT @limit
                """;
        }
        else if ((upper = PrefixUpperBound(prefix)) is not null)
        {
            sql = $"""
                SELECT {EndpointColumns} FROM tf_channel_endpoints
                WHERE tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND channel_kind=@kind
                  AND address IS NOT NULL AND address >= @prefix AND address < @upper
                ORDER BY address LIMIT @limit
                """;
        }
        else
        {
            sql = $"""
                SELECT {EndpointColumns} FROM tf_channel_endpoints
                WHERE tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND channel_kind=@kind
                  AND address IS NOT NULL AND address >= @prefix AND starts_with(address, @prefix)
                ORDER BY address LIMIT @limit
                """;
        }
        await using var command = source.CreateCommand(sql);
        AddText(command, "tenant", partition.TenantId);
        AddText(command, "project", partition.ProjectId);
        AddText(command, "environment", partition.EnvironmentId);
        AddText(command, "app", partition.AppId);
        AddText(command, "kind", Kind(kind));
        if (prefix.Length > 0)
            AddText(command, "prefix", prefix);
        if (upper is not null)
            AddText(command, "upper", upper);
        AddInt32(command, "limit", limit);
        var endpoints = new List<StoredChannelEndpoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            endpoints.Add(ReadEndpoint(reader, null));
        return endpoints;
    }

    public async Task<ChannelWriteResult<StoredChannelEndpoint>> CreateAsync(TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(endpoint);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.channels.endpoints", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value], cancellationToken).ConfigureAwait(false);
        long? insertedRevision;
        long? existingRevision;
        await using (var create = new NpgsqlCommand(CreateEndpointSql, connection, transaction))
        {
            AddScope(create, scope);
            AddEndpoint(create, endpoint);
            AddInt64(create, "max", IChannelEndpointStore.MaximumEndpointsPerScope);
            try
            {
                await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                insertedRevision = reader.IsDBNull(0) ? null : reader.GetInt64(0);
                existingRevision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName == "tf_channel_endpoints_address")
            {
                return new ChannelWriteRejected(ChannelWriteRejection.AddressTaken);
            }
        }
        if (insertedRevision is { } rev)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ChannelWriteApplied<StoredChannelEndpoint>(new StoredChannelEndpoint(scope, endpoint, rev));
        }
        return existingRevision is { } existing
            ? new ChannelWriteConflict(existing)
            : new ChannelWriteRejected(ChannelWriteRejection.CapacityExceeded);
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
        AddInt64(command, "revision", expectedRevision);
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
        AddInt64(command, "revision", expectedRevision);
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false))
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
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEndpoint(reader, scope) : null;
    }

    private static StoredChannelEndpoint ReadEndpoint(NpgsqlDataReader reader, TicketScope? scope)
    {
        try
        {
            if (reader.GetInt16(5) != 1)
                throw new FormatException("Unsupported endpoint format version.");
            var stored = scope ?? new TicketScope(reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), new WorkspaceId(reader.GetString(10)));
            return new StoredChannelEndpoint(stored, new ChannelEndpoint(new ChannelKind(reader.GetString(0)), reader.GetString(1), new InboxId(reader.GetString(2)),
                NullableText(reader, 3)), reader.GetInt64(4));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored channel endpoint is invalid or incompatible.", error);
        }
    }

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
        await using var command = source.CreateCommand($"""
            SELECT {StateColumns} FROM tf_channel_conversation_states
            WHERE state_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadState(reader, scope) : null;
    }

    public async Task<ChannelWriteResult<StoredChannelConversationState>> WriteAsync(TicketScope scope, ChannelConversationState state, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = expectedRevision is null
            ? """
              INSERT INTO tf_channel_conversation_states (tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, format_version, inbox_id, channel_kind,
                  recipient_kind, recipient_namespace, recipient_value, external_thread_id, subject, last_incoming_at, last_incoming_external_id, contact_id, revision)
              VALUES (@tenant,@project,@environment,@app,@workspace,@conversation,1,@inbox,@kind,@recipientKind,@recipientNamespace,@recipientValue,@thread,@subject,@lastAt,@lastId,@contact,1)
              ON CONFLICT (state_key) DO NOTHING
              RETURNING revision
              """
            : $"""
              UPDATE tf_channel_conversation_states SET recipient_kind=@recipientKind, recipient_namespace=@recipientNamespace, recipient_value=@recipientValue,
                  external_thread_id=@thread, subject=@subject, last_incoming_at=@lastAt, last_incoming_external_id=@lastId, contact_id=@contact, revision=revision+1
              WHERE state_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
                AND revision=@revision AND inbox_id=@inbox AND channel_kind=@kind
              RETURNING revision
              """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddScope(command, scope);
        AddText(command, "conversation", state.Conversation.Value);
        AddText(command, "inbox", state.Inbox.Value);
        AddText(command, "kind", state.Channel.Value);
        AddInt16(command, "recipientKind", (short)state.Recipient.Kind);
        AddText(command, "recipientNamespace", state.Recipient.Namespace);
        AddText(command, "recipientValue", state.Recipient.Value);
        AddText(command, "thread", state.ExternalThreadId);
        AddText(command, "subject", state.Subject);
        AddInstant(command, "lastAt", state.LastIncomingAt);
        AddText(command, "lastId", state.LastIncomingExternalId);
        AddText(command, "contact", state.Contact?.Value);
        if (expectedRevision is { } expected)
            AddInt64(command, "revision", expected);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new ChannelWriteApplied<StoredChannelConversationState>(new StoredChannelConversationState(scope, state, revision));
        var current = await LoadStateAsync(connection, scope, state.Conversation, cancellationToken).ConfigureAwait(false);
        return current is null ? ChannelWriteNotFound.Instance : new ChannelWriteConflict(current.Revision);
    }

    public async Task<IReadOnlyList<ConversationId>> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelKind channel, string externalThreadId, ContactId contact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(externalThreadId);
        await using var command = source.CreateCommand($"""
            SELECT conversation_id FROM tf_channel_conversation_states
            WHERE thread_key=tf_key_v1({ScopeArgs},@inbox,@kind,@thread,@contact) AND {ScopeWhere}
              AND inbox_id=@inbox AND channel_kind=@kind AND external_thread_id=@thread AND contact_id=@contact
            ORDER BY conversation_id DESC LIMIT {IChannelConversationStateStore.MaximumThreadMatches}
            """);
        AddScope(command, scope);
        AddText(command, "inbox", inbox.Value);
        AddText(command, "kind", Kind(channel));
        AddText(command, "thread", externalThreadId);
        AddText(command, "contact", contact.Value);
        var conversations = new List<ConversationId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            conversations.Add(new ConversationId(reader.GetString(0)));
        return conversations;
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
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadState(reader, scope) : null;
    }

    private static StoredChannelConversationState ReadState(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(11) != 1)
                throw new FormatException("Unsupported state format version.");
            var recipient = new ChannelRecipient(Defined((ContactIdentityKind)reader.GetInt16(3)), reader.GetString(5), NullableText(reader, 4));
            return new StoredChannelConversationState(scope, new ChannelConversationState(new ConversationId(reader.GetString(0)), new InboxId(reader.GetString(1)),
                new ChannelKind(reader.GetString(2)), recipient, NullableText(reader, 6), NullableText(reader, 7), NullableInstant(reader, 8), NullableText(reader, 9),
                NullableText(reader, 12) is { } contact ? new ContactId(contact) : null), reader.GetInt64(10));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored channel conversation state is invalid or incompatible.", error);
        }
    }

    // ---- References ----

    public async Task<int> AddAsync(TicketScope scope, InboxId inbox, ConversationId conversation, IReadOnlyList<ChannelMessageReference> references, DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > IChannelThreadStore.MaximumReferencesPerCall)
            throw new ArgumentException($"At most {IChannelThreadStore.MaximumReferencesPerCall} references per call.", nameof(references));
        var count = references.Count;
        if (count == 0)
            return 0;
        var refValues = new string[count];
        var directions = new short[count];
        var messageIds = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var reference = references[i] ?? throw new ArgumentException($"At most {IChannelThreadStore.MaximumReferencesPerCall} references per call.", nameof(references));
            refValues[i] = reference.Reference;
            directions[i] = (short)reference.Direction;
            messageIds[i] = reference.Message?.Value;
        }
        var at = ChannelConversationState.Microseconds(recordedAt);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["tf.channels.references", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value, conversation.Value],
            cancellationToken).ConfigureAwait(false);
        int added;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_channel_references (tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id, conversation_id, reference, format_version,
                direction, message_id, recorded_at)
            SELECT @tenant,@project,@environment,@app,@workspace,@inbox,@conversation,item.reference,1,item.direction,item.message,@at
            FROM unnest(@references, @directions, @messages) WITH ORDINALITY AS item(reference, direction, message, ord)
            ORDER BY item.ord
            ON CONFLICT (reference_key) DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "inbox", inbox.Value);
            AddText(insert, "conversation", conversation.Value);
            insert.Parameters.Add(new NpgsqlParameter<string[]>("references", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = refValues });
            insert.Parameters.Add(new NpgsqlParameter<short[]>("directions", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = directions });
            insert.Parameters.Add(new NpgsqlParameter<string?[]>("messages", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = messageIds });
            AddInstant(insert, "at", at);
            added = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (added > 0)
        {
            await using var prune = new NpgsqlCommand($"""
                DELETE FROM tf_channel_references WHERE conversation_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation AND {ScopeWhere}
                  AND sequence < (SELECT sequence FROM tf_channel_references WHERE conversation_key=tf_key_v1({ScopeArgs},@conversation) AND conversation_id=@conversation
                      AND {ScopeWhere} ORDER BY sequence DESC OFFSET @keep LIMIT 1)
                """, connection, transaction);
            AddScope(prune, scope);
            AddText(prune, "conversation", conversation.Value);
            AddInt32(prune, "keep", IChannelThreadStore.MaximumReferencesPerConversation - 1);
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
        var candidates = new List<string>(references.Count);
        HashSet<string>? seen = null;
        for (var i = 0; i < references.Count; i++)
        {
            var reference = references[i];
            if (reference is null)
                continue;
            if (candidates.Count >= 4)
            {
                seen ??= new HashSet<string>(candidates, StringComparer.Ordinal);
                if (seen.Add(reference))
                    candidates.Add(reference);
            }
            else if (!candidates.Contains(reference, StringComparer.Ordinal))
                candidates.Add(reference);
        }
        if (candidates.Count == 0)
            return null;
        await using var command = source.CreateCommand($"""
            SELECT r.conversation_id
            FROM unnest(@references) WITH ORDINALITY AS k(candidate, ord)
            JOIN tf_channel_references r
              ON r.reference_key = tf_key_v1({ScopeArgs},@inbox,k.candidate)
             AND r.reference = k.candidate AND r.inbox_id = @inbox
             AND r.tenant_id=@tenant AND r.project_id=@project AND r.environment_id=@environment AND r.app_id=@app AND r.workspace_id=@workspace
            ORDER BY k.ord LIMIT 1
            """);
        AddScope(command, scope);
        AddText(command, "inbox", inbox.Value);
        command.Parameters.Add(new NpgsqlParameter<string[]>("references", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = [.. candidates] });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string conversation ? new ConversationId(conversation) : null;
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
            references.Add(ReadReference(reader, conversation));
        return references;
    }

    private static StoredChannelMessageReference ReadReference(NpgsqlDataReader reader, ConversationId conversation)
    {
        try
        {
            if (reader.GetInt16(6) != 1)
                throw new FormatException("Unsupported reference format version.");
            var message = NullableText(reader, 3);
            return new StoredChannelMessageReference(conversation, new InboxId(reader.GetString(0)), new ChannelMessageReference(reader.GetString(1),
                Defined((MessageDirection)reader.GetInt16(2)), message is null ? null : new MessageId(message)), reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(5));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored channel reference is invalid or incompatible.", error);
        }
    }

    // ---- Dispatches ----

    public async Task<StoredChannelDispatch?> GetAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(conversation);
        if (message == default)
            throw new ArgumentException("A message identifier is required.", nameof(message));
        await using var command = source.CreateCommand($"""
            SELECT {DispatchColumns} FROM tf_channel_dispatches
            WHERE dispatch_key=tf_key_v1({ScopeArgs},@conversation,@message) AND conversation_id=@conversation AND message_id=@message AND {ScopeWhere}
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddText(command, "message", message.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDispatch(reader, scope) : null;
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
        AddInt16(command, "state", (short)dispatch.State);
        AddInt32(command, "attempts", dispatch.Attempts);
        AddInstant(command, "updated", dispatch.UpdatedAt);
        AddInstant(command, "next", dispatch.NextAttemptAt);
        AddText(command, "external", dispatch.ExternalMessageId);
        AddText(command, "code", dispatch.Code);
        if (expectedRevision is { } expected)
            AddInt64(command, "revision", expected);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new ChannelWriteApplied<StoredChannelDispatch>(new StoredChannelDispatch(scope, dispatch, revision));
        var current = await LoadDispatchAsync(connection, scope, dispatch.Conversation, dispatch.Message, cancellationToken).ConfigureAwait(false);
        return current is null ? ChannelWriteNotFound.Instance : new ChannelWriteConflict(current.Revision);
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
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDispatch(reader, scope) : null;
    }

    private static StoredChannelDispatch ReadDispatch(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(11) != 1)
                throw new FormatException("Unsupported dispatch format version.");
            return new StoredChannelDispatch(scope, new ChannelDispatch(new ConversationId(reader.GetString(0)), new MessageId(reader.GetString(1)), new InboxId(reader.GetString(2)),
                new ChannelKind(reader.GetString(3)), Defined((ChannelDispatchState)reader.GetInt16(4)), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6),
                NullableInstant(reader, 7), NullableText(reader, 8), NullableText(reader, 9)), reader.GetInt64(10));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored channel dispatch is invalid or incompatible.", error);
        }
    }

    // ---- Helpers ----

    /// <summary>Computes the exclusive upper bound for a COLLATE "C" (UTF-8 code-point order) prefix range scan.</summary>
    private static string? PrefixUpperBound(string prefix)
    {
        var span = prefix.AsSpan();
        var end = span.Length;
        while (end > 0)
        {
            if (Rune.DecodeLastFromUtf16(span[..end], out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
                return null;
            end -= consumed;
            var next = rune.Value + 1;
            if (next == 0xD800)
                next = 0xE000;
            if (next <= 0x10FFFF)
            {
                Span<char> tail = stackalloc char[2];
                var written = new Rune(next).EncodeToUtf16(tail);
                return string.Concat(span[..end], tail[..written]);
            }
        }
        return null;
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] parts, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]>("parts", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = parts });
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
        command.Parameters.Add(value is null
            ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value }
            : new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(value is { } instant
            ? new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = instant }
            : new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });

    private static void AddInt16(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddInt32(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddInt64(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void RequireId(ConversationId id)
    {
        if (id == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(id));
    }
}
