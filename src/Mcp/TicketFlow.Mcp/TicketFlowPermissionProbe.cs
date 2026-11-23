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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in permissions)
        {
            if (!seen.Add(value))
                continue;
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
            if (probe.Count <= Batch)
            {
                var held = await planner.HeldAnywhereAsync(principal, principal.Scope, probe, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < probe.Count; i++)
                    result[probe[i].Value] = held.Contains(probe[i]);
                return result;
            }
            var chunks = probe.Chunk(Batch).ToArray();
            var tasks = new Task<IReadOnlySet<Permission>>[chunks.Length];
            for (var i = 0; i < chunks.Length; i++)
                tasks[i] = planner.HeldAnywhereAsync(principal, principal.Scope, chunks[i], cancellationToken);
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (var i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                var held = results[i];
                for (var j = 0; j < chunk.Length; j++)
                    result[chunk[j].Value] = held.Contains(chunk[j]);
            }
            return result;
        }
        var resource = WorkspaceResources.Workspace(principal.Scope);
        if (probe.Count <= Batch)
        {
            var context = await evaluator.LoadDecisionContextAsync(principal, principal.Scope, probe, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < probe.Count; i++)
                result[probe[i].Value] = (await context.DecideAsync(probe[i], resource, cancellationToken).ConfigureAwait(false)).IsPermitted;
            return result;
        }
        var workspaceChunks = probe.Chunk(Batch).ToArray();
        var contextTasks = new Task<IAccessDecisionContext>[workspaceChunks.Length];
        for (var i = 0; i < workspaceChunks.Length; i++)
            contextTasks[i] = evaluator.LoadDecisionContextAsync(principal, principal.Scope, workspaceChunks[i], cancellationToken);
        var contexts = await Task.WhenAll(contextTasks).ConfigureAwait(false);
        for (var i = 0; i < workspaceChunks.Length; i++)
        {
            var chunk = workspaceChunks[i];
            var context = contexts[i];
            for (var j = 0; j < chunk.Length; j++)
                result[chunk[j].Value] = (await context.DecideAsync(chunk[j], resource, cancellationToken).ConfigureAwait(false)).IsPermitted;
        }
        return result;
    }

    /// <summary>Whether the probed decisions satisfy a tool: true, false or <see langword="null"/> (undetermined).</summary>
    public static bool? Satisfies(TicketFlowToolDescriptor tool, IReadOnlyDictionary<string, bool?> decisions)
    {
        var count = tool.Permissions.Count;
        if (count == 0)
            return true;
        var anyNull = false;
        if (tool.AnyOf)
        {
            for (var i = 0; i < count; i++)
            {
                var value = decisions.GetValueOrDefault(tool.Permissions[i]);
                if (value == true)
                    return true;
                if (value is null)
                    anyNull = true;
            }
            return anyNull ? null : false;
        }
        for (var i = 0; i < count; i++)
        {
            var value = decisions.GetValueOrDefault(tool.Permissions[i]);
            if (value == false)
                return false;
            if (value is null)
                anyNull = true;
        }
        return anyNull ? null : true;
    }
}
