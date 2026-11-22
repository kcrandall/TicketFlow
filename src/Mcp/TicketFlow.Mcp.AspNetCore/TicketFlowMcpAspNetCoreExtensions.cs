using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;

namespace TicketFlow.Mcp.AspNetCore;

public static class TicketFlowMcpAspNetCoreExtensions
{
    /// <summary>
    /// Resolves MCP callers with the host's <see cref="ITicketFlowPartitionResolver"/> and <see cref="ITicketFlowPrincipalResolver"/>
    /// (the same resolvers the TicketFlow HTTP endpoints use) over the MCP request's <see cref="HttpContext"/>. Replaces any
    /// previously registered <see cref="ITicketFlowMcpPrincipalResolver"/>. Requires <c>AddTicketFlowEndpoints()</c> (or the host's own
    /// registrations of both resolvers).
    /// </summary>
    public static IMcpServerBuilder WithTicketFlowHostPrincipals(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpContextAccessor();
        builder.Services.RemoveAll<ITicketFlowMcpPrincipalResolver>();
        builder.Services.AddScoped<ITicketFlowMcpPrincipalResolver, HostMcpPrincipalResolver>();
        return builder;
    }

    /// <summary>
    /// Maps the MCP Streamable HTTP endpoint (the SDK's <c>MapMcp</c>) at <paramref name="pattern"/> and requires authorization with
    /// <paramref name="authorizationPolicy"/> (or the default policy). Authentication and policies are the host's; TicketFlow Access
    /// still decides every tool call.
    /// </summary>
    public static IEndpointConventionBuilder MapTicketFlowMcp(this IEndpointRouteBuilder endpoints, string pattern = "/mcp", string? authorizationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var mapped = endpoints.MapMcp(pattern);
        return authorizationPolicy is null ? mapped.RequireAuthorization() : mapped.RequireAuthorization(authorizationPolicy);
    }
}

/// <summary>
/// Bridges MCP caller resolution to the TicketFlow hosting resolvers. The identity is always the MCP caller's own
/// <see cref="TicketFlowMcpCaller.User"/>: when the current request's <see cref="HttpContext.User"/> is that same principal the request
/// is used as is; otherwise a detached context carrying the caller's user (and a read-only copy of the request line and headers for
/// partition resolution) is built. The shared request context is never mutated.
/// </summary>
internal sealed class HostMcpPrincipalResolver(IHttpContextAccessor accessor, ITicketFlowPartitionResolver partitions, ITicketFlowPrincipalResolver principals)
    : ITicketFlowMcpPrincipalResolver
{
    public async ValueTask<PrincipalRef?> ResolveAsync(TicketFlowMcpCaller caller, WorkspaceId workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.User is not { Identity.IsAuthenticated: true } user)
            return null;
        var http = For(accessor.HttpContext, user, caller.Services);
        if (await partitions.ResolveAsync(http, cancellationToken).ConfigureAwait(false) is not { } partition)
            return null;
        var scope = partition.InWorkspace(workspace);
        var principal = await principals.ResolveAsync(http, scope, cancellationToken).ConfigureAwait(false);
        return principal is { } value && value.Value is not null && value.Scope == scope ? value : (PrincipalRef?)null;
    }

    internal static HttpContext For(HttpContext? current, System.Security.Claims.ClaimsPrincipal user, IServiceProvider services)
    {
        if (current is not null && ReferenceEquals(current.User, user))
            return current;
        var detached = new DefaultHttpContext { User = user, RequestServices = current?.RequestServices ?? services };
        if (current is not null)
        {
            detached.Request.Scheme = current.Request.Scheme;
            detached.Request.Host = current.Request.Host;
            detached.Request.PathBase = current.Request.PathBase;
            detached.Request.Path = current.Request.Path;
            foreach (var header in current.Request.Headers)
                detached.Request.Headers[header.Key] = header.Value;
        }
        return detached;
    }
}
