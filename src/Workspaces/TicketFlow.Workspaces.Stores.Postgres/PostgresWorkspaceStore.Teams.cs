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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
        command.CommandText = FilteredQuery("tf_workspace_teams", "team_id", "r.team_id, r.format_version, r.name, r.description, r.member_count, r.revision", "NULL::text AS inbox_id", filter, command);
        AddScope(command, scope);
        AddPage(command, page);
        var rows = new List<TeamSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Decode(() =>
            {
                if (reader.GetInt16(1) != 1)
                    throw new FormatException("Unsupported team format version.");
                var team = new Team(new TeamId(reader.GetString(0)), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
                return new TeamSummary(scope, team.Id, team.Name, team.Description, reader.GetInt32(4), reader.GetInt64(5));
            }, "team"));
        }
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
            teams.Add(Decode(() => new TeamId(reader.GetString(0)), "team membership"));
        if (teams.Count > WorkspaceLimits.MaximumTeamsPerUser)
            throw new InvalidOperationException("A stored team membership set exceeds the per-user limit.");
        return teams;
    }

    public async Task<WorkspaceWriteResult<StoredTeam>> CreateAsync(TicketScope scope, Team team, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(team);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "teams", scope, cancellationToken).ConfigureAwait(false);
        if (await RevisionAsync(connection, transaction, TeamRevision, command => BindTeamKey(command, scope, team.Id), cancellationToken).ConfigureAwait(false) is { } existing)
            return new WorkspaceWriteConflict(existing);
        if (await CountAsync(connection, transaction, "tf_workspace_teams", scope, cancellationToken).ConfigureAwait(false) >= WorkspaceLimits.MaximumTeamsPerWorkspace)
            return new WorkspaceWriteRejected(WorkspaceWriteRejection.CapacityExceeded);
        if (await ExceedsMembershipLimitAsync(connection, transaction, scope, team, cancellationToken).ConfigureAwait(false))
            return new WorkspaceWriteRejected(WorkspaceWriteRejection.TeamMembershipLimitExceeded);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_teams(tenant_id, project_id, environment_id, app_id, workspace_id, team_id, format_version, name, description, member_count, revision)
            VALUES(@tenant, @project, @environment, @app, @workspace, @team, 1, @name, @description, @member_count, 1)
            """, connection, transaction))
        {
            BindTeam(insert, scope, team);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertMembersAsync(connection, transaction, scope, team, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredTeam>(new(scope, team, 1));
    }

    public async Task<WorkspaceWriteResult<StoredTeam>> UpdateAsync(TicketScope scope, Team team, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(team);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "teams", scope, cancellationToken).ConfigureAwait(false);
        var current = await RevisionAsync(connection, transaction, TeamRevision + " FOR UPDATE", command => BindTeamKey(command, scope, team.Id), cancellationToken).ConfigureAwait(false);
        if (current is null)
            return WorkspaceWriteNotFound.Instance;
        if (current != expectedRevision)
            return new WorkspaceWriteConflict(current.Value);
        if (await ExceedsMembershipLimitAsync(connection, transaction, scope, team, cancellationToken).ConfigureAwait(false))
            return new WorkspaceWriteRejected(WorkspaceWriteRejection.TeamMembershipLimitExceeded);
        long revision;
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_workspace_teams SET name=@name, description=@description, member_count=@member_count, revision=revision+1
            WHERE {TeamKey} AND revision=@expected RETURNING revision
            """, connection, transaction))
        {
            BindTeam(update, scope, team);
            update.Parameters.AddWithValue("expected", expectedRevision);
            revision = (long)(await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        await using (var clear = new NpgsqlCommand($"DELETE FROM tf_workspace_team_members WHERE {TeamKey}", connection, transaction))
        {
            BindTeamKey(clear, scope, team.Id);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertMembersAsync(connection, transaction, scope, team, cancellationToken).ConfigureAwait(false);
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
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
            delete.Parameters.AddWithValue("expected", expectedRevision);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceWriteApplied<StoredTeam>(existing);
    }

    /// <summary>True when any member already belongs to the per-user limit of other teams in the scope. Runs under the team lock.</summary>
    private static async Task<bool> ExceedsMembershipLimitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Team team, CancellationToken cancellationToken)
    {
        if (team.Members.Count == 0)
            return false;
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1 FROM tf_workspace_team_members
                WHERE user_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,u) FROM unnest(@users::text[]) AS u)
                  AND {WorkspaceWhere} AND user_id = ANY(@users::text[]) AND team_id <> @team
                GROUP BY user_id HAVING count(*) >= {WorkspaceLimits.MaximumTeamsPerUser})
            """, connection, transaction);
        BindTeamKey(command, scope, team.Id);
        AddTextArray(command, "users", team.Members.Select(member => member.Value));
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task InsertMembersAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Team team, CancellationToken cancellationToken)
    {
        if (team.Members.Count == 0)
            return;
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tf_workspace_team_members(tenant_id, project_id, environment_id, app_id, workspace_id, team_id, user_id)
            SELECT @tenant, @project, @environment, @app, @workspace, @team, u FROM unnest(@users::text[]) AS u
            """, connection, transaction);
        BindTeamKey(insert, scope, team.Id);
        AddTextArray(insert, "users", team.Members.Select(member => member.Value));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static void BindTeam(NpgsqlCommand command, TicketScope scope, Team team)
    {
        BindTeamKey(command, scope, team.Id);
        AddText(command, "name", team.Name);
        AddText(command, "description", team.Description);
        command.Parameters.AddWithValue("member_count", team.Members.Count);
    }

    private static StoredTeam ReadTeam(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported team format version.");
        var members = reader.GetFieldValue<string[]>(6);
        if (members.Length != reader.GetInt32(4))
            throw new FormatException("The stored member count does not match the members.");
        var team = new Team(new TeamId(reader.GetString(0)), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), members.Select(member => new UserId(member)));
        return new StoredTeam(scope, team, reader.GetInt64(5));
    }, "team");
}
