using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Realtime.SignalR;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.IdentityFlow;

/// <summary>
/// Resolves TicketFlow principals from IdentityFlow-authenticated requests (<see cref="IdentityFlowClaimsMapper"/>): users get a
/// <see cref="UserPrincipal"/> with the workspace's current team snapshot from <see cref="ITeamMembershipResolver"/>; API clients an
/// <see cref="ApiClientPrincipal"/>. For SignalR agent connections the workspace comes from the
/// <see cref="IdentityFlowPrincipalOptions.RealtimeWorkspaceParameter"/> query parameter and the partition from
/// <see cref="ITicketFlowPartitionResolver"/>. Authorization itself stays with TicketFlow Access grants.
/// </summary>
public sealed class IdentityFlowPrincipalResolver(ITeamMembershipResolver teams, ITicketFlowPartitionResolver partitions, IOptions<IdentityFlowPrincipalOptions> options)
    : ITicketFlowPrincipalResolver, IRealtimePrincipalResolver
{
    public async ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(scope);
        return await ResolveAsync(httpContext.User, scope, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PrincipalRef?> ResolveAsync(ClaimsPrincipal user, HttpContext? httpContext, CancellationToken cancellationToken = default)
    {
        if (httpContext is null || httpContext.Request.Query[options.Value.RealtimeWorkspaceParameter].ToString() is not { Length: > 0 } workspace)
            return null;
        WorkspaceId id;
        try
        {
            id = new WorkspaceId(workspace);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (await partitions.ResolveAsync(httpContext, cancellationToken).ConfigureAwait(false) is not { } partition)
            return null;
        return await ResolveAsync(user, partition.InWorkspace(id), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps <paramref name="user"/> within <paramref name="scope"/>; <see langword="null"/> when IdentityFlow claims do not identify a principal.</summary>
    public async ValueTask<PrincipalRef?> ResolveAsync(ClaimsPrincipal user, TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        switch (IdentityFlowClaimsMapper.Map(user, options.Value))
        {
            case { Kind: IdentityFlowPrincipalKind.User } identity:
                return await teams.ResolveUserPrincipalAsync(scope, new UserId(identity.Id), cancellationToken).ConfigureAwait(false);
            case { Kind: IdentityFlowPrincipalKind.ApiClient } identity:
                return new ApiClientPrincipal(scope, new ApiClientId(identity.Id));
            default:
                return null;
        }
    }
}
