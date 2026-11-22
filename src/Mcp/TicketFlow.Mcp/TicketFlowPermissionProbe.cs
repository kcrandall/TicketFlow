using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp;

/// <summary>
/// Workspace-level permission probes for self-awareness and tool-list marking, through <see cref="IAccessEvaluator"/> batch decisions
/// (the same decisions as <c>GET /me</c>). Narrower team/inbox grants are not reflected. Sensitive permissions are probed only when
/// asked, because Access audits every sensitive decision.
/// </summary>
internal static class TicketFlowPermissionProbe
{
    private const int Batch = 32;

    /// <summary>
    /// Decisions for <paramref name="permissions"/>: true (permitted), false (denied or not registered) or <see langword="null"/>
    /// (sensitive and not probed, or Access is not registered).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, bool?>> ProbeAsync(IServiceProvider services, PrincipalRef principal, IEnumerable<string> permissions,
        bool includeSensitive, CancellationToken cancellationToken)
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
