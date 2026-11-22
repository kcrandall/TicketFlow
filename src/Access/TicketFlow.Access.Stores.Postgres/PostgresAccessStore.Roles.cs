using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Access.Stores.Postgres;

public sealed partial class PostgresAccessStore
{
    private const string RoleKey = "role_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@role) AND " + WorkspaceWhere + " AND role_id=@role";
    private const string RoleColumns = "role_id, format_version, name, permissions, revision";

    public async Task<StoredAccessRole?> GetAsync(TicketScope scope, RoleId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!id.IsInitialized)
            throw new ArgumentException("A role identifier is required.", nameof(id));
        await using var command = source.CreateCommand($"SELECT {RoleColumns} FROM tf_access_roles WHERE {RoleKey}");
        AddScope(command, scope);
        AddText(command, "role", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRole(reader, scope) : null;
    }

    public async Task<IReadOnlyList<StoredAccessRole>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {RoleColumns} FROM tf_access_roles WHERE {ScopeKeyWhere} ORDER BY role_id COLLATE \"C\" LIMIT {AccessLimits.MaximumCustomRolesPerWorkspace}");
        AddScope(command, scope);
        var roles = new List<StoredAccessRole>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            roles.Add(ReadRole(reader, scope));
        return roles;
    }

    public async Task<AccessWriteResult<StoredAccessRole>> CreateAsync(TicketScope scope, AccessRole role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(role);
        if (role.IsBuiltIn || BuiltInRoles.IsBuiltIn(role.Id))
            return new AccessWriteRejected(AccessWriteRejection.BuiltInRoleImmutable);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken, "roles", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value).ConfigureAwait(false);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_access_roles WHERE {ScopeKeyWhere}", connection, transaction))
        {
            AddScope(count, scope);
            if (await CountAsync(count, cancellationToken).ConfigureAwait(false) >= AccessLimits.MaximumCustomRolesPerWorkspace)
            {
                await using var existing = new NpgsqlCommand($"SELECT revision FROM tf_access_roles WHERE {RoleKey}", connection, transaction);
                BindRoleKey(existing, scope, role.Id);
                if (await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                    return new AccessWriteConflict(revision);
                return new AccessWriteRejected(AccessWriteRejection.CapacityExceeded);
            }
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_access_roles(tenant_id, project_id, environment_id, app_id, workspace_id, role_id, format_version, name, permissions, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @role, 1, @name, @permissions, 1)
            ON CONFLICT DO NOTHING RETURNING revision
            """, connection, transaction))
        {
            BindRole(insert, scope, role);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
                return await MissAsync<StoredAccessRole>(connection, transaction, $"SELECT revision FROM tf_access_roles WHERE {RoleKey}", command => BindRoleKey(command, scope, role.Id), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AccessWriteApplied<StoredAccessRole>(new(scope, role, 1));
    }

    public async Task<AccessWriteResult<StoredAccessRole>> UpdateAsync(TicketScope scope, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (role.IsBuiltIn || BuiltInRoles.IsBuiltIn(role.Id))
            return new AccessWriteRejected(AccessWriteRejection.BuiltInRoleImmutable);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"UPDATE tf_access_roles SET name=@name, permissions=@permissions, revision=revision+1 WHERE {RoleKey} AND revision=@expected RETURNING revision", connection, transaction))
        {
            BindRole(update, scope, role);
            update.Parameters.AddWithValue("expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AccessWriteApplied<StoredAccessRole>(new(scope, role, revision));
            }
        }
        return await MissAsync<StoredAccessRole>(connection, transaction, $"SELECT revision FROM tf_access_roles WHERE {RoleKey}", command => BindRoleKey(command, scope, role.Id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccessWriteResult<StoredAccessRole>> DeleteAsync(TicketScope scope, RoleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!id.IsInitialized)
            throw new ArgumentException("A role identifier is required.", nameof(id));
        if (BuiltInRoles.IsBuiltIn(id))
            return new AccessWriteRejected(AccessWriteRejection.BuiltInRoleImmutable);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_access_roles WHERE {RoleKey} AND revision=@expected RETURNING {RoleColumns}", connection, transaction))
        {
            AddScope(delete, scope);
            AddText(delete, "role", id.Value);
            delete.Parameters.AddWithValue("expected", expectedRevision);
            StoredAccessRole? removed = null;
            await using (var reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    removed = ReadRole(reader, scope);
            if (removed is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AccessWriteApplied<StoredAccessRole>(removed);
            }
        }
        return await MissAsync<StoredAccessRole>(connection, transaction, $"SELECT revision FROM tf_access_roles WHERE {RoleKey}", command => BindRoleKey(command, scope, id), cancellationToken).ConfigureAwait(false);
    }

    private static void BindRoleKey(NpgsqlCommand command, TicketScope scope, RoleId id)
    {
        AddScope(command, scope);
        AddText(command, "role", id.Value);
    }

    private static void BindRole(NpgsqlCommand command, TicketScope scope, AccessRole role)
    {
        BindRoleKey(command, scope, role.Id);
        AddText(command, "name", role.Name);
        AddTextArray(command, "permissions", role.Permissions.Select(permission => permission.Value).Order(StringComparer.Ordinal));
    }

    private static StoredAccessRole ReadRole(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported role format version.");
        var role = new AccessRole(new RoleId(reader.GetString(0)), reader.GetString(2), reader.GetFieldValue<string[]>(3).Select(Permission.Parse));
        return new StoredAccessRole(scope, role, reader.GetInt64(4));
    }, "access role");
}
