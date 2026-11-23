using System.Data;
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
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
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
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_access_roles SET name=@name, permissions=@permissions, revision=revision+1
            WHERE {RoleKey} AND revision=@expected RETURNING revision
            """, connection))
        {
            BindRole(update, scope, role);
            AddLong(update, "expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new AccessWriteApplied<StoredAccessRole>(new(scope, role, revision));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_access_roles WHERE {RoleKey}", connection);
        BindRoleKey(current, scope, role.Id);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new AccessWriteConflict(existing)
            : AccessWriteNotFound.Instance;
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
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_access_roles WHERE {RoleKey} AND revision=@expected RETURNING {RoleColumns}", connection))
        {
            BindRoleKey(delete, scope, id);
            AddLong(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new AccessWriteApplied<StoredAccessRole>(ReadRole(reader, scope));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_access_roles WHERE {RoleKey}", connection);
        BindRoleKey(current, scope, id);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new AccessWriteConflict(existing)
            : AccessWriteNotFound.Instance;
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
        var permissions = new string[role.Permissions.Count];
        var index = 0;
        foreach (var permission in role.Permissions)
            permissions[index++] = permission.Value;
        Array.Sort(permissions, StringComparer.Ordinal);
        AddTextArray(command, "permissions", permissions);
    }

    private static StoredAccessRole ReadRole(NpgsqlDataReader reader, TicketScope scope, int offset = 0)
    {
        try
        {
            if (reader.GetInt16(offset + 1) != 1)
                throw new FormatException("Unsupported role format version.");
            var raw = reader.GetFieldValue<string[]>(offset + 3);
            var parsed = new Permission[raw.Length];
            for (var i = 0; i < raw.Length; i++)
                parsed[i] = Permission.Parse(raw[i]);
            var role = new AccessRole(new RoleId(reader.GetString(offset)), reader.GetString(offset + 2), parsed);
            return new StoredAccessRole(scope, role, reader.GetInt64(offset + 4));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "access role");
        }
    }
}
