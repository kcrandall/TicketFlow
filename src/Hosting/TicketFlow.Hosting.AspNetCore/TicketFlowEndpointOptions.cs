using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Hosting.AspNetCore;

/// <summary>
/// Maps the host-authenticated request user to a TicketFlow principal for one resolved scope. Application-owned: implementations read
/// the authenticated <see cref="HttpContext.User"/> (never client-supplied identifiers) and return <see langword="null"/> to reject the
/// request with 401. The returned principal's scope must equal the requested scope.
/// </summary>
public interface ITicketFlowPrincipalResolver
{
    ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the partition (tenant, project, environment, app) a request addresses, for example from the host name, a tenant claim or
/// fixed configuration. The workspace comes from the <c>/workspaces/{workspaceId}</c> route segment. <see langword="null"/> rejects the
/// request (404 for public endpoints, 401 for authenticated ones).
/// </summary>
public interface ITicketFlowPartitionResolver
{
    ValueTask<AccessPartition?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken = default);
}

public sealed class TicketFlowEndpointOptions
{
    /// <summary>Prefix of authenticated endpoints (default <c>/ticketflow/api</c>); groups add <c>/workspaces/{workspaceId}</c>.</summary>
    public string RoutePrefix { get; set; } = "/ticketflow/api";

    /// <summary>Prefix of anonymous endpoints (public portal, survey responses; default <c>/ticketflow/public</c>).</summary>
    public string PublicRoutePrefix { get; set; } = "/ticketflow/public";

    /// <summary>Maximum request body size for TicketFlow endpoints (default 1 MiB; requests with a larger Content-Length get 413).</summary>
    public long MaximumRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Adds <c>RequireAuthorization</c> to authenticated groups (with <see cref="AuthorizationPolicy"/> when set). Default false.</summary>
    public bool RequireAuthorization { get; set; }

    public string? AuthorizationPolicy { get; set; }

    /// <summary>A rate-limiting policy (registered by the host with <c>AddRateLimiter</c>) applied to every anonymous endpoint.</summary>
    public string? AnonymousRateLimitingPolicy { get; set; }

    /// <summary>The partition of single-partition hosts when no <see cref="ITicketFlowPartitionResolver"/> is registered.</summary>
    public AccessPartition? Partition { get; set; }

    /// <summary>Fallback partition resolution when no <see cref="ITicketFlowPartitionResolver"/> is registered (wins over <see cref="Partition"/>).</summary>
    public Func<HttpContext, ValueTask<AccessPartition?>>? ResolvePartition { get; set; }

    /// <summary>Fallback principal resolution when no <see cref="ITicketFlowPrincipalResolver"/> is registered.</summary>
    public Func<HttpContext, TicketScope, ValueTask<PrincipalRef?>>? ResolvePrincipal { get; set; }

    /// <summary>The OpenAPI document (and endpoint group) name (default <c>ticketflow</c>).</summary>
    public string OpenApiDocumentName { get; set; } = "ticketflow";

    /// <summary>Upper bound for one contact export request (default 10 000).</summary>
    public int MaximumExportContacts { get; set; } = 10_000;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RoutePrefix) || !RoutePrefix.StartsWith('/') || string.IsNullOrWhiteSpace(PublicRoutePrefix) || !PublicRoutePrefix.StartsWith('/')
            || MaximumRequestBodyBytes < 1024 || string.IsNullOrWhiteSpace(OpenApiDocumentName) || MaximumExportContacts < 1)
            throw new InvalidOperationException("TicketFlow endpoint options are invalid.");
    }
}

/// <summary>The <see cref="TicketFlowEndpointOptions.Partition"/> fallback registered by <c>AddTicketFlowEndpoints()</c>.</summary>
public sealed class OptionsTicketFlowPartitionResolver(IOptions<TicketFlowEndpointOptions> options) : ITicketFlowPartitionResolver
{
    public ValueTask<AccessPartition?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken = default) =>
        options.Value.ResolvePartition is { } resolve ? resolve(httpContext) : ValueTask.FromResult(options.Value.Partition);
}

/// <summary>The <see cref="TicketFlowEndpointOptions.ResolvePrincipal"/> fallback registered by <c>AddTicketFlowEndpoints()</c>; adapters may replace exactly this registration.</summary>
public sealed class OptionsTicketFlowPrincipalResolver(IOptions<TicketFlowEndpointOptions> options) : ITicketFlowPrincipalResolver
{
    public ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default) =>
        options.Value.ResolvePrincipal is { } resolve ? resolve(httpContext, scope) : ValueTask.FromResult<PrincipalRef?>(null);
}
