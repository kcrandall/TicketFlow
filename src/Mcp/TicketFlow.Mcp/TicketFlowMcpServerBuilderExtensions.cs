using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Access;

namespace TicketFlow.Mcp;

/// <summary>
/// Which toolsets this server registered and which are usable: a toolset is enabled when it was registered by
/// <c>WithTicketFlowTools</c> and its feature services are registered in the container.
/// </summary>
public sealed class TicketFlowMcpRegistry
{
    private readonly IServiceProviderIsService? isService;
    private readonly IServiceScopeFactory? scopeFactory;
    private readonly ConcurrentDictionary<string, bool> toolEnabled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<TicketFlowToolsets, bool> toolsetServices = new();
    private readonly ConcurrentDictionary<Type, bool> serviceAvailable = new();
    private string? cachedGuide;

    internal TicketFlowMcpRegistry(TicketFlowToolsets registered, IServiceProviderIsService? isService, IServiceScopeFactory? scopeFactory = null)
    {
        Registered = registered;
        this.isService = isService;
        this.scopeFactory = scopeFactory;
    }

    /// <summary>The toolsets whose tools were added to the MCP server.</summary>
    public TicketFlowToolsets Registered { get; }

    /// <summary>Whether every feature service <paramref name="tool"/> calls is registered.</summary>
    public bool ServicesAvailable(TicketFlowToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        for (var i = 0; i < tool.Services.Count; i++)
            if (!IsRegistered(tool.Services[i]))
                return false;
        return true;
    }

    /// <summary>Whether the primary feature service of <paramref name="toolset"/> is registered.</summary>
    public bool ServicesAvailable(TicketFlowToolsets toolset) => toolset == TicketFlowToolsets.None
        || toolsetServices.GetOrAdd(toolset, static (set, self) => self.IsRegistered(TicketFlowToolsetCatalog.Get(set).PrimaryService), this);

    public bool IsRegistered(TicketFlowToolsets toolset) => toolset == TicketFlowToolsets.None || (Registered & toolset) == toolset;

    /// <summary>Registered on this server and its services available: the tool can be listed and called.</summary>
    public bool IsEnabled(TicketFlowToolDescriptor tool) =>
        toolEnabled.GetOrAdd(tool.Name, static (_, state) => state.Self.IsRegistered(state.Tool.Toolset) && state.Self.ServicesAvailable(state.Tool), (Self: this, Tool: tool));

    internal string GetOrRenderGuide(Func<TicketFlowMcpRegistry, string> render) => cachedGuide ??= render(this);

    private bool IsRegistered(Type service) =>
        serviceAvailable.GetOrAdd(service, static (type, self) => self.CheckServiceAvailable(type), this);

    private bool CheckServiceAvailable(Type service)
    {
        if (isService is null || !isService.IsService(service))
            return false;
        if (service == typeof(IServiceAccountService))
            return (isService.IsService(typeof(IServiceAccountStore)) && isService.IsService(typeof(IApiKeyStore))) || CanResolve(service);
        if (service == typeof(IRoleFederationService))
            return isService.IsService(typeof(IRoleFederationMappingStore)) || CanResolve(service);
        return true;
    }

    private bool CanResolve(Type service)
    {
        if (scopeFactory is null)
            return false;
        try
        {
            using var scope = scopeFactory.CreateScope();
            return scope.ServiceProvider.GetService(service) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
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
            services.AddSingleton(provider => new TicketFlowMcpRegistry(state.Toolsets, provider.GetService<IServiceProviderIsService>(), provider.GetService<IServiceScopeFactory>()));
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
            builder.WithPrompts<TicketFlowMcpPrompts>();
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
        var factory = ActivatorUtilities.CreateFactory(tool.DeclaringType, [typeof(TicketFlowToolContext)]);
        var options = root.GetService<IOptions<TicketFlowMcpOptions>>()?.Value ?? new TicketFlowMcpOptions();
        var logger = root.GetService<ILoggerFactory>()?.CreateLogger("TicketFlow.Mcp") ?? NullLogger.Instance;
        return McpServerTool.Create(tool.Method, request =>
        {
            var services = request.Services ?? root;
            return factory(services, [new TicketFlowToolContext(services, request.User, tool, options, logger)]);
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
