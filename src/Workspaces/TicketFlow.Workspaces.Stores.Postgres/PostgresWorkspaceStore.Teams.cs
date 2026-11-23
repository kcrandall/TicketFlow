using System.Data;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces.Stores.Postgres;

public sealed partial class PostgresWorkspaceStore
{
    private const string TeamKey = "team_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@team) AND " + WorkspaceWhere + " AND team_id=@team";
    private const string AliasedTeamKey =
        "t.team_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@team) AND t.tenant_id=@tenant AND t.project_id=@project AND t.environment_id=@environment AND t.app_id=@app AND t.workspace_id=@workspace AND t.team_id=@team";
    private const string TeamRevision = $"SELECT revision FROM tf_workspace_teams WHERE {TeamKey}";

    // One statement reads the team and its members from the same snapshot.
    private const string TeamSelect = """
        SELECT t.team_id, t.format_version, t.name, t.description, t.member_count, t.revision,
            ARRAY(SELECT m.user_id FROM tf_workspace_team_members m WHERE m.team_key=t.team_key AND m.team_id=t.team_id
                AND m.tenant_id=t.tenant_id AND m.project_id=t.project_id AND m.environment_id=t.environment_id AND m.app_id=t.app_id AND m.workspace_id=t.workspace_id
                ORDER BY m.user_id COLLATE "C")
        FROM tf_workspace_teams t
        """;

    public async Task<StoredTeam?> GetAsync(TicketScope scope, TeamId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireTeam(id);
        await using var command = source.CreateCommand($"{TeamSelect} WHERE {AliasedTeamKey}");
        BindTeamKey(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTeam(reader, scope) : null;
    }

    Task<WorkspacePage<TeamSummary>> ITeamStore.ListAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken) =>
        ListTeamsAsync(scope, filter, page, cancellationToken);

    /// <summary>Team summaries matching <paramref name="filter"/> over <see cref="WorkspaceResources.Team"/>, in identifier order.</summary>
    public async Task<WorkspacePage<TeamSummary>> ListTeamsAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        await using var command = source.CreateCommand();
        command.CommandText = FilteredQuery("tf_workspace_teams", "team_id", "r.team_id, r.format_version, r.name, r.description, r.member_count, r.revision", "NULL::text AS inbox_id", filter, page.After is not null, command);
        AddScope(command, scope);
        AddPage(command, page);
        var rows = new List<TeamSummary>(Math.Min(page.Limit + 1, 256));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(ReadTeamSummary(reader, scope));
        return Page(rows, page);
    }

    public async Task<IReadOnlyList<TeamId>> ListTeamsForUserAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        await using var command = source.CreateCommand($"""
            SELECT team_id FROM tf_workspace_team_members
            WHERE user_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@user) AND {WorkspaceWhere} AND user_id=@user
            ORDER BY team_id COLLATE "C" LIMIT {WorkspaceLimits.MaximumTeamsPerUser + 1}
            """);
        AddScope(command, scope);
        AddText(command, "user", user.Value);
        var teams = new List<TeamId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                teams.Add(new TeamId(reader.GetString(0)));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
            {
                throw StoredDataError(error, "team membership");
            }
        }
        if (teams.Count > WorkspaceLimits.MaximumTeamsPerUser)
            throw new InvalidOperationException("A stored team membership set exceeds the per-user limit.");
        return teams;
    }

    public async Task<IReadOnlyDictionary<UserId, IReadOnlyList<TeamId>>> ListTeamsForUsersAsync(TicketScope scope, IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(users);
        var buckets = new Dictionary<UserId, List<TeamId>>(users.Count);
        var ids = new List<string>(users.Count);
        foreach (var user in users)
        {
            if (user == default)
                throw new ArgumentException("User identifiers must be initialized.", nameof(users));
            if (buckets.TryAdd(user, []))
                ids.Add(user.Value);
        }
        if (ids.Count == 0)
            return new Dictionary<UserId, IReadOnlyList<TeamId>>();

        await using var command = source.CreateCommand("""
            SELECT m.user_id, m.team_id
            FROM unnest(@users::text[]) AS u(user_id)
            JOIN tf_workspace_team_members m
              ON m.user_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace,u.user_id)
             AND m.user_id = u.user_id
            WHERE m.tenant_id=@tenant AND m.project_id=@project AND m.environment_id=@environment AND m.app_id=@app AND m.workspace_id=@workspace
            ORDER BY m.user_id COLLATE "C", m.team_id COLLATE "C"
            """);
        AddScope(command, scope);
        AddTextArray(command, "users", ids.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            UserId userId;
            TeamId teamId;
            try
            {
                userId = new UserId(reader.GetString(0));
                teamId = new TeamId(reader.GetString(1));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
            {
                throw StoredDataError(error, "team membership");
            }
            if (buckets.TryGetValue(userId, out var list))
            {
                list.Add(teamId);
                if (list.Count > WorkspaceLimits.MaximumTeamsPerUser)
                    throw new InvalidOperationException("A stored team membership set exceeds the per-user limit.");
            }
        }
        var result = new Dictionary<UserId, IReadOnlyList<TeamId>>(buckets.Count);
        foreach (var (user, list) in buckets)
            result[user] = list;
        return result;
    }

    public async Task<WorkspaceWriteResult<StoredTeam>> CreateAsync(TicketScope scope, Team team, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(team);
        var users = MemberArray(team);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "teams", scope, cancellationToken).ConfigureAwait(false);
        await using (var check = new NpgsqlCommand($"""
            SELECT (SELECT revision FROM tf_workspace_teams WHERE {TeamKey}),
                   (SELECT count(*) FROM tf_workspace_teams WHERE {ScopeKeyWhere})
            """, connection, transaction))
        {
            BindTeamKey(check, scope, team.Id);
            await using var reader = await check.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.IsDBNull(0))
                return new WorkspaceWriteConflict(reader.GetInt64(0));
            if (reader.GetInt64(1) >= WorkspaceLimits.MaximumTeamsPerWorkspace)
                return new WorkspaceWriteRejected(WorkspaceWriteRejection.CapacityExceeded);
        }
        if (users.Length > 0 && await ExceedsMembershipLimitAsync(connection, transaction, scope, team.Id, users, onlyAdded: false, cancellationToken).ConfigureAwait(false))
            return new WorkspaceWriteRejected(WorkspaceWriteRejection.TeamMembershipLimitExceeded);
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            var insert = new NpgsqlBatchCommand("""
                INSERT INTO tf_workspace_teams(tenant_id, project_id, environment_id, app_id, workspace_id, team_id, format_version, name, description, member_count, revision)
                VALUES(@tenant, @project, @environment, @app, @workspace, @team, 1, @name, @description, @member_count, 1)
                """);
            BindTeam(insert, scope, team);
            batch.BatchCommands.Add(insert);
            if (users.Length > 0)
            {
                var insertMembers = new NpgsqlBatchCommand("""
                    INSERT INTO tf_workspace_team_members(tenant_id, project_id, environment_id, app_id, workspace_id, team_id, user_id)
                    SELECT @tenant, @project, @environment, @app, @workspace, @team, u FROM unnest(@users::text[]) AS u
                    """);
                BindTeamKey(insertMembers, scope, team.Id);
                AddTextArray(insertMembers, "users", users);
                batch.BatchCommands.Add(insertMembers);
            }
            await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredTeam>(new(scope, team, 1));
    }

    public async Task<WorkspaceWriteResult<StoredTeam>> UpdateAsync(TicketScope scope, Team team, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(team);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var users = MemberArray(team);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "teams", scope, cancellationToken).ConfigureAwait(false);
        var current = await RevisionAsync(connection, transaction, TeamRevision + " FOR UPDATE", command => BindTeamKey(command, scope, team.Id), cancellationToken).ConfigureAwait(false);
        if (current is null)
            return WorkspaceWriteNotFound.Instance;
        if (current != expectedRevision)
            return new WorkspaceWriteConflict(current.Value);
        if (users.Length > 0 && await ExceedsMembershipLimitAsync(connection, transaction, scope, team.Id, users, onlyAdded: true, cancellationToken).ConfigureAwait(false))
            return new WorkspaceWriteRejected(WorkspaceWriteRejection.TeamMembershipLimitExceeded);

        long revision;
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            var update = new NpgsqlBatchCommand($"""
                UPDATE tf_workspace_teams SET name=@name, description=@description, member_count=@member_count, revision=revision+1
                WHERE {TeamKey} AND revision=@expected RETURNING revision
                """);
            BindTeam(update, scope, team);
            AddLong(update, "expected", expectedRevision);
            batch.BatchCommands.Add(update);

            if (users.Length == 0)
            {
                var clear = new NpgsqlBatchCommand($"DELETE FROM tf_workspace_team_members WHERE {TeamKey}");
                BindTeamKey(clear, scope, team.Id);
                batch.BatchCommands.Add(clear);
            }
            else
            {
                var remove = new NpgsqlBatchCommand($"DELETE FROM tf_workspace_team_members WHERE {TeamKey} AND NOT (user_id = ANY(@users::text[]))");
                BindTeamKey(remove, scope, team.Id);
                AddTextArray(remove, "users", users);
                batch.BatchCommands.Add(remove);

                var add = new NpgsqlBatchCommand("""
                    INSERT INTO tf_workspace_team_members(tenant_id, project_id, environment_id, app_id, workspace_id, team_id, user_id)
                    SELECT @tenant, @project, @environment, @app, @workspace, @team, u FROM unnest(@users::text[]) AS u
                    ON CONFLICT (member_key) DO NOTHING
                    """);
                BindTeamKey(add, scope, team.Id);
                AddTextArray(add, "users", users);
                batch.BatchCommands.Add(add);
            }

            revision = (long)(await batch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredTeam>(new(scope, team, revision));
    }

    public async Task<WorkspaceWriteResult<StoredTeam>> DeleteAsync(TicketScope scope, TeamId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireTeam(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        StoredTeam? existing = null;
        await using (var read = new NpgsqlCommand($"{TeamSelect} WHERE {AliasedTeamKey} FOR UPDATE OF t", connection, transaction))
        {
            BindTeamKey(read, scope, id);
            await using var reader = await read.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                existing = ReadTeam(reader, scope);
        }
        if (existing is null)
            return WorkspaceWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new WorkspaceWriteConflict(existing.Revision);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_workspace_teams WHERE {TeamKey} AND revision=@expected", connection, transaction))
        {
            BindTeamKey(delete, scope, id);
            AddLong(delete, "expected", expectedRevision);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredTeam>(existing);
    }

    /// <summary>True when any member (or newly added member) already belongs to the per-user limit of other teams in the scope. Runs under the team lock.</summary>
    private static async Task<bool> ExceedsMembershipLimitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, TeamId teamId, string[] users, bool onlyAdded, CancellationToken cancellationToken)
    {
        var sql = onlyAdded
            ? $"""
                WITH added AS (
                    SELECT u FROM unnest(@users::text[]) AS u
                    WHERE NOT EXISTS (
                        SELECT 1 FROM tf_workspace_team_members cur
                        WHERE cur.member_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace,@team,u)
                          AND cur.tenant_id=@tenant AND cur.project_id=@project AND cur.environment_id=@environment AND cur.app_id=@app AND cur.workspace_id=@workspace
                          AND cur.team_id=@team AND cur.user_id=u)
                )
                SELECT EXISTS (
                    SELECT 1 FROM added a
                    JOIN tf_workspace_team_members m
                      ON m.user_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace,a.u)
                     AND m.user_id = a.u
                    WHERE m.tenant_id=@tenant AND m.project_id=@project AND m.environment_id=@environment AND m.app_id=@app AND m.workspace_id=@workspace
                      AND m.team_id <> @team
                    GROUP BY m.user_id HAVING count(*) >= {WorkspaceLimits.MaximumTeamsPerUser})
                """
            : $"""
                SELECT EXISTS (
                    SELECT 1 FROM unnest(@users::text[]) AS a(u)
                    JOIN tf_workspace_team_members m
                      ON m.user_key = tf_key_v1(@tenant,@project,@environment,@app,@workspace,a.u)
                     AND m.user_id = a.u
                    WHERE m.tenant_id=@tenant AND m.project_id=@project AND m.environment_id=@environment AND m.app_id=@app AND m.workspace_id=@workspace
                      AND m.team_id <> @team
                    GROUP BY m.user_id HAVING count(*) >= {WorkspaceLimits.MaximumTeamsPerUser})
                """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        BindTeamKey(command, scope, teamId);
        AddTextArray(command, "users", users);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static string[] MemberArray(Team team)
    {
        if (team.Members.Count == 0)
            return [];
        var users = new string[team.Members.Count];
        var index = 0;
        foreach (var member in team.Members)
            users[index++] = member.Value;
        return users;
    }

    private static void RequireTeam(TeamId id)
    {
        if (id == default)
            throw new ArgumentException("A team identifier is required.", nameof(id));
    }

    private static void BindTeamKey(NpgsqlCommand command, TicketScope scope, TeamId id)
    {
        AddScope(command, scope);
        AddText(command, "team", id.Value);
    }

    private static void BindTeamKey(NpgsqlBatchCommand command, TicketScope scope, TeamId id)
    {
        AddScope(command, scope);
        AddText(command, "team", id.Value);
    }

    private static void BindTeam(NpgsqlBatchCommand command, TicketScope scope, Team team)
    {
        BindTeamKey(command, scope, team.Id);
        AddText(command, "name", team.Name);
        AddText(command, "description", team.Description);
        AddInt(command, "member_count", team.Members.Count);
    }

    private static TeamSummary ReadTeamSummary(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported team format version.");
            var team = new Team(new TeamId(reader.GetString(0)), reader.GetString(2), NullableText(reader, 3));
            return new TeamSummary(scope, team.Id, team.Name, team.Description, reader.GetInt32(4), reader.GetInt64(5));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "team");
        }
    }

    private static StoredTeam ReadTeam(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported team format version.");
            var rawMembers = reader.GetFieldValue<string[]>(6);
            if (rawMembers.Length != reader.GetInt32(4))
                throw new FormatException("The stored member count does not match the members.");
            UserId[]? members = null;
            if (rawMembers.Length > 0)
            {
                members = new UserId[rawMembers.Length];
                for (var i = 0; i < rawMembers.Length; i++)
                    members[i] = new UserId(rawMembers[i]);
            }
            var team = new Team(new TeamId(reader.GetString(0)), reader.GetString(2), NullableText(reader, 3), members);
            return new StoredTeam(scope, team, reader.GetInt64(5));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "team");
        }
    }
}
