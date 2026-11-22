using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
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
        await using var command = source.CreateCommand("""
            SELECT tenant_id, project_id, environment_id, app_id, workspace_id, scope_key FROM tf_workspace_settings
            WHERE (@after::bytea IS NULL OR scope_key > @after::bytea) ORDER BY scope_key LIMIT @limit
            """);
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Bytea) { Value = (object?)cursor ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit + 1);
        var rows = new List<(TicketScope Scope, byte[] Key)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add((Decode(() => new TicketScope(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), new WorkspaceId(reader.GetString(4))),
                    "workspace scope"), reader.GetFieldValue<byte[]>(5)));
        var page = rows.Take(limit).ToList();
        return new WorkspaceScopePage([.. page.Select(row => row.Scope)], rows.Count > limit ? Convert.ToHexStringLower(page[^1].Key) : null);
    }
}
