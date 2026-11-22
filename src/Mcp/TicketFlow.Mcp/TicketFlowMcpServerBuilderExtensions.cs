using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TicketFlow.Mcp;

/// <summary>
/// Which toolsets this server registered and which are usable: a toolset is enabled when it was registered by
/// <c>WithTicketFlowTools</c> and its feature services are registered in the container.
/// </summary>
public sealed class TicketFlowMcpRegistry
{
    private readonly IServiceProviderIsService? isService;

    internal TicketFlowMcpRegistry(TicketFlowToolsets registered, IServiceProviderIsService? isService)
    {
        Registered = registered;
        this.isService = isService;
    }

    /// <summary>The toolsets whose tools were added to the MCP server.</summary>
    public TicketFlowToolsets Registered { get; }

    /// <summary>Whether every feature service <paramref name="tool"/> calls is registered.</summary>
    public bool ServicesAvailable(TicketFlowToolDescriptor tool) => tool.Services.All(IsRegistered);

    /// <summary>Whether the primary feature service of <paramref name="toolset"/> is registered.</summary>
    public bool ServicesAvailable(TicketFlowToolsets toolset) => toolset == TicketFlowToolsets.None || IsRegistered(TicketFlowToolsetCatalog.Get(toolset).PrimaryService);

    public bool IsRegistered(TicketFlowToolsets toolset) => toolset == TicketFlowToolsets.None || (Registered & toolset) == toolset;

    /// <summary>Registered on this server and its services available: the tool can be listed and called.</summary>
    public bool IsEnabled(TicketFlowToolDescriptor tool) => IsRegistered(tool.Toolset) && ServicesAvailable(tool);

    private bool IsRegistered(Type service) => isService?.IsService(service) ?? false;
}

public static class TicketFlowMcpServerBuilderExtensions
{
    /// <summary>
    /// Adds the TicketFlow tools of the selected <see cref="TicketFlowMcpOptions.Toolsets"/> (default
    /// <see cref="TicketFlowToolsets.Standard"/>) plus the always-on self-awareness tools to the host's MCP server, the guide resource
    /// and the tool-list/call filters. Authentication stays with the host: register an <see cref="ITicketFlowMcpPrincipalResolver"/>
    /// (or set <see cref="TicketFlowMcpOptions.ResolvePrincipal"/>, or call <c>WithTicketFlowHostPrincipals()</c> from
    /// <c>TicketFlow.Mcp.AspNetCore</c>). Repeated calls add further toolsets without duplicating tools.
    /// </summary>
    public static IMcpServerBuilder WithTicketFlowTools(this IMcpServerBuilder builder, Action<TicketFlowMcpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        var selection = new TicketFlowMcpOptions();
        configure?.Invoke(selection);
        selection.Validate();
        services.AddOptions<TicketFlowMcpOptions>().Validate(options =>
        {
            options.Validate();
            return true;
        });
        if (configure is not null)
            services.Configure(configure);
        services.TryAddScoped<ITicketFlowMcpPrincipalResolver, OptionsMcpPrincipalResolver>();

        var state = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(TicketFlowMcpRegistrationState))?.ImplementationInstance as TicketFlowMcpRegistrationState;
        var first = state is null;
        if (state is null)
        {
            state = new TicketFlowMcpRegistrationState();
            services.AddSingleton(state);
            services.AddSingleton(provider => new TicketFlowMcpRegistry(state.Toolsets, provider.GetService<IServiceProviderIsService>()));
        }
        foreach (var tool in TicketFlowToolCatalog.Tools)
        {
            var add = tool.Toolset == TicketFlowToolsets.None ? first : (selection.Toolsets & tool.Toolset) == tool.Toolset && (state.Toolsets & tool.Toolset) != tool.Toolset;
            if (add)
                services.AddSingleton(provider => CreateTool(tool, provider));
        }
        state.Toolsets |= selection.Toolsets;
        if (first)
        {
            builder.WithResources<TicketFlowMcpResources>();
            builder.WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(TicketFlowToolListFilter.Create);
                filters.AddCallToolFilter(CallFilter);
            });
        }
        return builder;
    }

    /// <summary>Adds the given toolsets (shorthand for <c>WithTicketFlowTools(o =&gt; o.Toolsets = toolsets)</c>).</summary>
    public static IMcpServerBuilder WithTicketFlowTools(this IMcpServerBuilder builder, TicketFlowToolsets toolsets) =>
        builder.WithTicketFlowTools(options => options.Toolsets = toolsets);

    private static McpServerTool CreateTool(TicketFlowToolDescriptor tool, IServiceProvider root)
    {
        var meta = new JsonObject
        {
            ["ticketflow/toolset"] = tool.ToolsetName,
            ["ticketflow/permissions"] = new JsonArray([.. tool.Permissions.Select(permission => (JsonNode)JsonValue.Create(permission))]),
            ["ticketflow/anyOf"] = tool.AnyOf,
            ["ticketflow/grantedByBuiltInRoles"] = new JsonArray([.. tool.GrantedByBuiltInRoles.Select(role => (JsonNode)JsonValue.Create(role))]),
        };
        return McpServerTool.Create(tool.Method, request =>
        {
            var services = request.Services ?? root;
            return ActivatorUtilities.CreateInstance(services, tool.DeclaringType, new TicketFlowToolContext(services, request.User, tool));
        }, new McpServerToolCreateOptions { Services = root, Meta = meta, OutputSchema = TicketFlowToolJson.OutputSchema(tool.OutputType) });
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> CallFilter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            if (request.Params is { } call && TicketFlowToolCatalog.Find(call.Name) is { } tool)
            {
                var registry = (request.Services ?? request.Server.Services)?.GetService<TicketFlowMcpRegistry>();
                if (registry is null || !registry.IsEnabled(tool))
                    return TicketFlowToolResults.Error(new TicketFlowToolError("toolset-unavailable",
                        $"The '{tool.ToolsetName}' toolset is not enabled on this server (not plugged in, or its feature services are not registered). Call list_toolsets for details."));
            }
            return await next(request, cancellationToken).ConfigureAwait(false);
        };
}

/// <summary>Registration-time state shared by repeated <c>WithTicketFlowTools</c> calls.</summary>
internal sealed class TicketFlowMcpRegistrationState
{
    public TicketFlowToolsets Toolsets { get; set; }
}
