using Npgsql;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string InboxColumns =
        "inbox_id, format_version, name, channel_kind, channel_config_id, calendar_id, sla_policy_id, routing_policy_id, enabled, greeting_message, away_message, revision";

    private const string InboxKey = "inbox_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@inbox) AND " + WorkspaceWhere + " AND inbox_id=@inbox";
    private const string InboxRevision = $"SELECT revision FROM tf_workspace_inboxes WHERE {InboxKey}";

    public async Task<StoredInbox?> GetAsync(TicketScope scope, InboxId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInbox(id);
        await using var command = source.CreateCommand($"SELECT {InboxColumns} FROM tf_workspace_inboxes WHERE {InboxKey}");
        BindInboxKey(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadInbox(reader, scope) : null;
    }

    Task<WorkspacePage<StoredInbox>> IInboxStore.ListAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken) =>
        ListInboxesAsync(scope, filter, page, cancellationToken);

    /// <summary>Inboxes matching <paramref name="filter"/> over <see cref="WorkspaceResources.Inbox"/>, in identifier order.</summary>
    public async Task<WorkspacePage<StoredInbox>> ListInboxesAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        await using var command = source.CreateCommand();
        command.CommandText = FilteredQuery("tf_workspace_inboxes", "inbox_id", string.Join(", ", InboxColumns.Split(", ").Select(column => "r." + column)), "NULL::text AS team_id", filter, command);
        AddScope(command, scope);
        AddPage(command, page);
        var rows = new List<StoredInbox>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadInbox(reader, scope));
        return Page(rows, page);
    }

    public async Task<WorkspaceWriteResult<StoredInbox>> CreateAsync(TicketScope scope, Inbox inbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inbox);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "inboxes", scope, cancellationToken).ConfigureAwait(false);
        if (await RevisionAsync(connection, transaction, InboxRevision, command => BindInboxKey(command, scope, inbox.Id), cancellationToken).ConfigureAwait(false) is { } existing)
            return new WorkspaceWriteConflict(existing);
        if (await CountAsync(connection, transaction, "tf_workspace_inboxes", scope, cancellationToken).ConfigureAwait(false) >= WorkspaceLimits.MaximumInboxesPerWorkspace)
            return new WorkspaceWriteRejected(WorkspaceWriteRejection.CapacityExceeded);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_inboxes(tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id, format_version, name, channel_kind,
                channel_config_id, calendar_id, sla_policy_id, routing_policy_id, enabled, greeting_message, away_message, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @inbox, 1, @name, @kind, @config, @calendar, @sla, @routing, @enabled, @greeting, @away, 1)
            """, connection, transaction))
        {
            BindInbox(insert, scope, inbox);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredInbox>(new(scope, inbox, 1));
    }

    public async Task<WorkspaceWriteResult<StoredInbox>> UpdateAsync(TicketScope scope, Inbox inbox, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_workspace_inboxes SET name=@name, channel_kind=@kind, channel_config_id=@config, calendar_id=@calendar, sla_policy_id=@sla,
                routing_policy_id=@routing, enabled=@enabled, greeting_message=@greeting, away_message=@away, revision=revision+1
            WHERE {InboxKey} AND revision=@expected RETURNING revision
            """, connection, transaction))
        {
            BindInbox(update, scope, inbox);
            update.Parameters.AddWithValue("expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new WorkspaceWriteApplied<StoredInbox>(new(scope, inbox, revision));
            }
        }
        return await MissAsync<StoredInbox>(connection, transaction, InboxRevision, command => BindInboxKey(command, scope, inbox.Id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceWriteResult<StoredInbox>> DeleteAsync(TicketScope scope, InboxId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInbox(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        StoredInbox? removed = null;
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_workspace_inboxes WHERE {InboxKey} AND revision=@expected RETURNING {InboxColumns}", connection, transaction))
        {
            BindInboxKey(delete, scope, id);
            delete.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                removed = ReadInbox(reader, scope);
        }
        if (removed is null)
            return await MissAsync<StoredInbox>(connection, transaction, InboxRevision, command => BindInboxKey(command, scope, id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredInbox>(removed);
    }

    private static void RequireInbox(InboxId id)
    {
        if (id == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(id));
    }

    private static void BindInboxKey(NpgsqlCommand command, TicketScope scope, InboxId id)
    {
        AddScope(command, scope);
        AddText(command, "inbox", id.Value);
    }

    private static void BindInbox(NpgsqlCommand command, TicketScope scope, Inbox inbox)
    {
        BindInboxKey(command, scope, inbox.Id);
        AddText(command, "name", inbox.Name);
        AddText(command, "kind", inbox.Channel.Kind.Value);
        AddText(command, "config", inbox.Channel.ConfigurationId);
        AddText(command, "calendar", inbox.Calendar?.Value);
        AddText(command, "sla", inbox.SlaPolicy?.Value);
        AddText(command, "routing", inbox.RoutingPolicy?.Value);
        command.Parameters.AddWithValue("enabled", inbox.Enabled);
        AddText(command, "greeting", inbox.GreetingMessage);
        AddText(command, "away", inbox.AwayMessage);
    }

    private static StoredInbox ReadInbox(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported inbox format version.");
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        var inbox = new Inbox(
            new InboxId(reader.GetString(0)),
            reader.GetString(2),
            new ChannelBinding(new ChannelKind(reader.GetString(3)), reader.GetString(4)),
            Text(5) is { } calendar ? new BusinessCalendarId(calendar) : null,
            Text(6) is { } sla ? new SlaPolicyId(sla) : null,
            Text(7) is { } routing ? new RoutingPolicyId(routing) : null,
            reader.GetBoolean(8),
            Text(9),
            Text(10));
        return new StoredInbox(scope, inbox, reader.GetInt64(11));
    }, "inbox");
}
