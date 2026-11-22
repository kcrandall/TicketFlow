using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string SettingsColumns = "format_version, name, default_locale, time_zone, enabled_features, disabled_features, revision";
    private const string SettingsRevision = $"SELECT revision FROM tf_workspace_settings WHERE {ScopeKeyWhere}";

    public async Task<StoredWorkspaceSettings?> GetAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {SettingsColumns} FROM tf_workspace_settings WHERE {ScopeKeyWhere}");
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSettings(reader, scope) : null;
    }

    public async Task<WorkspaceWriteResult<StoredWorkspaceSettings>> CreateAsync(TicketScope scope, WorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_settings(tenant_id, project_id, environment_id, app_id, workspace_id, format_version, name, default_locale, time_zone, enabled_features, disabled_features, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, 1, @name, @locale, @zone, @enabled, @disabled, 1)
            ON CONFLICT DO NOTHING RETURNING revision
            """, connection, transaction))
        {
            BindSettings(insert, scope, settings);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
                return await MissAsync<StoredWorkspaceSettings>(connection, transaction, SettingsRevision, command => AddScope(command, scope), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredWorkspaceSettings>(new(scope, settings, 1));
    }

    public async Task<WorkspaceWriteResult<StoredWorkspaceSettings>> UpdateAsync(TicketScope scope, WorkspaceSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_workspace_settings SET name=@name, default_locale=@locale, time_zone=@zone, enabled_features=@enabled, disabled_features=@disabled, revision=revision+1
            WHERE {ScopeKeyWhere} AND revision=@expected RETURNING revision
            """, connection, transaction))
        {
            BindSettings(update, scope, settings);
            update.Parameters.AddWithValue("expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new WorkspaceWriteApplied<StoredWorkspaceSettings>(new(scope, settings, revision));
            }
        }
        return await MissAsync<StoredWorkspaceSettings>(connection, transaction, SettingsRevision, command => AddScope(command, scope), cancellationToken).ConfigureAwait(false);
    }

    private static void BindSettings(NpgsqlCommand command, TicketScope scope, WorkspaceSettings settings)
    {
        AddScope(command, scope);
        AddText(command, "name", settings.Name);
        AddText(command, "locale", settings.DefaultLocale);
        AddText(command, "zone", settings.TimeZoneId);
        AddTextArray(command, "enabled", settings.FeatureToggles.Where(pair => pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal));
        AddTextArray(command, "disabled", settings.FeatureToggles.Where(pair => !pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    private static StoredWorkspaceSettings ReadSettings(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(0) != 1)
            throw new FormatException("Unsupported workspace settings format version.");
        var toggles = reader.GetFieldValue<string[]>(4).Select(key => new KeyValuePair<string, bool>(key, true))
            .Concat(reader.GetFieldValue<string[]>(5).Select(key => new KeyValuePair<string, bool>(key, false)));
        return new StoredWorkspaceSettings(scope, new WorkspaceSettings(reader.GetString(1), reader.GetString(2), reader.GetString(3), toggles), reader.GetInt64(6));
    }, "workspace settings");
}
