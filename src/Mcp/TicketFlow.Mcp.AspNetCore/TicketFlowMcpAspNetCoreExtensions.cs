using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;

namespace TicketFlow.Mcp.AspNetCore;

public static class TicketFlowMcpAspNetCoreExtensions
{
    /// <summary>
    /// Resolves MCP callers with the host's <see cref="ITicketFlowPartitionResolver"/> and <see cref="ITicketFlowPrincipalResolver"/>
    /// (falling back to <see cref="IAccessPrincipalResolver"/> when no custom host principal resolver is configured) over the MCP
    /// request's <see cref="HttpContext"/>, preserving external roles, identity provider claims and scope pinning. Replaces any
    /// previously registered <see cref="ITicketFlowMcpPrincipalResolver"/>.
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
    /// Maps the MCP Streamable HTTP endpoint (the SDK's <c>MapMcp</c>) at <paramref name="pattern"/>, applies
    /// <c>Cache-Control: no-store</c> to every response and requires authorization with <paramref name="authorizationPolicy"/>
    /// (or the default policy). Authentication and policies are the host's; TicketFlow Access still decides every tool call.
    /// </summary>
    public static IEndpointConventionBuilder MapTicketFlowMcp(this IEndpointRouteBuilder endpoints, string pattern = "/mcp", string? authorizationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var mapped = endpoints.MapMcp(pattern);
        mapped.Add(static endpointBuilder =>
        {
            if (endpointBuilder.RequestDelegate is not { } original)
                return;
            endpointBuilder.RequestDelegate = context =>
            {
                context.Response.OnStarting(static state =>
                {
                    ((HttpResponse)state).Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                }, context.Response);
                context.Response.Headers.CacheControl = "no-store";
                return original(context);
            };
        });
        return authorizationPolicy is null ? mapped.RequireAuthorization() : mapped.RequireAuthorization(authorizationPolicy);
    }
}

/// <summary>
/// Bridges MCP caller resolution to the TicketFlow hosting resolvers. The identity is always the MCP caller's own
/// <see cref="TicketFlowMcpCaller.User"/>: when the current request's <see cref="HttpContext.User"/> is that same principal the request
/// is used as is; otherwise a detached context carrying the caller's user (and a read-only copy of the request line and headers for
/// partition resolution) is built. The shared request context is never mutated. Scope-pinning claims (<c>tenant_id</c>,
/// <c>project_id</c>, <c>environment_id</c>, <c>app_id</c>, <c>workspace_id</c>) are enforced on every call, and external role and
/// identity-provider claims are preserved for Role Federation.
/// </summary>
internal sealed class HostMcpPrincipalResolver(
    IHttpContextAccessor accessor,
    ITicketFlowPartitionResolver partitions,
    ITicketFlowPrincipalResolver? principals = null,
    IAccessPrincipalResolver? accessResolver = null,
    IOptions<TicketFlowEndpointOptions>? endpointOptions = null,
    IServiceAccountStore? serviceAccounts = null)
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
        if (!AccessPrincipalGuard.VerifyScopeClaims(user, scope))
            return null;

        var hasHostResolver = principals is not null
            && (principals is not OptionsTicketFlowPrincipalResolver
                || endpointOptions?.Value.ResolvePrincipal is not null);

        PrincipalRef? principal;
        if (hasHostResolver)
        {
            principal = await principals!.ResolveAsync(http, scope, cancellationToken).ConfigureAwait(false);
            if (principal is null || principal.Scope != scope)
                return null;
            if ((principal.ExternalRoles.Count == 0 || principal.IdentityProvider is null)
                && accessResolver is ClaimsPrincipalResolver claimsResolver)
            {
                IReadOnlyCollection<string> externalRoles = principal.ExternalRoles.Count > 0 ? principal.ExternalRoles : claimsResolver.ExtractExternalRoles(user);
                var identityProvider = principal.IdentityProvider ?? claimsResolver.TryGetIdentityProvider(user);
                if (externalRoles.Count > 0 || identityProvider is not null)
                    principal = principal switch
                    {
                        UserPrincipal userPrincipal => new UserPrincipal(userPrincipal.Scope, userPrincipal.UserId, userPrincipal.Teams, externalRoles, identityProvider),
                        ApiClientPrincipal clientPrincipal => new ApiClientPrincipal(clientPrincipal.Scope, clientPrincipal.ClientId, externalRoles, identityProvider),
                        _ => principal,
                    };
            }
        }
        else if (accessResolver is not null)
            principal = await accessResolver.ResolveAsync(user, scope, cancellationToken).ConfigureAwait(false);
        else
            return null;

        if (principal is null || principal.Scope != scope)
            return null;

        var accounts = serviceAccounts
            ?? caller.Services.GetService<IServiceAccountStore>()
            ?? http.RequestServices?.GetService<IServiceAccountStore>();
        if (!await AccessPrincipalGuard.IsServiceAccountActiveForScopeAsync(principal, scope, accounts, cancellationToken).ConfigureAwait(false))
            return null;

        return principal;
    }

    internal static HttpContext For(HttpContext? current, ClaimsPrincipal user, IServiceProvider services)
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
