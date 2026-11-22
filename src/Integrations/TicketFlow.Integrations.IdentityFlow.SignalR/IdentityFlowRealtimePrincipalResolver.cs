using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Realtime.SignalR;

namespace TicketFlow.Integrations.IdentityFlow.SignalR;

/// <summary>
/// <see cref="IRealtimePrincipalResolver"/> for agent hub connections: the workspace comes from the
/// <see cref="IdentityFlowPrincipalOptions.RealtimeWorkspaceParameter"/> query parameter, the partition from
/// <see cref="ITicketFlowPartitionResolver"/>, and the principal from <see cref="IdentityFlowPrincipalResolver"/> (same staff links,
/// prefixes, scopes and team snapshots as REST endpoints).
/// </summary>
public sealed class IdentityFlowRealtimePrincipalResolver(IdentityFlowPrincipalResolver principals, ITicketFlowPartitionResolver partitions, IOptions<IdentityFlowPrincipalOptions> options)
    : IRealtimePrincipalResolver
{
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
        return await principals.ResolveAsync(user, partition.InWorkspace(id), cancellationToken).ConfigureAwait(false);
    }
}
