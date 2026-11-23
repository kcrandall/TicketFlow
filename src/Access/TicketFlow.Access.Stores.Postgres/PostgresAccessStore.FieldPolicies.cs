using System.Data;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Access.Stores.Postgres;

public sealed partial class PostgresAccessStore
{
    private const string PolicyColumns = "field, format_version, applies_to, unmasked_roles, reveal_permission, revision";
    private const string PolicyKey = "policy_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@field) AND " + WorkspaceWhere + " AND field=@field";

    public async Task<StoredAccessFieldPolicy?> GetAsync(TicketScope scope, FieldPath field, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireField(field);
        await using var command = source.CreateCommand($"SELECT {PolicyColumns} FROM tf_access_field_policies WHERE {PolicyKey}");
        BindPolicyKey(command, scope, field);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPolicy(reader, scope) : null;
    }

    Task<IReadOnlyList<StoredAccessFieldPolicy>> IAccessFieldPolicyStore.ListAsync(TicketScope scope, CancellationToken cancellationToken) =>
        ListFieldPoliciesAsync(scope, cancellationToken);

    /// <summary>Field policies in <paramref name="scope"/> ordered by field path.</summary>
    public async Task<IReadOnlyList<StoredAccessFieldPolicy>> ListFieldPoliciesAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {PolicyColumns} FROM tf_access_field_policies WHERE {ScopeKeyWhere} ORDER BY field COLLATE \"C\" LIMIT {AccessLimits.MaximumFieldPoliciesPerWorkspace}");
        AddScope(command, scope);
        var policies = new List<StoredAccessFieldPolicy>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            policies.Add(ReadPolicy(reader, scope));
        return policies;
    }

    public async Task<AccessWriteResult<StoredAccessFieldPolicy>> CreateAsync(TicketScope scope, AccessFieldPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken, "policies", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value).ConfigureAwait(false);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_access_field_policies WHERE {ScopeKeyWhere}", connection, transaction))
        {
            AddScope(count, scope);
            if (await CountAsync(count, cancellationToken).ConfigureAwait(false) >= AccessLimits.MaximumFieldPoliciesPerWorkspace)
            {
                await using var existing = new NpgsqlCommand($"SELECT revision FROM tf_access_field_policies WHERE {PolicyKey}", connection, transaction);
                BindPolicyKey(existing, scope, policy.Field);
                return await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision
                    ? new AccessWriteConflict(revision)
                    : new AccessWriteRejected(AccessWriteRejection.CapacityExceeded);
            }
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_access_field_policies(tenant_id, project_id, environment_id, app_id, workspace_id, field, format_version, applies_to, unmasked_roles, reveal_permission, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @field, 1, @applies_to, @unmasked_roles, @reveal, 1)
            ON CONFLICT DO NOTHING RETURNING revision
            """, connection, transaction))
        {
            BindPolicy(insert, scope, policy);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
                return await MissAsync<StoredAccessFieldPolicy>(connection, transaction, $"SELECT revision FROM tf_access_field_policies WHERE {PolicyKey}", command => BindPolicyKey(command, scope, policy.Field), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AccessWriteApplied<StoredAccessFieldPolicy>(new(scope, policy, 1));
    }

    public async Task<AccessWriteResult<StoredAccessFieldPolicy>> UpdateAsync(TicketScope scope, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_access_field_policies SET applies_to=@applies_to, unmasked_roles=@unmasked_roles, reveal_permission=@reveal, revision=revision+1
            WHERE {PolicyKey} AND revision=@expected RETURNING revision
            """, connection))
        {
            BindPolicy(update, scope, policy);
            AddLong(update, "expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new AccessWriteApplied<StoredAccessFieldPolicy>(new(scope, policy, revision));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_access_field_policies WHERE {PolicyKey}", connection);
        BindPolicyKey(current, scope, policy.Field);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new AccessWriteConflict(existing)
            : AccessWriteNotFound.Instance;
    }

    public async Task<AccessWriteResult<StoredAccessFieldPolicy>> DeleteAsync(TicketScope scope, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireField(field);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_access_field_policies WHERE {PolicyKey} AND revision=@expected RETURNING {PolicyColumns}", connection))
        {
            BindPolicyKey(delete, scope, field);
            AddLong(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new AccessWriteApplied<StoredAccessFieldPolicy>(ReadPolicy(reader, scope));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_access_field_policies WHERE {PolicyKey}", connection);
        BindPolicyKey(current, scope, field);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new AccessWriteConflict(existing)
            : AccessWriteNotFound.Instance;
    }

    private static void RequireField(FieldPath field)
    {
        if (!field.IsInitialized)
            throw new ArgumentException("A field path is required.", nameof(field));
    }

    private static void BindPolicyKey(NpgsqlCommand command, TicketScope scope, FieldPath field)
    {
        AddScope(command, scope);
        AddText(command, "field", field.Value);
    }

    private static void BindPolicy(NpgsqlCommand command, TicketScope scope, AccessFieldPolicy policy)
    {
        BindPolicyKey(command, scope, policy.Field);
        var appliesTo = new string[policy.AppliesTo.Count];
        var index = 0;
        foreach (var permission in policy.AppliesTo)
            appliesTo[index++] = permission.Value;
        Array.Sort(appliesTo, StringComparer.Ordinal);
        AddTextArray(command, "applies_to", appliesTo);

        var unmasked = new string[policy.UnmaskedRoles.Count];
        index = 0;
        foreach (var role in policy.UnmaskedRoles)
            unmasked[index++] = role.Value;
        Array.Sort(unmasked, StringComparer.Ordinal);
        AddTextArray(command, "unmasked_roles", unmasked);

        AddText(command, "reveal", policy.RevealPermission?.Value);
    }

    private static StoredAccessFieldPolicy ReadPolicy(NpgsqlDataReader reader, TicketScope scope, int offset = 0)
    {
        try
        {
            if (reader.GetInt16(offset + 1) != 1)
                throw new FormatException("Unsupported field policy format version.");
            var rawApplies = reader.GetFieldValue<string[]>(offset + 2);
            var applies = new Permission[rawApplies.Length];
            for (var i = 0; i < rawApplies.Length; i++)
                applies[i] = Permission.Parse(rawApplies[i]);
            var rawRoles = reader.GetFieldValue<string[]>(offset + 3);
            var unmasked = new RoleId[rawRoles.Length];
            for (var i = 0; i < rawRoles.Length; i++)
                unmasked[i] = new RoleId(rawRoles[i]);
            var policy = new AccessFieldPolicy(
                new FieldPath(reader.GetString(offset)),
                applies,
                unmasked,
                reader.IsDBNull(offset + 4) ? null : Permission.Parse(reader.GetString(offset + 4)));
            return new StoredAccessFieldPolicy(scope, policy, reader.GetInt64(offset + 5));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "field policy");
        }
    }
}
