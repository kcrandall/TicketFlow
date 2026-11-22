using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.Workspaces.WorkspaceGuards;

namespace TicketFlow.Workspaces;

/// <summary>
/// Default <see cref="ITeamService"/>. Every call authorizes through Access before touching the store; lists use the access query
/// planner. Member additions are checked against the team's allow grants so membership never escalates privileges.
/// </summary>
public sealed class TeamService : ITeamService
{
    private readonly ITeamStore teams;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;
    private readonly TeamGrantGuard guard;

    public TeamService(ITeamStore teams, IAccessEvaluator evaluator, IAccessQueryPlanner planner, IAccessGrantStore grants, IAccessRoleStore roles, IPermissionCatalog catalog, TimeProvider time)
    {
        this.teams = teams ?? throw new ArgumentNullException(nameof(teams));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(time);
        guard = new(grants, roles, catalog, evaluator, time);
    }

    public async Task<WorkspaceResult<StoredTeam>> GetAsync(PrincipalRef actor, TeamId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        if (await AuthorizeAsync(evaluator, actor, WorkspacePermissions.TeamRead, WorkspaceResources.Team(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await teams.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } team
            ? new WorkspaceSucceeded<StoredTeam>(team)
            : NotFound;
    }

    public async Task<WorkspaceResult<WorkspacePage<TeamSummary>>> ListAsync(PrincipalRef actor, WorkspacePageRequest? page = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var filter = await planner.PlanAsync(new AccessQuery(actor, WorkspacePermissions.TeamRead, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new WorkspaceSucceeded<WorkspacePage<TeamSummary>>(new([], false));
        return new WorkspaceSucceeded<WorkspacePage<TeamSummary>>(await teams.ListAsync(actor.Scope, filter, page ?? new(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorkspaceResult<StoredTeam>> CreateAsync(PrincipalRef actor, Team team, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(team);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.TeamManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false);
        // Grants may already name the team identifier, so initial members are checked like later additions.
        if (refused is null && team.Members.Count > 0)
            refused = await guard.CheckAsync(actor, team.Id, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        return Map(await teams.CreateAsync(actor.Scope, team, cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorkspaceResult<StoredTeam>> UpdateAsync(PrincipalRef actor, Team team, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(team);
        return await ReplaceAsync(actor, team.Id, expectedRevision, _ => team, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceResult<StoredTeam>> DeleteAsync(PrincipalRef actor, TeamId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.TeamManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        return Map(await teams.DeleteAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public Task<WorkspaceResult<StoredTeam>> AddMemberAsync(PrincipalRef actor, TeamId team, UserId member, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(team);
        RequireMember(member);
        return ReplaceAsync(actor, team, expectedRevision, current => current.Members.Contains(member) ? null : current.WithMembers(current.Members.Append(member)), cancellationToken);
    }

    public Task<WorkspaceResult<StoredTeam>> RemoveMemberAsync(PrincipalRef actor, TeamId team, UserId member, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(team);
        RequireMember(member);
        return ReplaceAsync(actor, team, expectedRevision, current => current.Members.Contains(member) ? current.WithMembers(current.Members.Where(user => user != member)) : null, cancellationToken);
    }

    /// <summary>
    /// Authorizes <c>team:manage</c> on the team, fences on the loaded revision, checks added members against the team's grants and
    /// writes with compare-and-set. A <see langword="null"/> replacement is a no-op that returns the current state unchanged.
    /// </summary>
    private async Task<WorkspaceResult<StoredTeam>> ReplaceAsync(PrincipalRef actor, TeamId id, long expectedRevision, Func<Team, Team?> replace, CancellationToken cancellationToken)
    {
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.TeamManage, WorkspaceResources.Team(actor.Scope, id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        var current = await teams.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (Fence(current?.Revision, expectedRevision) is { } fenced)
            return fenced;
        var replacement = replace(current!.Team);
        if (replacement is null)
            return new WorkspaceSucceeded<StoredTeam>(current);
        if (replacement.Members.Any(member => !current.Team.Members.Contains(member))
            && await guard.CheckAsync(actor, id, cancellationToken).ConfigureAwait(false) is { } escalation)
            return escalation;
        return Map(await teams.UpdateAsync(actor.Scope, replacement, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private static void RequireId(TeamId id)
    {
        if (id == default)
            throw new ArgumentException("A team identifier is required.", nameof(id));
    }

    private static void RequireMember(UserId member)
    {
        if (member == default)
            throw new ArgumentException("A member identifier is required.", nameof(member));
    }
}

/// <summary>Default <see cref="ITeamMembershipResolver"/> over <see cref="ITeamStore.ListTeamsForUserAsync"/>.</summary>
public sealed class StoreTeamMembershipResolver(ITeamStore teams) : ITeamMembershipResolver
{
    private readonly ITeamStore teams = teams ?? throw new ArgumentNullException(nameof(teams));

    public async Task<IReadOnlyList<TeamId>> GetTeamsAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        var result = await teams.ListTeamsForUserAsync(scope, user, cancellationToken).ConfigureAwait(false);
        if (result.Count > WorkspaceLimits.MaximumTeamsPerUser)
            throw new InvalidOperationException("The team store returned more memberships than its contract allows.");
        return result;
    }
}
