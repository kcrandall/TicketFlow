using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string CalendarColumns = "calendar_id, format_version, name, time_zone, schedule, revision";
    private const string CalendarKey = "calendar_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@calendar) AND " + WorkspaceWhere + " AND calendar_id=@calendar";
    private const string CalendarRevision = $"SELECT revision FROM tf_workspace_calendars WHERE {CalendarKey}";

    public async Task<StoredBusinessCalendar?> GetAsync(TicketScope scope, BusinessCalendarId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireCalendar(id);
        await using var command = source.CreateCommand($"SELECT {CalendarColumns} FROM tf_workspace_calendars WHERE {CalendarKey}");
        BindCalendarKey(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<WorkspaceWriteResult<StoredBusinessCalendar>> CreateAsync(TicketScope scope, BusinessCalendar calendar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(calendar);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "calendars", scope, cancellationToken).ConfigureAwait(false);
        if (await RevisionAsync(connection, transaction, CalendarRevision, command => BindCalendarKey(command, scope, calendar.Id), cancellationToken).ConfigureAwait(false) is { } existing)
            return new WorkspaceWriteConflict(existing);
        if (await CountAsync(connection, transaction, "tf_workspace_calendars", scope, cancellationToken).ConfigureAwait(false) >= WorkspaceLimits.MaximumCalendarsPerWorkspace)
            return new WorkspaceWriteRejected(WorkspaceWriteRejection.CapacityExceeded);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_calendars(tenant_id, project_id, environment_id, app_id, workspace_id, calendar_id, format_version, name, time_zone, schedule, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @calendar, 1, @name, @zone, @schedule, 1)
            """, connection, transaction))
        {
            BindCalendar(insert, scope, calendar);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredBusinessCalendar>(new(scope, calendar, 1));
    }

    public async Task<WorkspaceWriteResult<StoredBusinessCalendar>> UpdateAsync(TicketScope scope, BusinessCalendar calendar, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"UPDATE tf_workspace_calendars SET name=@name, time_zone=@zone, schedule=@schedule, revision=revision+1 WHERE {CalendarKey} AND revision=@expected RETURNING revision", connection, transaction))
        {
            BindCalendar(update, scope, calendar);
            update.Parameters.AddWithValue("expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new WorkspaceWriteApplied<StoredBusinessCalendar>(new(scope, calendar, revision));
            }
        }
        return await MissAsync<StoredBusinessCalendar>(connection, transaction, CalendarRevision, command => BindCalendarKey(command, scope, calendar.Id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceWriteResult<StoredBusinessCalendar>> DeleteAsync(TicketScope scope, BusinessCalendarId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireCalendar(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        StoredBusinessCalendar? removed = null;
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_workspace_calendars WHERE {CalendarKey} AND revision=@expected RETURNING {CalendarColumns}", connection, transaction))
        {
            BindCalendarKey(delete, scope, id);
            delete.Parameters.AddWithValue("expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                removed = ReadCalendar(reader, scope);
        }
        if (removed is null)
            return await MissAsync<StoredBusinessCalendar>(connection, transaction, CalendarRevision, command => BindCalendarKey(command, scope, id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredBusinessCalendar>(removed);
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

    private static StoredBusinessCalendar ReadCalendar(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported calendar format version.");
        var calendar = BusinessCalendarCodec.DecodeSchedule(new BusinessCalendarId(reader.GetString(0)), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        return new StoredBusinessCalendar(scope, calendar, reader.GetInt64(5));
    }, "business calendar");
}
