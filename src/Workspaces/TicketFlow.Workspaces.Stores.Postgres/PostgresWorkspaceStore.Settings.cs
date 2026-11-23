using System.Data;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string SettingsColumns = "format_version, name, default_locale, time_zone, enabled_features, disabled_features, revision";

    public async Task<StoredWorkspaceSettings?> GetAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {SettingsColumns} FROM tf_workspace_settings WHERE {ScopeKeyWhere}");
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSettings(reader, scope) : null;
    }

    public async Task<StoreWriteResult<StoredWorkspaceSettings, WorkspaceWriteRejection>> CreateAsync(TicketScope scope, WorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_settings(tenant_id, project_id, environment_id, app_id, workspace_id, format_version, name, default_locale, time_zone, enabled_features, disabled_features, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, 1, @name, @locale, @zone, @enabled, @disabled, 1)
            ON CONFLICT DO NOTHING RETURNING revision
            """, connection))
        {
            BindSettings(insert, scope, settings);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new StoreApplied<StoredWorkspaceSettings>(new(scope, settings, revision));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_workspace_settings WHERE {ScopeKeyWhere}", connection);
        AddScope(current, scope);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StoreConflict(existing)
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredWorkspaceSettings, WorkspaceWriteRejection>> UpdateAsync(TicketScope scope, WorkspaceSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_workspace_settings SET name=@name, default_locale=@locale, time_zone=@zone, enabled_features=@enabled, disabled_features=@disabled, revision=revision+1
            WHERE {ScopeKeyWhere} AND revision=@expected RETURNING revision
            """, connection))
        {
            BindSettings(update, scope, settings);
            AddLong(update, "expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new StoreApplied<StoredWorkspaceSettings>(new(scope, settings, revision));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_workspace_settings WHERE {ScopeKeyWhere}", connection);
        AddScope(current, scope);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StoreConflict(existing)
            : StoreNotFound.Instance;
    }

    private static void BindSettings(NpgsqlCommand command, TicketScope scope, WorkspaceSettings settings)
    {
        AddScope(command, scope);
        AddText(command, "name", settings.Name);
        AddText(command, "locale", settings.DefaultLocale);
        AddText(command, "zone", settings.TimeZoneId);
        if (settings.FeatureToggles.Count == 0)
        {
            AddTextArray(command, "enabled", Array.Empty<string>());
            AddTextArray(command, "disabled", Array.Empty<string>());
            return;
        }
        List<string>? enabled = null;
        List<string>? disabled = null;
        foreach (var pair in settings.FeatureToggles)
        {
            if (pair.Value)
                (enabled ??= []).Add(pair.Key);
            else
                (disabled ??= []).Add(pair.Key);
        }
        string[] enabledArr;
        if (enabled is null)
            enabledArr = [];
        else
        {
            enabled.Sort(StringComparer.Ordinal);
            enabledArr = [.. enabled];
        }
        string[] disabledArr;
        if (disabled is null)
            disabledArr = [];
        else
        {
            disabled.Sort(StringComparer.Ordinal);
            disabledArr = [.. disabled];
        }
        AddTextArray(command, "enabled", enabledArr);
        AddTextArray(command, "disabled", disabledArr);
    }

    private static StoredWorkspaceSettings ReadSettings(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(0) != 1)
                throw new FormatException("Unsupported workspace settings format version.");
            var enabled = reader.GetFieldValue<string[]>(4);
            var disabled = reader.GetFieldValue<string[]>(5);
            KeyValuePair<string, bool>[]? toggles = null;
            if (enabled.Length + disabled.Length > 0)
            {
                toggles = new KeyValuePair<string, bool>[enabled.Length + disabled.Length];
                var idx = 0;
                for (var i = 0; i < enabled.Length; i++)
                    toggles[idx++] = new(enabled[i], true);
                for (var i = 0; i < disabled.Length; i++)
                    toggles[idx++] = new(disabled[i], false);
            }
            return new StoredWorkspaceSettings(scope, new WorkspaceSettings(reader.GetString(1), reader.GetString(2), reader.GetString(3), toggles), reader.GetInt64(6));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "workspace settings");
        }
    }
}
