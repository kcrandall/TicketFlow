using System.Data;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Contacts.Stores.Postgres;

public sealed partial class PostgresContactStore
{
    private const string DefinitionColumns = "attribute_key, format_version, attribute_type, sensitive, display_name, revision";
    private const string DefinitionKey = "definition_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@key) AND attribute_key=@key AND " + ScopeWhere;

    public async Task<StoredContactAttributeDefinition?> GetAsync(TicketScope scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ContactAttributeDefinition.RequireKey(key, nameof(key));
        await using var command = source.CreateCommand($"SELECT {DefinitionColumns} FROM tf_contact_attribute_definitions WHERE {DefinitionKey}");
        AddScope(command, scope);
        AddText(command, "key", key);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDefinition(reader, scope) : null;
    }

    Task<IReadOnlyList<StoredContactAttributeDefinition>> IContactAttributeDefinitionStore.ListAsync(TicketScope scope, CancellationToken cancellationToken) =>
        ListDefinitionsAsync(scope, cancellationToken);

    /// <summary>Attribute definitions in <paramref name="scope"/> ordered by key.</summary>
    public async Task<IReadOnlyList<StoredContactAttributeDefinition>> ListDefinitionsAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT {DefinitionColumns} FROM tf_contact_attribute_definitions WHERE scope_key={ScopeKey} AND {ScopeWhere}
            ORDER BY attribute_key LIMIT {IContactAttributeDefinitionStore.MaximumDefinitionsPerWorkspace}
            """);
        AddScope(command, scope);
        var definitions = new List<StoredContactAttributeDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            definitions.Add(ReadDefinition(reader, scope));
        return definitions;
    }

    public async Task<AttributeDefinitionWriteResult> CreateAsync(TicketScope scope, ContactAttributeDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction))
        {
            AddTextArray(lockCommand, "parts", ["contact-definitions", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value]);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_contact_attribute_definitions(tenant_id, project_id, environment_id, app_id, workspace_id, attribute_key, format_version, attribute_type, sensitive, display_name, revision)
            SELECT @tenant, @project, @environment, @app, @workspace, @key, 1, @type, @sensitive, @display_name, 1
            WHERE (SELECT count(*) FROM tf_contact_attribute_definitions WHERE scope_key={ScopeKey} AND {ScopeWhere}) < {IContactAttributeDefinitionStore.MaximumDefinitionsPerWorkspace}
            ON CONFLICT (definition_key) DO NOTHING
            """, connection, transaction))
        {
            BindDefinition(insert, scope, definition);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AttributeDefinitionApplied(new(scope, definition, 1));
            }
        }
        if (await DefinitionRevisionAsync(connection, transaction, scope, definition.Key, cancellationToken).ConfigureAwait(false) is { } existing)
            return new AttributeDefinitionConflict(existing);
        return new ContactWriteRejected(ContactWriteRejection.CapacityExceeded);
    }

    public async Task<AttributeDefinitionWriteResult> UpdateAsync(TicketScope scope, ContactAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_contact_attribute_definitions SET sensitive=@sensitive, display_name=@display_name, revision=revision+1
            WHERE {DefinitionKey} AND revision=@expected AND attribute_type=@type RETURNING revision
            """, connection))
        {
            BindDefinition(update, scope, definition);
            AddBigInt(update, "expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new AttributeDefinitionApplied(new(scope, definition, revision));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_contact_attribute_definitions WHERE {DefinitionKey}", connection);
        AddScope(current, scope);
        AddText(current, "key", definition.Key);
        if (await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long currentRevision)
            return AttributeDefinitionNotFound.Instance;
        if (currentRevision != expectedRevision)
            return new AttributeDefinitionConflict(currentRevision);
        return new ContactWriteRejected(ContactWriteRejection.TypeChanged);
    }

    public async Task<AttributeDefinitionWriteResult> DeleteAsync(TicketScope scope, string key, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ContactAttributeDefinition.RequireKey(key, nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_contact_attribute_definitions WHERE {DefinitionKey} AND revision=@expected RETURNING {DefinitionColumns}", connection))
        {
            AddScope(delete, scope);
            AddText(delete, "key", key);
            AddBigInt(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new AttributeDefinitionApplied(ReadDefinition(reader, scope));
        }
        return await DefinitionRevisionAsync(connection, null, scope, key, cancellationToken).ConfigureAwait(false) is { } revision
            ? new AttributeDefinitionConflict(revision)
            : AttributeDefinitionNotFound.Instance;
    }

    private static async Task<long?> DefinitionRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_contact_attribute_definitions WHERE {DefinitionKey}", connection, transaction);
        AddScope(command, scope);
        AddText(command, "key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static void BindDefinition(NpgsqlCommand command, TicketScope scope, ContactAttributeDefinition definition)
    {
        AddScope(command, scope);
        AddText(command, "key", definition.Key);
        AddSmallInt(command, "type", (short)definition.Type);
        AddBool(command, "sensitive", definition.Sensitive);
        AddText(command, "display_name", definition.DisplayName);
    }

    private static StoredContactAttributeDefinition ReadDefinition(NpgsqlDataReader reader, TicketScope scope, int offset = 0)
    {
        try
        {
            if (reader.GetInt16(offset + 1) != 1)
                throw new FormatException("Unsupported attribute definition format version.");
            return new StoredContactAttributeDefinition(scope,
                new ContactAttributeDefinition(reader.GetString(offset), (ContactAttributeType)reader.GetInt16(offset + 2), reader.GetBoolean(offset + 3), NullableText(reader, offset + 4)),
                reader.GetInt64(offset + 5));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "attribute definition");
        }
    }
}
