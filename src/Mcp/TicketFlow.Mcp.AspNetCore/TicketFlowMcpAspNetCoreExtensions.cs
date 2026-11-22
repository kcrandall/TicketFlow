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

/// <summary>Bridges MCP caller resolution to the TicketFlow hosting resolvers.</summary>
internal sealed class HostMcpPrincipalResolver(IHttpContextAccessor accessor, ITicketFlowPartitionResolver partitions, ITicketFlowPrincipalResolver principals)
    : ITicketFlowMcpPrincipalResolver
{
    public async ValueTask<PrincipalRef?> ResolveAsync(TicketFlowMcpCaller caller, WorkspaceId workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        // The SDK runs tool calls on the MCP request's HttpContext. Fall back to a context carrying only the MCP user for transports
        // that do not flow it (resolvers that read the request itself, such as host-name partition resolvers, then return null).
        var http = accessor.HttpContext ?? new DefaultHttpContext { RequestServices = caller.Services };
        if (caller.User is { } user && !ReferenceEquals(http.User, user) && http.User.Identity?.IsAuthenticated != true)
            http.User = user;
        if (http.User.Identity?.IsAuthenticated != true)
            return null;
        if (await partitions.ResolveAsync(http, cancellationToken).ConfigureAwait(false) is not { } partition)
            return null;
        var scope = partition.InWorkspace(workspace);
        var principal = await principals.ResolveAsync(http, scope, cancellationToken).ConfigureAwait(false);
        return principal is { } value && value.Value is not null && value.Scope == scope ? value : (PrincipalRef?)null;
    }
}
