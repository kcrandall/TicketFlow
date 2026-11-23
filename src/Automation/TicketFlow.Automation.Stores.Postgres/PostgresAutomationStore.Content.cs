using System.Data;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Automation.Stores.Postgres;

public sealed partial class PostgresAutomationStore
{
    private const string UniqueViolation = "23505";
    private const string ShortcutConstraint = "tf_automation_canned_responses_shortcut";

    private const string CreateMacroSql = $"""
        WITH existing AS (
            SELECT revision FROM tf_automation_macros WHERE {MacroWhere}
        ),
        inserted AS (
            INSERT INTO tf_automation_macros({ScopeColumns}, macro_id, visibility, owner_id, format_version, definition, revision)
            SELECT {ScopeValues}, @macro, @visibility, @owner, 1, @definition, 1
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_automation_macros WHERE {ScopeKeyWhere}) < @max
            RETURNING revision
        )
        SELECT (SELECT revision FROM inserted), (SELECT revision FROM existing)
        """;

    private const string CreateResponseSql = $"""
        WITH existing AS (
            SELECT revision FROM tf_automation_canned_responses WHERE {ResponseWhere}
        ),
        cnt AS (
            SELECT count(*) AS n FROM tf_automation_canned_responses WHERE {ScopeKeyWhere}
        ),
        inserted AS (
            INSERT INTO tf_automation_canned_responses({ScopeColumns}, response_id, shortcut, format_version, definition, revision)
            SELECT {ScopeValues}, @response, @shortcut, 1, @definition, 1
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT n FROM cnt) < @max
            ON CONFLICT (shortcut_key) DO NOTHING
            RETURNING revision
        )
        SELECT (SELECT revision FROM inserted), (SELECT revision FROM existing), ((SELECT n FROM cnt) >= @max)
        """;

    public async Task<StoredMacro?> GetAsync(TicketScope scope, MacroId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT macro_id, format_version, definition, revision FROM tf_automation_macros WHERE {MacroWhere}");
        AddScope(command, scope);
        AddText(command, "macro", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMacro(reader, scope) : null;
    }

    async Task<IReadOnlyList<StoredMacro>> IMacroStore.ListAsync(TicketScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT macro_id, format_version, definition, revision FROM tf_automation_macros WHERE {ScopeKeyWhere}
            ORDER BY macro_id COLLATE "C" LIMIT {IMacroStore.MaximumMacrosPerWorkspace}
            """);
        AddScope(command, scope);
        var macros = new List<StoredMacro>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            macros.Add(ReadMacro(reader, scope));
        return macros;
    }

    async Task<IReadOnlyList<StoredMacro>> IMacroStore.ListVisibleAsync(TicketScope scope, UserId? owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var sql = owner is null
            ? $"""
              SELECT macro_id, format_version, definition, revision FROM tf_automation_macros
              WHERE {ScopeKeyWhere} AND visibility = 1
              ORDER BY macro_id COLLATE "C" LIMIT {IMacroStore.MaximumMacrosPerWorkspace}
              """
            : $"""
              SELECT macro_id, format_version, definition, revision FROM tf_automation_macros
              WHERE {ScopeKeyWhere} AND (visibility = 1 OR (visibility = 2 AND owner_id = @owner))
              ORDER BY macro_id COLLATE "C" LIMIT {IMacroStore.MaximumMacrosPerWorkspace}
              """;
        await using var command = source.CreateCommand(sql);
        AddScope(command, scope);
        if (owner is { } userId)
            AddText(command, "owner", userId.Value);
        var macros = new List<StoredMacro>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            macros.Add(ReadMacro(reader, scope));
        return macros;
    }

    public async Task<StoreWriteResult<StoredMacro, AutomationWriteRejection>> CreateAsync(TicketScope scope, Macro macro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(macro);
        var definition = AutomationCodec.SerializeMacro(macro);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, "macros", cancellationToken).ConfigureAwait(false);
        long? insertedRevision;
        long? existingRevision;
        await using (var create = new NpgsqlCommand(CreateMacroSql, connection, transaction))
        {
            AddScope(create, scope);
            AddText(create, "macro", macro.Id.Value);
            AddShort(create, "visibility", (short)macro.Visibility);
            AddText(create, "owner", macro.Owner?.Value);
            AddText(create, "definition", definition);
            AddLong(create, "max", IMacroStore.MaximumMacrosPerWorkspace);
            await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            insertedRevision = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            existingRevision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        if (insertedRevision is { } rev)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoreApplied<StoredMacro>(new StoredMacro(scope, macro, rev));
        }
        return existingRevision is { } current
            ? new StoreConflict(current)
            : new StoreRejected<AutomationWriteRejection>(AutomationWriteRejection.CapacityExceeded);
    }

    public async Task<StoreWriteResult<StoredMacro, AutomationWriteRejection>> UpdateAsync(TicketScope scope, Macro macro, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(macro);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"UPDATE tf_automation_macros SET visibility=@visibility, owner_id=@owner, definition=@definition, revision=revision+1 WHERE {MacroWhere} AND revision=@expected RETURNING revision",
            connection);
        AddScope(command, scope);
        AddText(command, "macro", macro.Id.Value);
        AddShort(command, "visibility", (short)macro.Visibility);
        AddText(command, "owner", macro.Owner?.Value);
        AddText(command, "definition", AutomationCodec.SerializeMacro(macro));
        AddLong(command, "expected", expectedRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new StoreApplied<StoredMacro>(new StoredMacro(scope, macro, revision));
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_macros WHERE {MacroWhere}", scope, "macro", macro.Id.Value, cancellationToken).ConfigureAwait(false) is { } current
            ? new StoreConflict(current)
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredMacro, AutomationWriteRejection>> DeleteAsync(TicketScope scope, MacroId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_automation_macros WHERE {MacroWhere} AND revision=@expected RETURNING macro_id, format_version, definition, revision", connection))
        {
            AddScope(command, scope);
            AddText(command, "macro", id.Value);
            AddLong(command, "expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredMacro>(ReadMacro(reader, scope));
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_macros WHERE {MacroWhere}", scope, "macro", id.Value, cancellationToken).ConfigureAwait(false) is { } current
            ? new StoreConflict(current)
            : StoreNotFound.Instance;
    }

    public async Task<StoredCannedResponse?> GetAsync(TicketScope scope, CannedResponseId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT response_id, format_version, definition, revision FROM tf_automation_canned_responses WHERE {ResponseWhere}");
        AddScope(command, scope);
        AddText(command, "response", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadResponse(reader, scope) : null;
    }

    public async Task<StoredCannedResponse?> FindByShortcutAsync(TicketScope scope, string shortcut, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(shortcut);
        await using var command = source.CreateCommand($"""
            SELECT response_id, format_version, definition, revision FROM tf_automation_canned_responses
            WHERE shortcut_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@shortcut) AND {ScopeWhere} AND shortcut=@shortcut
            """);
        AddScope(command, scope);
        AddText(command, "shortcut", shortcut);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadResponse(reader, scope) : null;
    }

    public async Task<CannedResponsePage> ListAsync(TicketScope scope, CannedResponseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        var upper = query.Prefix is { } prefix ? PrefixUpperBound(prefix) : null;
        var prefixClause = query.Prefix is null
            ? ""
            : upper is not null
                ? " AND shortcut >= @prefix AND shortcut < @prefix_upper"
                : " AND shortcut >= @prefix";
        var afterClause = query.AfterShortcut is null ? "" : " AND shortcut > @after";
        command.CommandText = $"""
            SELECT response_id, format_version, definition, revision FROM tf_automation_canned_responses
            WHERE {ScopeKeyWhere}{prefixClause}{afterClause}
            ORDER BY shortcut LIMIT @limit
            """;
        if (query.Prefix is not null)
        {
            AddText(command, "prefix", query.Prefix);
            if (upper is not null)
                AddText(command, "prefix_upper", upper);
        }
        if (query.AfterShortcut is not null)
            AddText(command, "after", query.AfterShortcut);
        AddInt(command, "limit", query.Limit + 1);
        var rows = new List<StoredCannedResponse>(Math.Min(query.Limit + 1, 64));
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadResponse(reader, scope));
        if (rows.Count <= query.Limit)
            return new CannedResponsePage(rows, null);
        rows.RemoveAt(query.Limit);
        return new CannedResponsePage(rows, rows[^1].Response.Shortcut);
    }

    public async Task<StoreWriteResult<StoredCannedResponse, AutomationWriteRejection>> CreateAsync(TicketScope scope, CannedResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        var definition = AutomationCodec.SerializeCannedResponse(response);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, "canned-responses", cancellationToken).ConfigureAwait(false);
        long? insertedRevision;
        long? existingRevision;
        bool atCapacity;
        await using (var create = new NpgsqlCommand(CreateResponseSql, connection, transaction))
        {
            AddScope(create, scope);
            AddText(create, "response", response.Id.Value);
            AddText(create, "shortcut", response.Shortcut);
            AddText(create, "definition", definition);
            AddLong(create, "max", ICannedResponseStore.MaximumResponsesPerWorkspace);
            await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            insertedRevision = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            existingRevision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            atCapacity = reader.GetBoolean(2);
        }
        if (insertedRevision is { } rev)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoreApplied<StoredCannedResponse>(new StoredCannedResponse(scope, response, rev));
        }
        if (existingRevision is { } current)
            return new StoreConflict(current);
        return new StoreRejected<AutomationWriteRejection>(atCapacity ? AutomationWriteRejection.CapacityExceeded : AutomationWriteRejection.DuplicateShortcut);
    }

    public async Task<StoreWriteResult<StoredCannedResponse, AutomationWriteRejection>> UpdateAsync(TicketScope scope, CannedResponse response, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"""
            UPDATE tf_automation_canned_responses SET shortcut=@shortcut, definition=@definition, revision=revision+1
            WHERE {ResponseWhere} AND revision=@expected RETURNING revision
            """, connection))
        {
            AddScope(command, scope);
            AddText(command, "response", response.Id.Value);
            AddText(command, "shortcut", response.Shortcut);
            AddText(command, "definition", AutomationCodec.SerializeCannedResponse(response));
            AddLong(command, "expected", expectedRevision);
            try
            {
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                    return new StoreApplied<StoredCannedResponse>(new StoredCannedResponse(scope, response, revision));
            }
            catch (PostgresException error) when (error.SqlState == UniqueViolation && error.ConstraintName == ShortcutConstraint)
            {
                return new StoreRejected<AutomationWriteRejection>(AutomationWriteRejection.DuplicateShortcut);
            }
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_canned_responses WHERE {ResponseWhere}", scope, "response", response.Id.Value, cancellationToken)
            .ConfigureAwait(false) is { } current
            ? new StoreConflict(current)
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredCannedResponse, AutomationWriteRejection>> DeleteAsync(TicketScope scope, CannedResponseId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_automation_canned_responses WHERE {ResponseWhere} AND revision=@expected RETURNING response_id, format_version, definition, revision",
            connection))
        {
            AddScope(command, scope);
            AddText(command, "response", id.Value);
            AddLong(command, "expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredCannedResponse>(ReadResponse(reader, scope));
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_canned_responses WHERE {ResponseWhere}", scope, "response", id.Value, cancellationToken)
            .ConfigureAwait(false) is { } current
            ? new StoreConflict(current)
            : StoreNotFound.Instance;
    }

    private static StoredMacro ReadMacro(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            RequireFormat(reader.GetInt16(1));
            return new StoredMacro(scope, AutomationCodec.DeserializeMacro(new MacroId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored automation macro is invalid or incompatible.", error);
        }
    }

    private static StoredCannedResponse ReadResponse(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            RequireFormat(reader.GetInt16(1));
            return new StoredCannedResponse(scope, AutomationCodec.DeserializeCannedResponse(new CannedResponseId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored automation canned response is invalid or incompatible.", error);
        }
    }
}
