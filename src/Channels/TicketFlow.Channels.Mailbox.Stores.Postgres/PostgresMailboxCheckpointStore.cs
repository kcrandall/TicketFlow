using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Channels.Mailbox.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IMailboxCheckpointStore"/> over an application-owned
/// <see cref="NpgsqlDataSource"/>. Tables are created explicitly by <see cref="InitializeAsync"/>.
/// </summary>
public sealed class PostgresMailboxCheckpointStore(NpgsqlDataSource dataSource) : IMailboxCheckpointStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeArgs = "@tenant,@project,@environment,@app,@workspace";

    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresMailboxMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<MailboxCheckpoint?> GetCheckpointAsync(TicketScope scope, string configurationId, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        ArgumentNullException.ThrowIfNull(folder);
        await using var command = source.CreateCommand($"""
            SELECT uid_validity, last_uid, updated_at, format_version
            FROM tf_mailbox_checkpoints
            WHERE mailbox_key = tf_key_v1({ScopeArgs}, @config, @folder) AND {ScopeWhere} AND configuration_id=@config AND folder=@folder
            """);
        AddScope(command, scope);
        AddText(command, "config", configurationId);
        AddText(command, "folder", folder);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetInt16(3) != 1)
            throw new FormatException("Unsupported mailbox checkpoint format version.");
        return new MailboxCheckpoint((uint)reader.GetInt64(0), (uint)reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task SaveCheckpointAsync(TicketScope scope, string configurationId, string folder, MailboxCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using var command = source.CreateCommand("""
            INSERT INTO tf_mailbox_checkpoints(tenant_id, project_id, environment_id, app_id, workspace_id, configuration_id, folder, format_version, uid_validity, last_uid, updated_at)
            VALUES(@tenant, @project, @environment, @app, @workspace, @config, @folder, 1, @uid_validity, @last_uid, @updated_at)
            ON CONFLICT (mailbox_key) DO UPDATE SET
                uid_validity = EXCLUDED.uid_validity,
                last_uid = EXCLUDED.last_uid,
                updated_at = EXCLUDED.updated_at
            """);
        AddScope(command, scope);
        AddText(command, "config", configurationId);
        AddText(command, "folder", folder);
        command.Parameters.AddWithValue("uid_validity", (long)checkpoint.UidValidity);
        command.Parameters.AddWithValue("last_uid", (long)checkpoint.LastUid);
        command.Parameters.Add(new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = checkpoint.UpdatedAt.ToUniversalTime() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryRecordMessageIdAsync(TicketScope scope, string configurationId, string folder, string messageId, DateTimeOffset seenAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(messageId);
        await using var command = source.CreateCommand("""
            INSERT INTO tf_mailbox_seen_messages(tenant_id, project_id, environment_id, app_id, workspace_id, configuration_id, folder, message_id, seen_at)
            VALUES(@tenant, @project, @environment, @app, @workspace, @config, @folder, @message_id, @seen_at)
            ON CONFLICT (seen_key) DO NOTHING
            """);
        AddScope(command, scope);
        AddText(command, "config", configurationId);
        AddText(command, "folder", folder);
        AddText(command, "message_id", messageId);
        command.Parameters.Add(new NpgsqlParameter("seen_at", NpgsqlDbType.TimestampTz) { Value = seenAt.ToUniversalTime() });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> HasSeenMessageIdAsync(TicketScope scope, string configurationId, string folder, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(messageId);
        await using var command = source.CreateCommand($"""
            SELECT 1 FROM tf_mailbox_seen_messages
            WHERE seen_key = tf_key_v1({ScopeArgs}, @config, @folder, @message_id)
              AND {ScopeWhere} AND configuration_id=@config AND folder=@folder AND message_id=@message_id
            """);
        AddScope(command, scope);
        AddText(command, "config", configurationId);
        AddText(command, "folder", folder);
        AddText(command, "message_id", messageId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<int> PruneSeenMessageIdsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_mailbox_seen_messages WHERE seen_key IN (
                SELECT seen_key FROM tf_mailbox_seen_messages WHERE seen_at < @before ORDER BY seen_at LIMIT @limit)
            """);
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.TimestampTz) { Value = olderThan.ToUniversalTime() });
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });
}
