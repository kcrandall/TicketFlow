using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Hosting.AspNetCore;

/// <summary>
/// The authenticated actor of a workspace endpoint: the scope (partition from <see cref="ITicketFlowPartitionResolver"/> plus the
/// <c>workspaceId</c> route value) and the principal from <see cref="ITicketFlowPrincipalResolver"/>. Bound per request; the group
/// filter turns a failed binding into 400/401 before the handler runs, so handlers only see resolved actors.
/// </summary>
public sealed class TicketFlowActor
{
    private readonly TicketScope? scope;
    private readonly PrincipalRef? principal;

    private TicketFlowActor(TicketScope? scope, PrincipalRef? principal, int failureStatus, string? failureCode)
    {
        this.scope = scope;
        this.principal = principal;
        FailureStatus = failureStatus;
        FailureCode = failureCode;
    }

    public TicketScope Scope => scope ?? throw new InvalidOperationException("The actor was not resolved.");

    public PrincipalRef Principal => principal ?? throw new InvalidOperationException("The actor was not resolved.");

    /// <summary>The user identifier for user principals, otherwise <see langword="null"/>.</summary>
    public UserId? UserId => principal is UserPrincipal user ? user.UserId : null;

    internal int FailureStatus { get; }
    internal string? FailureCode { get; }

    public static async ValueTask<TicketFlowActor?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (resolved, status, code) = await TicketFlowScopes.ResolveAsync(context).ConfigureAwait(false);
        if (resolved is null)
            return new TicketFlowActor(null, null, status == StatusCodes.Status404NotFound ? StatusCodes.Status401Unauthorized : status, code);
        if (!AccessPrincipalGuard.VerifyScopeClaims(context.User, resolved))
            return new TicketFlowActor(null, null, StatusCodes.Status401Unauthorized, "scope-mismatch");
        var resolver = context.RequestServices.GetRequiredService<ITicketFlowPrincipalResolver>();
        var resolvedPrincipal = await resolver.ResolveAsync(context, resolved, context.RequestAborted).ConfigureAwait(false);
        if (resolvedPrincipal is not { } value)
            return new TicketFlowActor(null, null, StatusCodes.Status401Unauthorized, "unauthenticated");
        if (value.Scope != resolved)
            return new TicketFlowActor(null, null, StatusCodes.Status401Unauthorized, "scope-mismatch");
        var serviceAccounts = context.RequestServices.GetService<IServiceAccountStore>();
        if (!await AccessPrincipalGuard.IsServiceAccountActiveForScopeAsync(value, resolved, serviceAccounts, context.RequestAborted).ConfigureAwait(false))
            return new TicketFlowActor(null, null, StatusCodes.Status401Unauthorized, "unauthenticated");
        return new TicketFlowActor(resolved, value, 0, null);
    }
}

/// <summary>The scope of an anonymous endpoint (public portal, survey response): partition resolver plus route workspace.</summary>
public sealed class TicketFlowPublicScope
{
    private readonly TicketScope? scope;

    private TicketFlowPublicScope(TicketScope? scope, int failureStatus, string? failureCode)
    {
        this.scope = scope;
        FailureStatus = failureStatus;
        FailureCode = failureCode;
    }

    public TicketScope Scope => scope ?? throw new InvalidOperationException("The scope was not resolved.");

    internal int FailureStatus { get; }
    internal string? FailureCode { get; }

    public static async ValueTask<TicketFlowPublicScope?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (resolved, status, code) = await TicketFlowScopes.ResolveAsync(context).ConfigureAwait(false);
        return new TicketFlowPublicScope(resolved, resolved is null ? status : 0, code);
    }
}

internal static class TicketFlowScopes
{
    public const string WorkspaceRouteValue = "workspaceId";

    public static async ValueTask<(TicketScope? Scope, int Status, string? Code)> ResolveAsync(HttpContext context)
    {
        if (context.GetRouteValue(WorkspaceRouteValue) is not string workspace || workspace.Length == 0)
            return (null, StatusCodes.Status400BadRequest, "workspace-required");
        WorkspaceId id;
        try
        {
            id = new WorkspaceId(workspace);
        }
        catch (ArgumentException)
        {
            return (null, StatusCodes.Status400BadRequest, "invalid-workspace");
        }
        var partitions = context.RequestServices.GetRequiredService<ITicketFlowPartitionResolver>();
        if (await partitions.ResolveAsync(context, context.RequestAborted).ConfigureAwait(false) is not { } partition)
            return (null, StatusCodes.Status404NotFound, "partition-unavailable");
        return (partition.InWorkspace(id), 0, null);
    }
}
