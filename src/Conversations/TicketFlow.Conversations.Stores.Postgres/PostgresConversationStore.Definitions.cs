using System.Data;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Conversations.Stores.Postgres;

public sealed partial class PostgresConversationStore
{
    private const string DefinitionColumns = "attribute_key, format_version, attribute_type, sensitive, display_name, revision";
    private const string DefinitionKey = "definition_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@key) AND attribute_key=@key AND " + ScopeWhere;

    private const string CreateDefinitionSql = $"""
        WITH existing AS (
            SELECT revision FROM tf_conversation_attribute_definitions WHERE {DefinitionKey}
        ),
        inserted AS (
            INSERT INTO tf_conversation_attribute_definitions(tenant_id, project_id, environment_id, app_id, workspace_id, attribute_key, format_version, attribute_type,
                sensitive, display_name, revision)
            SELECT @tenant, @project, @environment, @app, @workspace, @key, 1, @type, @sensitive, @display_name, 1
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_conversation_attribute_definitions WHERE scope_key={ScopeKey} AND {ScopeWhere}) < @max
            RETURNING revision
        )
        SELECT (SELECT revision FROM inserted), (SELECT revision FROM existing)
        """;

    public async Task<StoredConversationAttributeDefinition?> GetAsync(TicketScope scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ConversationAttributeDefinition.RequireKey(key, nameof(key));
        await using var command = source.CreateCommand($"SELECT {DefinitionColumns} FROM tf_conversation_attribute_definitions WHERE {DefinitionKey}");
        AddScope(command, scope);
        AddText(command, "key", key);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDefinition(reader, scope) : null;
    }

    public async Task<IReadOnlyList<StoredConversationAttributeDefinition>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT {DefinitionColumns} FROM tf_conversation_attribute_definitions WHERE scope_key={ScopeKey} AND {ScopeWhere}
            ORDER BY attribute_key LIMIT {IConversationAttributeDefinitionStore.MaximumDefinitionsPerWorkspace}
            """);
        AddScope(command, scope);
        var definitions = new List<StoredConversationAttributeDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            definitions.Add(ReadDefinition(reader, scope));
        return definitions;
    }

    public async Task<ConversationAttributeDefinitionWriteResult> CreateAsync(TicketScope scope, ConversationAttributeDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction))
        {
            AddTextArray(lockCommand, "parts", ["conversation-definitions", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value]);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long? insertedRevision;
        long? existingRevision;
        await using (var create = new NpgsqlCommand(CreateDefinitionSql, connection, transaction))
        {
            BindDefinition(create, scope, definition);
            AddInt64(create, "max", IConversationAttributeDefinitionStore.MaximumDefinitionsPerWorkspace);
            await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            insertedRevision = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(0);
            existingRevision = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(1);
        }
        if (insertedRevision is { } rev)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ConversationAttributeDefinitionApplied(new(scope, definition, rev));
        }
        return existingRevision is { } existing
            ? new ConversationWriteConflict(existing)
            : new ConversationWriteRejected(ConversationWriteRejection.CapacityExceeded);
    }

    public async Task<ConversationAttributeDefinitionWriteResult> UpdateAsync(TicketScope scope, ConversationAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_conversation_attribute_definitions SET sensitive=@sensitive, display_name=@display_name, revision=revision+1
            WHERE {DefinitionKey} AND revision=@expected AND attribute_type=@type RETURNING revision
            """, connection))
        {
            BindDefinition(update, scope, definition);
            AddInt64(update, "expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new ConversationAttributeDefinitionApplied(new(scope, definition, revision));
        }
        var current = await GetDefinitionAsync(connection, null, scope, definition.Key, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return ConversationWriteNotFound.Instance;
        if (current.Revision != expectedRevision)
            return new ConversationWriteConflict(current.Revision);
        return new ConversationWriteRejected(ConversationWriteRejection.TypeChanged);
    }

    public async Task<ConversationAttributeDefinitionWriteResult> DeleteAsync(TicketScope scope, string key, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ConversationAttributeDefinition.RequireKey(key, nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_conversation_attribute_definitions WHERE {DefinitionKey} AND revision=@expected RETURNING {DefinitionColumns}", connection))
        {
            AddScope(delete, scope);
            AddText(delete, "key", key);
            AddInt64(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new ConversationAttributeDefinitionApplied(ReadDefinition(reader, scope));
        }
        return await GetDefinitionAsync(connection, null, scope, key, cancellationToken).ConfigureAwait(false) is { } current
            ? new ConversationWriteConflict(current.Revision)
            : ConversationWriteNotFound.Instance;
    }

    private static async Task<StoredConversationAttributeDefinition?> GetDefinitionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {DefinitionColumns} FROM tf_conversation_attribute_definitions WHERE {DefinitionKey}", connection, transaction);
        AddScope(command, scope);
        AddText(command, "key", key);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDefinition(reader, scope) : null;
    }

    private static void BindDefinition(NpgsqlCommand command, TicketScope scope, ConversationAttributeDefinition definition)
    {
        AddScope(command, scope);
        AddText(command, "key", definition.Key);
        AddInt16(command, "type", (short)definition.Type);
        AddBool(command, "sensitive", definition.Sensitive);
        AddText(command, "display_name", definition.DisplayName);
    }

    private static StoredConversationAttributeDefinition ReadDefinition(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported attribute definition format version.");
            return new StoredConversationAttributeDefinition(scope,
                new ConversationAttributeDefinition(reader.GetString(0), Defined((ConversationAttributeType)reader.GetInt16(2)), reader.GetBoolean(3), reader.NullableText(4)),
                reader.GetInt64(5));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored conversation attribute definition is invalid or incompatible.", error);
        }
    }
}
