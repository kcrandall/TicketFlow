using System.Data;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string InboxColumns =
        "inbox_id, format_version, name, channel_kind, channel_config_id, calendar_id, sla_policy_id, routing_policy_id, enabled, greeting_message, away_message, revision";

    private const string AliasedInboxColumns =
        "r.inbox_id, r.format_version, r.name, r.channel_kind, r.channel_config_id, r.calendar_id, r.sla_policy_id, r.routing_policy_id, r.enabled, r.greeting_message, r.away_message, r.revision";

    private const string InboxKey = "inbox_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@inbox) AND " + WorkspaceWhere + " AND inbox_id=@inbox";
    private const string InboxRevision = $"SELECT revision FROM tf_workspace_inboxes WHERE {InboxKey}";

    public async Task<StoredInbox?> GetAsync(TicketScope scope, InboxId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInbox(id);
        await using var command = source.CreateCommand($"SELECT {InboxColumns} FROM tf_workspace_inboxes WHERE {InboxKey}");
        BindInboxKey(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
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
        command.CommandText = FilteredQuery("tf_workspace_inboxes", "inbox_id", AliasedInboxColumns, "NULL::text AS team_id", filter, page.After is not null, command);
        AddScope(command, scope);
        AddPage(command, page);
        var rows = new List<StoredInbox>(Math.Min(page.Limit + 1, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadInbox(reader, scope));
        return Page(rows, page);
    }

    public async Task<StoreWriteResult<StoredInbox, WorkspaceWriteRejection>> CreateAsync(TicketScope scope, Inbox inbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inbox);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "inboxes", scope, cancellationToken).ConfigureAwait(false);
        await using (var check = new NpgsqlCommand($"""
            SELECT (SELECT revision FROM tf_workspace_inboxes WHERE {InboxKey}),
                   (SELECT count(*) FROM tf_workspace_inboxes WHERE {ScopeKeyWhere})
            """, connection, transaction))
        {
            BindInboxKey(check, scope, inbox.Id);
            await using var reader = await check.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.IsDBNull(0))
                return new StoreConflict(reader.GetInt64(0));
            if (reader.GetInt64(1) >= WorkspaceLimits.MaximumInboxesPerWorkspace)
                return new StoreRejected<WorkspaceWriteRejection>(WorkspaceWriteRejection.CapacityExceeded);
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_inboxes(tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id, format_version, name, channel_kind,
                channel_config_id, calendar_id, sla_policy_id, routing_policy_id, enabled, greeting_message, away_message, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @inbox, 1, @name, @kind, @config, @calendar, @sla, @routing, @enabled, @greeting, @away, 1)
            """, connection, transaction))
        {
            BindInbox(insert, scope, inbox);
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName == BindingIndex)
            {
                return new StoreRejected<WorkspaceWriteRejection>(WorkspaceWriteRejection.ChannelBindingInUse);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<StoredInbox>(new(scope, inbox, 1));
    }

    public async Task<StoreWriteResult<StoredInbox, WorkspaceWriteRejection>> UpdateAsync(TicketScope scope, Inbox inbox, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_workspace_inboxes SET name=@name, channel_kind=@kind, channel_config_id=@config, calendar_id=@calendar, sla_policy_id=@sla,
                routing_policy_id=@routing, enabled=@enabled, greeting_message=@greeting, away_message=@away, revision=revision+1
            WHERE {InboxKey} AND revision=@expected RETURNING revision
            """, connection))
        {
            BindInbox(update, scope, inbox);
            AddLong(update, "expected", expectedRevision);
            try
            {
                if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                    return new StoreApplied<StoredInbox>(new(scope, inbox, revision));
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation && error.ConstraintName == BindingIndex)
            {
                return new StoreRejected<WorkspaceWriteRejection>(WorkspaceWriteRejection.ChannelBindingInUse);
            }
        }
        await using var current = new NpgsqlCommand(InboxRevision, connection);
        BindInboxKey(current, scope, inbox.Id);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StoreConflict(existing)
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredInbox, WorkspaceWriteRejection>> DeleteAsync(TicketScope scope, InboxId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInbox(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_workspace_inboxes WHERE {InboxKey} AND revision=@expected RETURNING {InboxColumns}", connection))
        {
            BindInboxKey(delete, scope, id);
            AddLong(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredInbox>(ReadInbox(reader, scope));
        }
        await using var current = new NpgsqlCommand(InboxRevision, connection);
        BindInboxKey(current, scope, id);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StoreConflict(existing)
            : StoreNotFound.Instance;
    }

    private const string BindingIndex = "tf_workspace_inboxes_channel_binding";

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
        AddBool(command, "enabled", inbox.Enabled);
        AddText(command, "greeting", inbox.GreetingMessage);
        AddText(command, "away", inbox.AwayMessage);
    }

    private static StoredInbox ReadInbox(NpgsqlDataReader reader, TicketScope scope, int offset = 0)
    {
        try
        {
            if (reader.GetInt16(offset + 1) != 1)
                throw new FormatException("Unsupported inbox format version.");
            var inbox = new Inbox(
                new InboxId(reader.GetString(offset)),
                reader.GetString(offset + 2),
                new ChannelBinding(new ChannelKind(reader.GetString(offset + 3)), reader.GetString(offset + 4)),
                reader.NullableText(offset + 5) is { } calendar ? new BusinessCalendarId(calendar) : null,
                reader.NullableText(offset + 6) is { } sla ? new SlaPolicyId(sla) : null,
                reader.NullableText(offset + 7) is { } routing ? new RoutingPolicyId(routing) : null,
                reader.GetBoolean(offset + 8),
                reader.NullableText(offset + 9),
                reader.NullableText(offset + 10));
            return new StoredInbox(scope, inbox, reader.GetInt64(offset + 11));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "inbox");
        }
    }
}
