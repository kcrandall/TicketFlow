using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Access.Stores.Postgres;

public sealed partial class PostgresAccessStore
{
    private const string GrantColumns =
        "grant_id, format_version, subject_kind, subject_id, role_id, scope_level, workspace_id, team_id, inbox_id, effect, grant_kind, condition::text, valid_from, valid_until, justification, revision";

    private const string GrantKey = "grant_key=tf_key_v1(@tenant,@project,@environment,@app,@grant) AND " + ScopeWhere + " AND grant_id=@grant";

    public async Task<StoredAccessGrant?> GetAsync(AccessPartition partition, GrantId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        RequireGrantId(id);
        await using var command = source.CreateCommand($"SELECT {GrantColumns} FROM tf_access_grants WHERE {GrantKey}");
        BindGrantKey(command, partition, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadGrant(reader, partition) : null;
    }

    public async Task<IReadOnlyList<StoredAccessGrant>> ListForSubjectsAsync(AccessPartition partition, WorkspaceId workspace, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(subjects);
        if (workspace == default)
            throw new ArgumentException("A workspace identifier is required.", nameof(workspace));
        if (subjects.Count > AccessLimits.MaximumSubjectsPerQuery)
            throw new ArgumentException($"At most {AccessLimits.MaximumSubjectsPerQuery} subjects can be queried.", nameof(subjects));
        var kinds = new List<short>(subjects.Count);
        var ids = new List<string>(subjects.Count);
        foreach (var subject in subjects)
        {
            var (kind, id) = EncodeSubject(subject);
            kinds.Add(kind);
            ids.Add(id);
        }
        await using var command = source.CreateCommand($"""
            WITH wanted AS (SELECT kind, id FROM unnest(@kinds::smallint[], @ids::text[]) AS s(kind, id))
            SELECT {GrantColumns} FROM tf_access_grants
            WHERE subject_key IN (SELECT tf_key_v1(@tenant, @project, @environment, @app, kind::text, id) FROM wanted)
              AND {ScopeWhere} AND (subject_kind, subject_id) IN (SELECT kind, id FROM wanted)
              AND (workspace_id IS NULL OR workspace_id=@workspace)
            ORDER BY grant_id COLLATE "C"
            """);
        AddPartition(command, partition);
        AddText(command, "workspace", workspace.Value);
        command.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = kinds.ToArray() });
        AddTextArray(command, "ids", ids);
        var grants = new List<StoredAccessGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            grants.Add(ReadGrant(reader, partition));
        return grants;
    }

    public async Task<IReadOnlyList<StoredAccessGrant>> ListForTargetAsync(AccessPartition partition, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        AccessGrantTargets.Validate(target, limit, after);
        var (level, workspace, team, inbox) = target switch
        {
            TenantGrantScope => throw new ArgumentException("Tenant scopes cannot be listed by target.", nameof(target)),
            WorkspaceGrantScope scope => ((short)2, scope.Workspace.Value, (string?)null, (string?)null),
            TeamGrantScope scope => ((short)3, scope.Workspace.Value, scope.Team.Value, null),
            InboxGrantScope scope => ((short)4, scope.Workspace.Value, null, scope.Inbox.Value),
        };
        await using var command = source.CreateCommand($"""
            SELECT {GrantColumns} FROM tf_access_grants
            WHERE target_key=tf_key_v1(@tenant, @project, @environment, @app, @workspace, @scope_level::text, coalesce(@team, @inbox, ''))
              AND {ScopeWhere} AND scope_level=@scope_level AND workspace_id=@workspace
              AND team_id IS NOT DISTINCT FROM @team AND inbox_id IS NOT DISTINCT FROM @inbox
              AND (@after::text IS NULL OR grant_id COLLATE "C" > @after::text)
            ORDER BY grant_id COLLATE "C" LIMIT @limit
            """);
        AddPartition(command, partition);
        command.Parameters.AddWithValue("scope_level", level);
        AddText(command, "workspace", workspace);
        AddText(command, "team", team);
        AddText(command, "inbox", inbox);
        AddText(command, "after", after?.Value);
        command.Parameters.AddWithValue("limit", limit);
        var result = new List<StoredAccessGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadGrant(reader, partition));
        return result;
    }

    public async Task<AccessWriteResult<StoredAccessGrant>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var (kind, subject) = EncodeSubject(grant.Subject);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockSubjectAsync(connection, transaction, grant.Partition, kind, subject, cancellationToken).ConfigureAwait(false);
        if (await CountSubjectAsync(connection, transaction, grant.Partition, kind, subject, grant.Id, cancellationToken).ConfigureAwait(false) >= AccessLimits.MaximumGrantsPerSubject)
        {
            await using var existing = new NpgsqlCommand($"SELECT revision FROM tf_access_grants WHERE {GrantKey}", connection, transaction);
            BindGrantKey(existing, grant.Partition, grant.Id);
            return await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision
                ? new AccessWriteConflict(revision)
                : new AccessWriteRejected(AccessWriteRejection.CapacityExceeded);
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_access_grants(tenant_id, project_id, environment_id, app_id, grant_id, format_version, subject_kind, subject_id, role_id,
                scope_level, workspace_id, team_id, inbox_id, effect, grant_kind, condition, valid_from, valid_until, justification, revision)
            VALUES(@tenant, @project, @environment, @app, @grant, 1, @subject_kind, @subject_id, @role,
                @scope_level, @workspace, @team, @inbox, @effect, @kind, @condition, @valid_from, @valid_until, @justification, 1)
            ON CONFLICT DO NOTHING RETURNING revision
            """, connection, transaction))
        {
            BindGrant(insert, grant);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long)
                return await MissAsync<StoredAccessGrant>(connection, transaction, $"SELECT revision FROM tf_access_grants WHERE {GrantKey}", command => BindGrantKey(command, grant.Partition, grant.Id), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AccessWriteApplied<StoredAccessGrant>(new(grant, 1));
    }

    public async Task<AccessWriteResult<StoredAccessGrant>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var (kind, subject) = EncodeSubject(grant.Subject);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockSubjectAsync(connection, transaction, grant.Partition, kind, subject, cancellationToken).ConfigureAwait(false);
        long current;
        bool sameSubject;
        await using (var read = new NpgsqlCommand($"SELECT revision, subject_kind=@subject_kind AND subject_id=@subject_id FROM tf_access_grants WHERE {GrantKey} FOR UPDATE", connection, transaction))
        {
            BindGrantKey(read, grant.Partition, grant.Id);
            read.Parameters.AddWithValue("subject_kind", kind);
            AddText(read, "subject_id", subject);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return AccessWriteNotFound.Instance;
            current = reader.GetInt64(0);
            sameSubject = reader.GetBoolean(1);
        }
        if (current != expectedRevision)
            return new AccessWriteConflict(current);
        if (!sameSubject && await CountSubjectAsync(connection, transaction, grant.Partition, kind, subject, grant.Id, cancellationToken).ConfigureAwait(false) >= AccessLimits.MaximumGrantsPerSubject)
            return new AccessWriteRejected(AccessWriteRejection.CapacityExceeded);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_access_grants SET subject_kind=@subject_kind, subject_id=@subject_id, role_id=@role, scope_level=@scope_level,
                workspace_id=@workspace, team_id=@team, inbox_id=@inbox, effect=@effect, grant_kind=@kind, condition=@condition,
                valid_from=@valid_from, valid_until=@valid_until, justification=@justification, revision=revision+1
            WHERE {GrantKey} AND revision=@expected
            RETURNING revision
            """, connection, transaction))
        {
            BindGrant(update, grant);
            update.Parameters.AddWithValue("expected", expectedRevision);
            var revision = (long)(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AccessWriteApplied<StoredAccessGrant>(new(grant, revision));
        }
    }

    public async Task<AccessWriteResult<StoredAccessGrant>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        RequireGrantId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_access_grants WHERE {GrantKey} AND revision=@expected RETURNING {GrantColumns}", connection, transaction))
        {
            BindGrantKey(delete, partition, id);
            delete.Parameters.AddWithValue("expected", expectedRevision);
            StoredAccessGrant? removed = null;
            await using (var reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    removed = ReadGrant(reader, partition);
            if (removed is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new AccessWriteApplied<StoredAccessGrant>(removed);
            }
        }
        return await MissAsync<StoredAccessGrant>(connection, transaction, $"SELECT revision FROM tf_access_grants WHERE {GrantKey}", command => BindGrantKey(command, partition, id), cancellationToken).ConfigureAwait(false);
    }

    private static void RequireGrantId(GrantId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A grant identifier is required.", nameof(id));
    }

    private static Task LockSubjectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AccessPartition partition, short kind, string subject, CancellationToken cancellationToken) =>
        LockAsync(connection, transaction, cancellationToken, "grants", partition.TenantId, partition.ProjectId, partition.EnvironmentId, partition.AppId, kind.ToString(System.Globalization.CultureInfo.InvariantCulture), subject);

    private static async Task<long> CountSubjectAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AccessPartition partition, short kind, string subject, GrantId except, CancellationToken cancellationToken)
    {
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM tf_access_grants WHERE subject_key=tf_key_v1(@tenant,@project,@environment,@app,@subject_kind::text,@subject_id) AND {ScopeWhere} AND subject_kind=@subject_kind AND subject_id=@subject_id AND grant_id<>@grant", connection, transaction);
        BindGrantKey(count, partition, except);
        count.Parameters.AddWithValue("subject_kind", kind);
        AddText(count, "subject_id", subject);
        return await CountAsync(count, cancellationToken).ConfigureAwait(false);
    }

    private static void BindGrantKey(NpgsqlCommand command, AccessPartition partition, GrantId id)
    {
        AddPartition(command, partition);
        AddText(command, "grant", id.Value);
    }

    private static void BindGrant(NpgsqlCommand command, AccessGrant grant)
    {
        BindGrantKey(command, grant.Partition, grant.Id);
        var (kind, subject) = EncodeSubject(grant.Subject);
        command.Parameters.AddWithValue("subject_kind", kind);
        AddText(command, "subject_id", subject);
        AddText(command, "role", grant.Role.Value);
        var (level, workspace, team, inbox) = grant.Scope switch
        {
            TenantGrantScope => ((short)1, (string?)null, (string?)null, (string?)null),
            WorkspaceGrantScope scope => ((short)2, scope.Workspace.Value, null, null),
            TeamGrantScope scope => ((short)3, scope.Workspace.Value, scope.Team.Value, null),
            InboxGrantScope scope => ((short)4, scope.Workspace.Value, null, scope.Inbox.Value),
        };
        command.Parameters.AddWithValue("scope_level", level);
        AddText(command, "workspace", workspace);
        AddText(command, "team", team);
        AddText(command, "inbox", inbox);
        command.Parameters.AddWithValue("effect", (short)grant.Effect);
        command.Parameters.AddWithValue("kind", (short)grant.Kind);
        command.Parameters.Add(new NpgsqlParameter("condition", NpgsqlDbType.Jsonb)
        {
            Value = grant.Condition is null ? DBNull.Value : AccessConditionCodec.Serialize(grant.Condition),
        });
        AddInstant(command, "valid_from", grant.ValidFrom);
        AddInstant(command, "valid_until", grant.ValidUntil);
        AddText(command, "justification", grant.Justification);
    }

    private static (short Kind, string Id) EncodeSubject(GrantSubject subject) => subject switch
    {
        UserId user => (1, user.Value),
        TeamId team => (2, team.Value),
        ApiClientId client => (3, client.Value),
    };

    private static StoredAccessGrant ReadGrant(NpgsqlDataReader reader, AccessPartition partition) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported grant format version.");
        var subjectId = reader.GetString(3);
        GrantSubject subject = reader.GetInt16(2) switch
        {
            1 => new UserId(subjectId),
            2 => new TeamId(subjectId),
            3 => new ApiClientId(subjectId),
            _ => throw new FormatException("Unknown subject discriminator."),
        };
        string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        DateTimeOffset? Instant(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
        GrantScope scope = reader.GetInt16(5) switch
        {
            1 => TenantGrantScope.Instance,
            2 => new WorkspaceGrantScope(new WorkspaceId(Text(6)!)),
            3 => new TeamGrantScope(new WorkspaceId(Text(6)!), new TeamId(Text(7)!)),
            4 => new InboxGrantScope(new WorkspaceId(Text(6)!), new InboxId(Text(8)!)),
            _ => throw new FormatException("Unknown scope discriminator."),
        };
        var effect = (GrantEffect)reader.GetInt16(9);
        var kind = (GrantKind)reader.GetInt16(10);
        var condition = Text(11) is { } json ? AccessConditionCodec.Deserialize(json) : null;
        var grant = new AccessGrant(new GrantId(reader.GetString(0)), partition, subject, new RoleId(reader.GetString(4)), scope, effect, condition,
            Instant(12), Instant(13), Text(14), kind);
        return new StoredAccessGrant(grant, reader.GetInt64(15));
    }, "access grant");
}
