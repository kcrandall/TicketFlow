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
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> Create(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            var services = request.Services ?? request.Server.Services;
            if (services?.GetService<TicketFlowMcpRegistry>() is not { } registry)
                return result;
            var options = services.GetService<IOptions<TicketFlowMcpOptions>>()?.Value ?? new TicketFlowMcpOptions();
            var tools = result.Tools.Select(tool => (Tool: tool, Descriptor: TicketFlowToolCatalog.Find(tool.Name)))
                .Where(item => item.Descriptor is null || registry.IsEnabled(item.Descriptor))
                .ToList();
            var decisions = await DecideAsync(services, request.User, options, tools.Where(item => item.Descriptor is not null).Select(item => item.Descriptor!), cancellationToken)
                .ConfigureAwait(false);
            var listed = new List<Tool>(tools.Count);
            foreach (var (tool, descriptor) in tools)
            {
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
        IEnumerable<TicketFlowToolDescriptor> tools, CancellationToken cancellationToken)
    {
        if (options.DefaultWorkspace is null || services.GetService<ITicketFlowMcpPrincipalResolver>() is not { } resolver)
            return null;
        var workspace = new WorkspaceId(options.DefaultWorkspace);
        if (await resolver.ResolveAsync(new TicketFlowMcpCaller(user, services), workspace, cancellationToken).ConfigureAwait(false) is not { } principal
            || principal.Value is null || principal.Scope.WorkspaceId != workspace)
            return null;
        return await TicketFlowPermissionProbe.ProbeAsync(services, principal, tools.SelectMany(tool => tool.Permissions), options.ProbeSensitivePermissions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A deep clone of <paramref name="tool"/> (through the SDK's own serializer) that differs only in <see cref="Tool.Meta"/>.</summary>
    private static Tool Mark(Tool tool, bool? permitted)
    {
        var info = (JsonTypeInfo<Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));
        var clone = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(tool, info), info)!;
        var meta = clone.Meta ?? [];
        if (permitted is { } value)
            meta["ticketflow/permitted"] = value;
        clone.Meta = meta;
        return clone;
    }
}
