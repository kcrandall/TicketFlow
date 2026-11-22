using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>
/// Default <see cref="IRoutingTeamMembershipResolver"/>. Small batches (≤ <see cref="PerAgentThreshold"/> agents) use
/// <see cref="ITeamMembershipResolver"/> per agent; larger batches read the workspace's teams once (≤ 1000 teams, paged) and invert their
/// member lists, so resolving N principals costs at most one read per team instead of one per agent. Applications that replace
/// <see cref="ITeamMembershipResolver"/> with a non-store source should replace this resolver too.
/// </summary>
public sealed class StoreRoutingTeamMembershipResolver(ITeamStore teams, ITeamMembershipResolver single) : IRoutingTeamMembershipResolver
{
    public const int PerAgentThreshold = 8;

    public async Task<IReadOnlyDictionary<UserId, IReadOnlyList<TeamId>>> GetTeamsAsync(TicketScope scope, IReadOnlyCollection<UserId> agents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(agents);
        var result = new Dictionary<UserId, IReadOnlyList<TeamId>>();
        if (agents.Count <= PerAgentThreshold)
        {
            foreach (var agent in agents.Distinct())
                result[agent] = await single.GetTeamsAsync(scope, agent, cancellationToken).ConfigureAwait(false);
            return result;
        }
        var wanted = agents.ToHashSet();
        var memberships = wanted.ToDictionary(agent => agent, _ => new SortedSet<string>(StringComparer.Ordinal));
        string? after = null;
        while (true)
        {
            var page = await teams.ListAsync(scope, AllowAllFilter.Instance, new WorkspacePageRequest(WorkspaceLimits.MaximumPageSize, after), cancellationToken).ConfigureAwait(false);
            foreach (var summary in page.Items)
            {
                if (summary.MemberCount == 0 || await teams.GetAsync(scope, summary.Id, cancellationToken).ConfigureAwait(false) is not { } team)
                    continue;
                foreach (var member in team.Team.Members)
                    if (memberships.TryGetValue(member, out var set) && set.Count < UserPrincipal.MaximumTeamMemberships)
                        set.Add(summary.Id.Value);
            }
            if (!page.HasMore || page.Items.Count == 0)
                break;
            after = page.Items[^1].Id.Value;
        }
        foreach (var (agent, set) in memberships)
            result[agent] = [.. set.Select(team => new TeamId(team))];
        return result;
    }
}
