using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp;

/// <summary>
/// Permission probes for self-awareness and tool-list marking. <see cref="ProbeAsync"/> reports a permission as held when the caller
/// holds it on <em>some</em> resource of the workspace — under a workspace, team or inbox grant (<see cref="IAccessQueryPlanner.HeldAnywhereAsync"/>),
/// so team- or inbox-scoped agents see the tools they can use somewhere. <see cref="WorkspaceAsync"/> reports workspace-level decisions
/// (the same as <c>GET /me</c>). Sensitive permissions are probed only when asked, because Access audits every sensitive decision or plan.
/// </summary>
internal static class TicketFlowPermissionProbe
{
    private const int Batch = 32;

    /// <summary>
    /// Decisions for <paramref name="permissions"/>: true (permitted), false (denied or not registered) or <see langword="null"/>
    /// (sensitive and not probed, or Access is not registered).
    /// </summary>
    public static Task<IReadOnlyDictionary<string, bool?>> ProbeAsync(IServiceProvider services, PrincipalRef principal, IEnumerable<string> permissions,
        bool includeSensitive, CancellationToken cancellationToken) =>
        DecideAsync(services, principal, permissions, includeSensitive, anyScope: true, cancellationToken);

    /// <summary>Workspace-level decisions only (narrower team/inbox grants are not reflected).</summary>
    public static Task<IReadOnlyDictionary<string, bool?>> WorkspaceAsync(IServiceProvider services, PrincipalRef principal, IEnumerable<string> permissions,
        bool includeSensitive, CancellationToken cancellationToken) =>
        DecideAsync(services, principal, permissions, includeSensitive, anyScope: false, cancellationToken);

    private static async Task<IReadOnlyDictionary<string, bool?>> DecideAsync(IServiceProvider services, PrincipalRef principal, IEnumerable<string> permissions,
        bool includeSensitive, bool anyScope, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, bool?>(StringComparer.Ordinal);
        var evaluator = services.GetService<IAccessEvaluator>();
        var catalog = services.GetService<IPermissionCatalog>();
        var probe = new List<Permission>();
        foreach (var value in permissions.Distinct(StringComparer.Ordinal))
        {
            if (evaluator is null || catalog is null)
            {
                result[value] = null;
                continue;
            }
            var permission = new Permission(value);
            if (catalog.Find(permission) is not { } definition)
                result[value] = false;
            else if (definition.IsSensitive && !includeSensitive)
                result[value] = null;
            else
                probe.Add(permission);
        }
        if (evaluator is null || probe.Count == 0)
            return result;
        if (anyScope && services.GetService<IAccessQueryPlanner>() is { } planner)
        {
            foreach (var chunk in probe.Chunk(Batch))
            {
                var held = await planner.HeldAnywhereAsync(principal, principal.Scope, chunk, cancellationToken).ConfigureAwait(false);
                foreach (var permission in chunk)
                    result[permission.Value] = held.Contains(permission);
            }
            return result;
        }
        var resource = WorkspaceResources.Workspace(principal.Scope);
        foreach (var chunk in probe.Chunk(Batch))
        {
            var context = await evaluator.LoadDecisionContextAsync(principal, principal.Scope, chunk, cancellationToken).ConfigureAwait(false);
            foreach (var permission in chunk)
                result[permission.Value] = (await context.DecideAsync(permission, resource, cancellationToken).ConfigureAwait(false)).IsPermitted;
        }
        return result;
    }

    /// <summary>Whether the probed decisions satisfy a tool: true, false or <see langword="null"/> (undetermined).</summary>
    public static bool? Satisfies(TicketFlowToolDescriptor tool, IReadOnlyDictionary<string, bool?> decisions)
    {
        if (tool.Permissions.Count == 0)
            return true;
        var values = tool.Permissions.Select(permission => decisions.GetValueOrDefault(permission)).ToList();
        if (tool.AnyOf)
            return values.Any(value => value == true) ? true : values.Any(value => value is null) ? null : false;
        return values.Any(value => value == false) ? false : values.All(value => value == true) ? true : null;
    }
}
