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
    private readonly TeamGrantPolicy policy;

    public TeamService(ITeamStore teams, IAccessEvaluator evaluator, IAccessQueryPlanner planner, IAccessAdministrator administrator, IAccessGrantStore grants, TimeProvider time)
    {
        this.teams = teams ?? throw new ArgumentNullException(nameof(teams));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        ArgumentNullException.ThrowIfNull(administrator);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(time);
        policy = new(administrator, grants, time);
    }

    public async Task<TicketResult<StoredTeam>> GetAsync(PrincipalRef actor, TeamId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        if (await AuthorizeAsync(evaluator, actor, WorkspacePermissions.TeamRead, WorkspaceResources.Team(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await teams.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } team
            ? new TicketValue<StoredTeam>(team)
            : NotFound;
    }

    public async Task<TicketResult<WorkspacePage<TeamSummary>>> ListAsync(PrincipalRef actor, WorkspacePageRequest? page = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var filter = await planner.PlanAsync(new AccessQuery(actor, WorkspacePermissions.TeamRead, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new TicketValue<WorkspacePage<TeamSummary>>(new([], false));
        return new TicketValue<WorkspacePage<TeamSummary>>(await teams.ListAsync(actor.Scope, filter, page ?? new(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredTeam>> CreateAsync(PrincipalRef actor, Team team, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(team);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.TeamManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false);
        // Grants may already name the team identifier, so initial members are checked like later additions.
        refused ??= await policy.CheckAdditionsAsync(actor, team.Id, team.Members, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        return Map(await teams.CreateAsync(actor.Scope, team, cancellationToken).ConfigureAwait(false));
    }

    public Task<TicketResult<StoredTeam>> UpdateAsync(PrincipalRef actor, Team team, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(team);
        return ReplaceAsync(actor, team.Id, expectedRevision, _ => team, knownAdded: null, knownRemoved: null, cancellationToken);
    }

    /// <summary>
    /// Deletes the team and then revokes every grant whose subject is the team through <see cref="IAccessAdministrator"/>. Because the
    /// deletion revokes them, the actor needs the authority to revoke all of them (checked before anything is written); grants that
    /// change concurrently after the deletion are left in place.
    /// </summary>
    public async Task<TicketResult<StoredTeam>> DeleteAsync(PrincipalRef actor, TeamId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.TeamManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        var current = await teams.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (Fence(current?.Revision, expectedRevision) is { } fenced)
            return fenced;
        var teamGrants = await policy.TeamGrantsAsync(actor, id, cancellationToken).ConfigureAwait(false);
        if (await policy.CheckRevokeAsync(actor, [.. teamGrants.Select(entry => entry.Grant)], cancellationToken).ConfigureAwait(false) is { } forbidden)
            return forbidden;
        var result = await teams.DeleteAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (result.Value is StoreApplied<StoredTeam>)
            await policy.RevokeAsync(actor, teamGrants, cancellationToken).ConfigureAwait(false);
        return Map(result);
    }

    public Task<TicketResult<StoredTeam>> AddMemberAsync(PrincipalRef actor, TeamId team, UserId member, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(team);
        RequireMember(member);
        return ReplaceAsync(actor, team, expectedRevision, current => current.Members.Contains(member) ? null : current.WithMembers(current.Members.Append(member)),
            knownAdded: [member], knownRemoved: [], cancellationToken);
    }

    public Task<TicketResult<StoredTeam>> RemoveMemberAsync(PrincipalRef actor, TeamId team, UserId member, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(team);
        RequireMember(member);
        return ReplaceAsync(actor, team, expectedRevision, current => current.Members.Contains(member) ? current.WithMembers(current.Members.Where(user => user != member)) : null,
            knownAdded: [], knownRemoved: [member], cancellationToken);
    }

    /// <summary>
    /// Authorizes <c>team:manage</c> on the team, fences on the loaded revision, checks added and removed members with
    /// <see cref="TeamGrantPolicy"/> and writes with compare-and-set. A <see langword="null"/> replacement is a no-op that returns the current state unchanged.
    /// </summary>
    private async Task<TicketResult<StoredTeam>> ReplaceAsync(PrincipalRef actor, TeamId id, long expectedRevision, Func<Team, Team?> replace,
        IReadOnlyCollection<UserId>? knownAdded, IReadOnlyCollection<UserId>? knownRemoved, CancellationToken cancellationToken)
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
            return new TicketValue<StoredTeam>(current);
        var added = knownAdded ?? ComputeDiff(replacement.Members, current.Team.Members);
        var removed = knownRemoved ?? ComputeDiff(current.Team.Members, replacement.Members);
        if (await policy.CheckMembershipChangeAsync(actor, id, added, removed, cancellationToken).ConfigureAwait(false) is { } denied)
            return denied;
        return Map(await teams.UpdateAsync(actor.Scope, replacement, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private static IReadOnlyCollection<UserId> ComputeDiff(IReadOnlySet<UserId> source, IReadOnlySet<UserId> exclude)
    {
        List<UserId>? diff = null;
        foreach (var member in source)
            if (!exclude.Contains(member))
                (diff ??= []).Add(member);
        return (IReadOnlyCollection<UserId>?)diff ?? [];
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
