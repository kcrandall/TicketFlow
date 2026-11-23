using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Core;

namespace TicketFlow.Mcp;

/// <summary>
/// <c>tools/list</c> filter: removes TicketFlow tools that are not enabled on this server and, for a resolvable caller in the default
/// workspace, marks each remaining tool with <c>_meta["ticketflow/permitted"]</c> (<see cref="TicketFlowToolListMode.ShowAll"/>) or
/// omits tools the caller cannot use (<see cref="TicketFlowToolListMode.HideUnpermitted"/>). Marking is advisory: every call is still
/// decided by Access.
/// </summary>
internal static class TicketFlowToolListFilter
{
    private sealed record MarkedPair(Tool Permitted, Tool Denied);

    private static readonly ConditionalWeakTable<Tool, MarkedPair> MarkedCache = new();

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> Create(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            var services = request.Services ?? request.Server.Services;
            if (services?.GetService<TicketFlowMcpRegistry>() is not { } registry)
                return result;
            var options = services.GetService<IOptions<TicketFlowMcpOptions>>()?.Value ?? new TicketFlowMcpOptions();
            var tools = new List<(Tool Tool, TicketFlowToolDescriptor? Descriptor)>(result.Tools.Count);
            List<string>? permissions = null;
            for (var i = 0; i < result.Tools.Count; i++)
            {
                var tool = result.Tools[i];
                var descriptor = TicketFlowToolCatalog.Find(tool.Name);
                if (descriptor is not null && !registry.IsEnabled(descriptor))
                    continue;
                tools.Add((tool, descriptor));
                if (descriptor is not null && descriptor.Permissions.Count > 0)
                {
                    permissions ??= [];
                    for (var j = 0; j < descriptor.Permissions.Count; j++)
                        permissions.Add(descriptor.Permissions[j]);
                }
            }
            var decisions = await DecideAsync(services, request.User, options, permissions, cancellationToken).ConfigureAwait(false);
            var listed = new List<Tool>(tools.Count);
            for (var i = 0; i < tools.Count; i++)
            {
                var (tool, descriptor) = tools[i];
                if (descriptor is null || decisions is null)
                {
                    listed.Add(tool);
                    continue;
                }
                var permitted = TicketFlowPermissionProbe.Satisfies(descriptor, decisions);
                if (options.ToolList == TicketFlowToolListMode.HideUnpermitted && permitted == false)
                    continue;
                listed.Add(Mark(tool, permitted));
            }
            result.Tools = listed;
            return result;
        };

    private static async Task<IReadOnlyDictionary<string, bool?>?> DecideAsync(IServiceProvider services, System.Security.Claims.ClaimsPrincipal? user, TicketFlowMcpOptions options,
        List<string>? permissions, CancellationToken cancellationToken)
    {
        if (options.DefaultWorkspace is null || services.GetService<ITicketFlowMcpPrincipalResolver>() is not { } resolver)
            return null;
        var workspace = new WorkspaceId(options.DefaultWorkspace);
        if (await resolver.ResolveAsync(new TicketFlowMcpCaller(user, services), workspace, cancellationToken).ConfigureAwait(false) is not { } principal
            || principal.Value is null || principal.Scope.WorkspaceId != workspace)
            return null;
        if (permissions is null || permissions.Count == 0)
            return new Dictionary<string, bool?>(StringComparer.Ordinal);
        return await TicketFlowPermissionProbe.ProbeAsync(services, principal, permissions, options.ProbeSensitivePermissions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A cached immutable clone of <paramref name="tool"/> that differs only in <c>_meta["ticketflow/permitted"]</c>.</summary>
    private static Tool Mark(Tool tool, bool? permitted)
    {
        if (permitted is not { } value)
            return tool;
        var pair = MarkedCache.GetValue(tool, static original => new MarkedPair(CloneWith(original, true), CloneWith(original, false)));
        return value ? pair.Permitted : pair.Denied;
    }

    private static Tool CloneWith(Tool tool, bool permitted)
    {
        var info = (JsonTypeInfo<Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
        var clone = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(tool, info), info)!;
        var meta = clone.Meta ?? [];
        meta["ticketflow/permitted"] = permitted;
        clone.Meta = meta;
        return clone;
    }
}
