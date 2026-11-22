using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Workspaces.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of every workspace store contract over an application-owned <see cref="NpgsqlDataSource"/>.
/// Tables live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is
/// parameterized; every key includes the full scope; updates and deletes are compare-and-set on revision; capacity and per-user
/// team limits are checked under a transaction-scoped advisory lock for the workspace. List queries translate the
/// <see cref="AccessFilter"/> with <see cref="PostgresAccessFilterTranslator"/>; untranslatable filters return no rows. Lists are
/// ordered by identifier with <c>COLLATE "C"</c> (UTF-8 byte order, equal to UTF-16 ordinal order for the Basic Multilingual Plane).
/// </summary>
public sealed partial class PostgresWorkspaceStore : IWorkspaceSettingsStore, IInboxStore, ITeamStore, IBusinessCalendarStore, IWorkspaceDirectory
{
    private const string WorkspaceWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + WorkspaceWhere;

    private const string AliasedScopeKeyWhere =
        "t.scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND t.tenant_id=@tenant AND t.project_id=@project AND t.environment_id=@environment AND t.app_id=@app AND t.workspace_id=@workspace";

    private readonly NpgsqlDataSource source;

    public PostgresWorkspaceStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Explicitly applies <see cref="PostgresWorkspaceMigrations.Catalog"/> through <see cref="PostgresMigrationRunner"/> (advisory-locked,
    /// checksum-verified, idempotent). Fails on unknown future versions or modified history. Never called implicitly.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresWorkspaceMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    /// <summary>Serializes writers for one workspace and table within the current transaction.</summary>
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, TicketScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        AddTextArray(command, "parts", ["ticketflow.workspaces", table, scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value]);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, TicketScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE {ScopeKeyWhere}", connection, transaction);
        AddScope(command, scope);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<long?> RevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        bind(command);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    /// <summary>Distinguishes a missed compare-and-set: not found, or conflict with the current revision.</summary>
    private static async Task<WorkspaceWriteResult<T>> MissAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        var current = await RevisionAsync(connection, transaction, sql, bind, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current is { } revision ? new WorkspaceWriteConflict(revision) : WorkspaceWriteNotFound.Instance;
    }

    /// <summary>
    /// Wraps <paramref name="table"/> rows of the bound scope in a derived table exposing the access columns a workspace
    /// resource lacks as NULL (no team/inbox/assignee, unknown labels and attributes), then applies the translated filter.
    /// </summary>
    private static string FilteredQuery(string table, string idColumn, string columns, string missingAccessColumns, AccessFilter filter, NpgsqlCommand command)
    {
        var translated = PostgresAccessFilterTranslator.Translate(filter, AccessColumns, "access_").OrDenyAll();
        foreach (var parameter in translated.Parameters)
            command.Parameters.Add(parameter);
        return $"""
            SELECT {columns} FROM (SELECT t.*, {missingAccessColumns}, NULL::text AS assignee_id, NULL::text[] AS labels, NULL::jsonb AS attributes
                FROM {table} t WHERE {AliasedScopeKeyWhere}) AS r
            WHERE ({translated.Sql}) AND (@after::text IS NULL OR r.{idColumn} COLLATE "C" > @after::text)
            ORDER BY r.{idColumn} COLLATE "C" LIMIT @limit
            """;
    }

    private static readonly PostgresAccessFilterColumns AccessColumns = new()
    {
        TenantId = "r.tenant_id",
        ProjectId = "r.project_id",
        EnvironmentId = "r.environment_id",
        AppId = "r.app_id",
        WorkspaceId = "r.workspace_id",
        TeamId = "r.team_id",
        InboxId = "r.inbox_id",
        AssigneeId = "r.assignee_id",
        Labels = "r.labels",
        Attributes = "r.attributes",
    };

    private static void AddPage(NpgsqlCommand command, WorkspacePageRequest page)
    {
        AddText(command, "after", page.After);
        command.Parameters.AddWithValue("limit", page.Limit + 1);
    }

    private static WorkspacePage<T> Page<T>(List<T> rows, WorkspacePageRequest page) =>
        rows.Count > page.Limit ? new(rows[..page.Limit], true) : new(rows, false);

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException($"A stored {description} is invalid or incompatible.", error);
        }
    }
}
