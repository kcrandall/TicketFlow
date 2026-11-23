using System.Data;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string CalendarColumns = "calendar_id, format_version, name, time_zone, schedule, revision";
    private const string CalendarKey = "calendar_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@calendar) AND " + WorkspaceWhere + " AND calendar_id=@calendar";

    public async Task<StoredBusinessCalendar?> GetAsync(TicketScope scope, BusinessCalendarId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireCalendar(id);
        await using var command = source.CreateCommand($"SELECT {CalendarColumns} FROM tf_workspace_calendars WHERE {CalendarKey}");
        BindCalendarKey(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadCalendar(reader, scope) : null;
    }

    Task<IReadOnlyList<StoredBusinessCalendar>> IBusinessCalendarStore.ListAsync(TicketScope scope, CancellationToken cancellationToken) =>
        ListCalendarsAsync(scope, cancellationToken);

    /// <summary>Calendars in <paramref name="scope"/> ordered by identifier.</summary>
    public async Task<IReadOnlyList<StoredBusinessCalendar>> ListCalendarsAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {CalendarColumns} FROM tf_workspace_calendars WHERE {ScopeKeyWhere} ORDER BY calendar_id COLLATE \"C\" LIMIT {WorkspaceLimits.MaximumCalendarsPerWorkspace}");
        AddScope(command, scope);
        var calendars = new List<StoredBusinessCalendar>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            calendars.Add(ReadCalendar(reader, scope));
        return calendars;
    }

    public async Task<StoreWriteResult<StoredBusinessCalendar, WorkspaceWriteRejection>> CreateAsync(TicketScope scope, BusinessCalendar calendar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(calendar);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "calendars", scope, cancellationToken).ConfigureAwait(false);
        await using (var check = new NpgsqlCommand($"""
            SELECT (SELECT revision FROM tf_workspace_calendars WHERE {CalendarKey}),
                   (SELECT count(*) FROM tf_workspace_calendars WHERE {ScopeKeyWhere})
            """, connection, transaction))
        {
            BindCalendarKey(check, scope, calendar.Id);
            await using var reader = await check.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.IsDBNull(0))
                return new StoreConflict(reader.GetInt64(0));
            if (reader.GetInt64(1) >= WorkspaceLimits.MaximumCalendarsPerWorkspace)
                return new StoreRejected<WorkspaceWriteRejection>(WorkspaceWriteRejection.CapacityExceeded);
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_calendars(tenant_id, project_id, environment_id, app_id, workspace_id, calendar_id, format_version, name, time_zone, schedule, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @calendar, 1, @name, @zone, @schedule, 1)
            """, connection, transaction))
        {
            BindCalendar(insert, scope, calendar);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<StoredBusinessCalendar>(new(scope, calendar, 1));
    }

    public async Task<StoreWriteResult<StoredBusinessCalendar, WorkspaceWriteRejection>> UpdateAsync(TicketScope scope, BusinessCalendar calendar, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_workspace_calendars SET name=@name, time_zone=@zone, schedule=@schedule, revision=revision+1
            WHERE {CalendarKey} AND revision=@expected RETURNING revision
            """, connection))
        {
            BindCalendar(update, scope, calendar);
            AddLong(update, "expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new StoreApplied<StoredBusinessCalendar>(new(scope, calendar, revision));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_workspace_calendars WHERE {CalendarKey}", connection);
        BindCalendarKey(current, scope, calendar.Id);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StoreConflict(existing)
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredBusinessCalendar, WorkspaceWriteRejection>> DeleteAsync(TicketScope scope, BusinessCalendarId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireCalendar(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_workspace_calendars WHERE {CalendarKey} AND revision=@expected RETURNING {CalendarColumns}", connection))
        {
            BindCalendarKey(delete, scope, id);
            AddLong(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredBusinessCalendar>(ReadCalendar(reader, scope));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_workspace_calendars WHERE {CalendarKey}", connection);
        BindCalendarKey(current, scope, id);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StoreConflict(existing)
            : StoreNotFound.Instance;
    }

    private static void RequireCalendar(BusinessCalendarId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A calendar identifier is required.", nameof(id));
    }

    private static void BindCalendarKey(NpgsqlCommand command, TicketScope scope, BusinessCalendarId id)
    {
        AddScope(command, scope);
        AddText(command, "calendar", id.Value);
    }

    private static void BindCalendar(NpgsqlCommand command, TicketScope scope, BusinessCalendar calendar)
    {
        BindCalendarKey(command, scope, calendar.Id);
        AddText(command, "name", calendar.Name);
        AddText(command, "zone", calendar.TimeZoneId);
        AddText(command, "schedule", BusinessCalendarCodec.EncodeSchedule(calendar));
    }

    private static StoredBusinessCalendar ReadCalendar(NpgsqlDataReader reader, TicketScope scope, int offset = 0)
    {
        try
        {
            if (reader.GetInt16(offset + 1) != 1)
                throw new FormatException("Unsupported calendar format version.");
            var calendar = BusinessCalendarCodec.DecodeSchedule(new BusinessCalendarId(reader.GetString(offset)), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4));
            return new StoredBusinessCalendar(scope, calendar, reader.GetInt64(offset + 5));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "business calendar");
        }
    }
}
