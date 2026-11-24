using System.Data;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Access.Stores.Postgres;

public sealed partial class PostgresAccessStore
{
    private const string GrantColumns =
        "grant_id, format_version, subject_kind, subject_id, role_id, scope_level, workspace_id, team_id, inbox_id, effect, grant_kind, condition::text, valid_from, valid_until, justification, revision";

    private const string AliasedGrantColumns =
        "g.grant_id, g.format_version, g.subject_kind, g.subject_id, g.role_id, g.scope_level, g.workspace_id, g.team_id, g.inbox_id, g.effect, g.grant_kind, g.condition::text, g.valid_from, g.valid_until, g.justification, g.revision";

    private const string GrantKey = "grant_key=tf_key_v1(@tenant,@project,@environment,@app,@grant) AND " + ScopeWhere + " AND grant_id=@grant";

    private const string ListForSubjectsSql = $"""
        SELECT {AliasedGrantColumns}
        FROM (SELECT DISTINCT kind, id FROM unnest(@kinds::smallint[], @ids::text[]) AS s(kind, id)) AS w
        JOIN tf_access_grants g
          ON g.subject_key = tf_key_v1(@tenant, @project, @environment, @app, w.kind::text, w.id)
         AND g.subject_kind = w.kind AND g.subject_id = w.id
        WHERE g.tenant_id=@tenant AND g.project_id=@project AND g.environment_id=@environment AND g.app_id=@app
          AND (g.workspace_id IS NULL OR g.workspace_id=@workspace)
        ORDER BY g.grant_id COLLATE "C"
        """;

    private const string ListForTargetSql = $"""
        SELECT {GrantColumns} FROM tf_access_grants
        WHERE target_key=tf_key_v1(@tenant, @project, @environment, @app, @workspace, @scope_level::text, coalesce(@team, @inbox, ''))
          AND {ScopeWhere} AND scope_level=@scope_level AND workspace_id=@workspace
          AND team_id IS NOT DISTINCT FROM @team AND inbox_id IS NOT DISTINCT FROM @inbox
        ORDER BY grant_id COLLATE "C" LIMIT @limit
        """;

    private const string ListForTargetAfterSql = $"""
        SELECT {GrantColumns} FROM tf_access_grants
        WHERE target_key=tf_key_v1(@tenant, @project, @environment, @app, @workspace, @scope_level::text, coalesce(@team, @inbox, ''))
          AND {ScopeWhere} AND scope_level=@scope_level AND workspace_id=@workspace
          AND team_id IS NOT DISTINCT FROM @team AND inbox_id IS NOT DISTINCT FROM @inbox
          AND grant_id COLLATE "C" > @after
        ORDER BY grant_id COLLATE "C" LIMIT @limit
        """;

    public async Task<StoredAccessGrant?> GetAsync(AccessPartition partition, GrantId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        RequireGrantId(id);
        await using var command = source.CreateCommand($"SELECT {GrantColumns} FROM tf_access_grants WHERE {GrantKey}");
        BindGrantKey(command, partition, id);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
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
        if (subjects.Count == 0)
            return [];
        var kinds = new short[subjects.Count];
        var ids = new string[subjects.Count];
        var index = 0;
        foreach (var subject in subjects)
        {
            var (kind, id) = EncodeSubject(subject);
            kinds[index] = kind;
            ids[index] = id;
            index++;
        }
        await using var command = source.CreateCommand(ListForSubjectsSql);
        AddPartition(command, partition);
        AddText(command, "workspace", workspace.Value);
        command.Parameters.Add(new NpgsqlParameter<short[]>("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = kinds });
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
        await using var command = source.CreateCommand(after is null ? ListForTargetSql : ListForTargetAfterSql);
        AddPartition(command, partition);
        AddShort(command, "scope_level", level);
        AddText(command, "workspace", workspace);
        AddText(command, "team", team);
        AddText(command, "inbox", inbox);
        if (after is { } cursor)
            AddText(command, "after", cursor.Value);
        AddInt(command, "limit", limit);
        var result = new List<StoredAccessGrant>(Math.Min(limit, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadGrant(reader, partition));
        return result;
    }

    public async Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default)
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
                ? new StoreConflict(revision)
                : new StoreRejected<AccessWriteRejection>(AccessWriteRejection.CapacityExceeded);
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
        return new StoreApplied<StoredAccessGrant>(new(grant, 1));
    }

    public async Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Fast path: 1-RTT atomic CAS when subject is unchanged (no advisory lock needed).
        await using (var fast = new NpgsqlCommand($"""
            UPDATE tf_access_grants SET role_id=@role, scope_level=@scope_level,
                workspace_id=@workspace, team_id=@team, inbox_id=@inbox, effect=@effect, grant_kind=@kind, condition=@condition,
                valid_from=@valid_from, valid_until=@valid_until, justification=@justification, revision=revision+1
            WHERE {GrantKey} AND revision=@expected AND subject_kind=@subject_kind AND subject_id=@subject_id
            RETURNING revision
            """, connection))
        {
            BindGrant(fast, grant);
            AddLong(fast, "expected", expectedRevision);
            if (await fast.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long fastRevision)
                return new StoreApplied<StoredAccessGrant>(new(grant, fastRevision));
        }

        // Fresh snapshot check: distinguish not-found / revision conflict / subject change.
        var (kind, subject) = EncodeSubject(grant.Subject);
        await using (var probe = new NpgsqlCommand($"SELECT revision, subject_kind=@subject_kind AND subject_id=@subject_id FROM tf_access_grants WHERE {GrantKey}", connection))
        {
            BindGrantKey(probe, grant.Partition, grant.Id);
            AddShort(probe, "subject_kind", kind);
            AddText(probe, "subject_id", subject);
            await using var reader = await probe.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return StoreNotFound.Instance;
            var probedRevision = reader.GetInt64(0);
            if (probedRevision != expectedRevision)
                return new StoreConflict(probedRevision);
            if (reader.GetBoolean(1))
                return new StoreConflict(probedRevision);
        }

        // Subject changed at matching revision: run subject-locked capacity-checked update.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockSubjectAsync(connection, transaction, grant.Partition, kind, subject, cancellationToken).ConfigureAwait(false);
        long current;
        bool sameSubject;
        await using (var read = new NpgsqlCommand($"SELECT revision, subject_kind=@subject_kind AND subject_id=@subject_id FROM tf_access_grants WHERE {GrantKey} FOR UPDATE", connection, transaction))
        {
            BindGrantKey(read, grant.Partition, grant.Id);
            AddShort(read, "subject_kind", kind);
            AddText(read, "subject_id", subject);
            await using var reader = await read.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return StoreNotFound.Instance;
            current = reader.GetInt64(0);
            sameSubject = reader.GetBoolean(1);
        }
        if (current != expectedRevision)
            return new StoreConflict(current);
        if (!sameSubject && await CountSubjectAsync(connection, transaction, grant.Partition, kind, subject, grant.Id, cancellationToken).ConfigureAwait(false) >= AccessLimits.MaximumGrantsPerSubject)
            return new StoreRejected<AccessWriteRejection>(AccessWriteRejection.CapacityExceeded);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_access_grants SET subject_kind=@subject_kind, subject_id=@subject_id, role_id=@role, scope_level=@scope_level,
                workspace_id=@workspace, team_id=@team, inbox_id=@inbox, effect=@effect, grant_kind=@kind, condition=@condition,
                valid_from=@valid_from, valid_until=@valid_until, justification=@justification, revision=revision+1
            WHERE {GrantKey} AND revision=@expected
            RETURNING revision
            """, connection, transaction))
        {
            BindGrant(update, grant);
            AddLong(update, "expected", expectedRevision);
            var revision = (long)(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoreApplied<StoredAccessGrant>(new(grant, revision));
        }
    }

    public async Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        RequireGrantId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_access_grants WHERE {GrantKey} AND revision=@expected RETURNING {GrantColumns}", connection))
        {
            BindGrantKey(delete, partition, id);
            AddLong(delete, "expected", expectedRevision);
            await using var reader = await delete.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredAccessGrant>(ReadGrant(reader, partition));
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_access_grants WHERE {GrantKey}", connection);
        BindGrantKey(current, partition, id);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StoreConflict(existing)
            : StoreNotFound.Instance;
    }

    public async Task<IReadOnlyList<StoredAccessGrant>> DeleteManyAsync(AccessPartition partition, IReadOnlyList<(GrantId Id, long ExpectedRevision)> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            return [];
        var ids = new string[targets.Count];
        var revisions = new long[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            RequireGrantId(targets[i].Id);
            ArgumentOutOfRangeException.ThrowIfLessThan(targets[i].ExpectedRevision, 1);
            ids[i] = targets[i].Id.Value;
            revisions[i] = targets[i].ExpectedRevision;
        }
        await using var command = source.CreateCommand($"""
            DELETE FROM tf_access_grants g
            USING unnest(@grants::text[], @revisions::bigint[]) AS req(grant_id, expected_revision)
            WHERE g.grant_key = tf_key_v1(@tenant, @project, @environment, @app, req.grant_id)
              AND g.tenant_id=@tenant AND g.project_id=@project AND g.environment_id=@environment AND g.app_id=@app
              AND g.grant_id = req.grant_id AND g.revision = req.expected_revision
            RETURNING {AliasedGrantColumns}
            """);
        AddPartition(command, partition);
        AddTextArray(command, "grants", ids);
        command.Parameters.Add(new NpgsqlParameter<long[]>("revisions", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = revisions });
        var removed = new List<StoredAccessGrant>(targets.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            removed.Add(ReadGrant(reader, partition));
        return removed;
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
        AddShort(count, "subject_kind", kind);
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
        AddShort(command, "subject_kind", kind);
        AddText(command, "subject_id", subject);
        AddText(command, "role", grant.Role.Value);
        var (level, workspace, team, inbox) = grant.Scope switch
        {
            TenantGrantScope => ((short)1, (string?)null, (string?)null, (string?)null),
            WorkspaceGrantScope scope => ((short)2, scope.Workspace.Value, null, null),
            TeamGrantScope scope => ((short)3, scope.Workspace.Value, scope.Team.Value, null),
            InboxGrantScope scope => ((short)4, scope.Workspace.Value, null, scope.Inbox.Value),
        };
        AddShort(command, "scope_level", level);
        AddText(command, "workspace", workspace);
        AddText(command, "team", team);
        AddText(command, "inbox", inbox);
        AddShort(command, "effect", (short)grant.Effect);
        AddShort(command, "kind", (short)grant.Kind);
        if (grant.Condition is null)
            command.Parameters.Add(new NpgsqlParameter("condition", NpgsqlDbType.Jsonb) { Value = DBNull.Value });
        else
            command.Parameters.Add(new NpgsqlParameter<string>("condition", NpgsqlDbType.Jsonb) { TypedValue = AccessConditionCodec.Serialize(grant.Condition) });
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

    private static StoredAccessGrant ReadGrant(NpgsqlDataReader reader, AccessPartition partition, int offset = 0)
    {
        try
        {
            if (reader.GetInt16(offset + 1) != 1)
                throw new FormatException("Unsupported grant format version.");
            var subjectId = reader.GetString(offset + 3);
            GrantSubject subject = reader.GetInt16(offset + 2) switch
            {
                1 => new UserId(subjectId),
                2 => new TeamId(subjectId),
                3 => new ApiClientId(subjectId),
                _ => throw new FormatException("Unknown subject discriminator."),
            };
            GrantScope scope = reader.GetInt16(offset + 5) switch
            {
                1 => TenantGrantScope.Instance,
                2 => new WorkspaceGrantScope(new WorkspaceId(reader.NullableText(offset + 6)!)),
                3 => new TeamGrantScope(new WorkspaceId(reader.NullableText(offset + 6)!), new TeamId(reader.NullableText(offset + 7)!)),
                4 => new InboxGrantScope(new WorkspaceId(reader.NullableText(offset + 6)!), new InboxId(reader.NullableText(offset + 8)!)),
                _ => throw new FormatException("Unknown scope discriminator."),
            };
            var effect = (GrantEffect)reader.GetInt16(offset + 9);
            var kind = (GrantKind)reader.GetInt16(offset + 10);
            var condition = reader.NullableText(offset + 11) is { } json ? AccessConditionCodec.Deserialize(json) : null;
            var grant = new AccessGrant(new GrantId(reader.GetString(offset)), partition, subject, new RoleId(reader.GetString(offset + 4)), scope, effect, condition,
                reader.NullableInstant(offset + 12), reader.NullableInstant(offset + 13), reader.NullableText(offset + 14), kind);
            return new StoredAccessGrant(grant, reader.GetInt64(offset + 15));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "access grant");
        }
    }
}
