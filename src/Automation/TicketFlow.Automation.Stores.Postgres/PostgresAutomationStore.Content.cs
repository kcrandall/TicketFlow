using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Automation.Stores.Postgres;

public sealed partial class PostgresAutomationStore
{
    private const string UniqueViolation = "23505";
    private const string ShortcutConstraint = "tf_automation_canned_responses_shortcut";

    public async Task<StoredMacro?> GetAsync(TicketScope scope, MacroId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT macro_id, format_version, definition, revision FROM tf_automation_macros WHERE {MacroWhere}");
        AddScope(command, scope);
        AddText(command, "macro", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<AutomationWriteResult<StoredMacro>> CreateAsync(TicketScope scope, Macro macro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(macro);
        var definition = AutomationCodec.SerializeMacro(macro);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, "macros", cancellationToken).ConfigureAwait(false);
        if (await RevisionAsync(connection, transaction, $"SELECT revision FROM tf_automation_macros WHERE {MacroWhere}", scope, "macro", macro.Id.Value, cancellationToken).ConfigureAwait(false) is { } current)
            return new AutomationWriteConflict(current);
        if (await CountAsync(connection, transaction, "tf_automation_macros", scope, cancellationToken).ConfigureAwait(false) >= IMacroStore.MaximumMacrosPerWorkspace)
            return new AutomationWriteRejected(AutomationWriteRejection.CapacityExceeded);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_automation_macros({ScopeColumns}, macro_id, format_version, definition, revision) VALUES({ScopeValues}, @macro, 1, @definition, 1)
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "macro", macro.Id.Value);
            AddText(insert, "definition", definition);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AutomationWriteApplied<StoredMacro>(new StoredMacro(scope, macro, 1));
    }

    public async Task<AutomationWriteResult<StoredMacro>> UpdateAsync(TicketScope scope, Macro macro, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(macro);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"UPDATE tf_automation_macros SET definition=@definition, revision=revision+1 WHERE {MacroWhere} AND revision=@expected RETURNING revision",
            connection);
        AddScope(command, scope);
        AddText(command, "macro", macro.Id.Value);
        AddText(command, "definition", AutomationCodec.SerializeMacro(macro));
        command.Parameters.AddWithValue("expected", expectedRevision);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            return new AutomationWriteApplied<StoredMacro>(new StoredMacro(scope, macro, revision));
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_macros WHERE {MacroWhere}", scope, "macro", macro.Id.Value, cancellationToken).ConfigureAwait(false) is { } current
            ? new AutomationWriteConflict(current)
            : AutomationWriteNotFound.Instance;
    }

    public async Task<AutomationWriteResult<StoredMacro>> DeleteAsync(TicketScope scope, MacroId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_automation_macros WHERE {MacroWhere} AND revision=@expected RETURNING macro_id, format_version, definition, revision", connection))
        {
            AddScope(command, scope);
            AddText(command, "macro", id.Value);
            command.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new AutomationWriteApplied<StoredMacro>(ReadMacro(reader, scope));
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_macros WHERE {MacroWhere}", scope, "macro", id.Value, cancellationToken).ConfigureAwait(false) is { } current
            ? new AutomationWriteConflict(current)
            : AutomationWriteNotFound.Instance;
    }

    public async Task<StoredCannedResponse?> GetAsync(TicketScope scope, CannedResponseId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT response_id, format_version, definition, revision FROM tf_automation_canned_responses WHERE {ResponseWhere}");
        AddScope(command, scope);
        AddText(command, "response", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadResponse(reader, scope) : null;
    }

    public async Task<CannedResponsePage> ListAsync(TicketScope scope, CannedResponseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        await using var command = source.CreateCommand($"""
            SELECT response_id, format_version, definition, revision FROM tf_automation_canned_responses
            WHERE {ScopeKeyWhere} AND (@prefix::text IS NULL OR starts_with(shortcut, @prefix)) AND (@after::text IS NULL OR shortcut > @after)
            ORDER BY shortcut LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "prefix", query.Prefix);
        AddText(command, "after", query.AfterShortcut);
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        var rows = new List<StoredCannedResponse>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadResponse(reader, scope));
        return rows.Count <= query.Limit
            ? new CannedResponsePage(rows, null)
            : new CannedResponsePage(rows[..query.Limit], rows[query.Limit - 1].Response.Shortcut);
    }

    public async Task<AutomationWriteResult<StoredCannedResponse>> CreateAsync(TicketScope scope, CannedResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        var definition = AutomationCodec.SerializeCannedResponse(response);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, "canned-responses", cancellationToken).ConfigureAwait(false);
        if (await RevisionAsync(connection, transaction, $"SELECT revision FROM tf_automation_canned_responses WHERE {ResponseWhere}", scope, "response", response.Id.Value, cancellationToken)
            .ConfigureAwait(false) is { } current)
            return new AutomationWriteConflict(current);
        if (await CountAsync(connection, transaction, "tf_automation_canned_responses", scope, cancellationToken).ConfigureAwait(false) >= ICannedResponseStore.MaximumResponsesPerWorkspace)
            return new AutomationWriteRejected(AutomationWriteRejection.CapacityExceeded);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_automation_canned_responses({ScopeColumns}, response_id, shortcut, format_version, definition, revision)
            VALUES({ScopeValues}, @response, @shortcut, 1, @definition, 1)
            ON CONFLICT (shortcut_key) DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "response", response.Id.Value);
            AddText(insert, "shortcut", response.Shortcut);
            AddText(insert, "definition", definition);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return new AutomationWriteRejected(AutomationWriteRejection.DuplicateShortcut);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AutomationWriteApplied<StoredCannedResponse>(new StoredCannedResponse(scope, response, 1));
    }

    public async Task<AutomationWriteResult<StoredCannedResponse>> UpdateAsync(TicketScope scope, CannedResponse response, long expectedRevision, CancellationToken cancellationToken = default)
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
            command.Parameters.AddWithValue("expected", expectedRevision);
            try
            {
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                    return new AutomationWriteApplied<StoredCannedResponse>(new StoredCannedResponse(scope, response, revision));
            }
            catch (PostgresException error) when (error.SqlState == UniqueViolation && error.ConstraintName == ShortcutConstraint)
            {
                return new AutomationWriteRejected(AutomationWriteRejection.DuplicateShortcut);
            }
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_canned_responses WHERE {ResponseWhere}", scope, "response", response.Id.Value, cancellationToken)
            .ConfigureAwait(false) is { } current
            ? new AutomationWriteConflict(current)
            : AutomationWriteNotFound.Instance;
    }

    public async Task<AutomationWriteResult<StoredCannedResponse>> DeleteAsync(TicketScope scope, CannedResponseId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand($"DELETE FROM tf_automation_canned_responses WHERE {ResponseWhere} AND revision=@expected RETURNING response_id, format_version, definition, revision",
            connection))
        {
            AddScope(command, scope);
            AddText(command, "response", id.Value);
            command.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new AutomationWriteApplied<StoredCannedResponse>(ReadResponse(reader, scope));
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_canned_responses WHERE {ResponseWhere}", scope, "response", id.Value, cancellationToken)
            .ConfigureAwait(false) is { } current
            ? new AutomationWriteConflict(current)
            : AutomationWriteNotFound.Instance;
    }

    private static StoredMacro ReadMacro(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        RequireFormat(reader.GetInt16(1));
        return new StoredMacro(scope, AutomationCodec.DeserializeMacro(new MacroId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
    }, "macro");

    private static StoredCannedResponse ReadResponse(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        RequireFormat(reader.GetInt16(1));
        return new StoredCannedResponse(scope, AutomationCodec.DeserializeCannedResponse(new CannedResponseId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
    }, "canned response");
}
