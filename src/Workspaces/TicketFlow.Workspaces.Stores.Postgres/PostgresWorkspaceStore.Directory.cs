using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string ListWorkspacesSql =
        "SELECT tenant_id, project_id, environment_id, app_id, workspace_id, scope_key FROM tf_workspace_settings ORDER BY scope_key LIMIT @limit";

    private const string ListWorkspacesAfterSql =
        "SELECT tenant_id, project_id, environment_id, app_id, workspace_id, scope_key FROM tf_workspace_settings WHERE scope_key > @after ORDER BY scope_key LIMIT @limit";

    /// <summary>Workspaces with settings ordered by the primary key (<c>scope_key</c>); the token is the hex key of the page's last row.</summary>
    public async Task<WorkspaceScopePage> ListWorkspacesAsync(string? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IWorkspaceDirectory.MaximumPage);
        byte[]? cursor = null;
        if (after is not null)
        {
            try
            {
                cursor = Convert.FromHexString(after);
            }
            catch (FormatException error)
            {
                throw new ArgumentException("The workspace directory token is invalid.", nameof(after), error);
            }
        }
        await using var command = source.CreateCommand(cursor is null ? ListWorkspacesSql : ListWorkspacesAfterSql);
        if (cursor is not null)
            command.Parameters.Add(new NpgsqlParameter<byte[]>("after", NpgsqlDbType.Bytea) { TypedValue = cursor });
        AddInt(command, "limit", limit + 1);
        var scopes = new List<TicketScope>(Math.Min(limit, 256));
        byte[]? lastKey = null;
        var hasMore = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (scopes.Count == limit)
                {
                    hasMore = true;
                    break;
                }
                try
                {
                    scopes.Add(new TicketScope(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), new WorkspaceId(reader.GetString(4))));
                }
                catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
                {
                    throw StoredDataError(error, "workspace scope");
                }
                if (scopes.Count == limit)
                    lastKey = await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false);
            }
        }
        return new WorkspaceScopePage(scopes, hasMore ? Convert.ToHexStringLower(lastKey!) : null);
    }
}
