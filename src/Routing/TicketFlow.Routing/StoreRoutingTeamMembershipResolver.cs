using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>
/// Default <see cref="IRoutingTeamMembershipResolver"/>. Small batches (≤ <see cref="PerAgentThreshold"/> agents) use
/// <see cref="ITeamMembershipResolver"/> per agent; larger batches resolve team memberships in a single batch query through
/// <see cref="ITeamStore.ListTeamsForUsersAsync"/>. Applications that replace <see cref="ITeamMembershipResolver"/> with a
/// non-store source should replace this resolver too.
/// </summary>
public sealed class StoreRoutingTeamMembershipResolver(ITeamStore teams, ITeamMembershipResolver single) : IRoutingTeamMembershipResolver
{
    public const int PerAgentThreshold = 8;

    public async Task<IReadOnlyDictionary<UserId, IReadOnlyList<TeamId>>> GetTeamsAsync(TicketScope scope, IReadOnlyCollection<UserId> agents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
            return EmptyMemberships;
        var distinct = new List<UserId>(agents.Count);
        var seen = new HashSet<UserId>(agents.Count);
        foreach (var agent in agents)
            if (seen.Add(agent))
                distinct.Add(agent);
        if (distinct.Count <= PerAgentThreshold)
        {
            if (distinct.Count == 1)
                return new Dictionary<UserId, IReadOnlyList<TeamId>>(1)
                {
                    [distinct[0]] = await single.GetTeamsAsync(scope, distinct[0], cancellationToken).ConfigureAwait(false),
                };
            var tasks = new Task<IReadOnlyList<TeamId>>[distinct.Count];
            for (var i = 0; i < distinct.Count; i++)
                tasks[i] = single.GetTeamsAsync(scope, distinct[i], cancellationToken);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            var result = new Dictionary<UserId, IReadOnlyList<TeamId>>(distinct.Count);
            for (var i = 0; i < distinct.Count; i++)
                result[distinct[i]] = tasks[i].Result;
            return result;
        }
        var batched = await teams.ListTeamsForUsersAsync(scope, distinct, cancellationToken).ConfigureAwait(false);
        var output = new Dictionary<UserId, IReadOnlyList<TeamId>>(distinct.Count);
        for (var i = 0; i < distinct.Count; i++)
        {
            var agent = distinct[i];
            if (batched.TryGetValue(agent, out var list))
                output[agent] = list.Count <= UserPrincipal.MaximumTeamMemberships ? list : [.. list.Take(UserPrincipal.MaximumTeamMemberships)];
            else
                output[agent] = [];
        }
        return output;
    }

    private static readonly Dictionary<UserId, IReadOnlyList<TeamId>> EmptyMemberships = [];
}
