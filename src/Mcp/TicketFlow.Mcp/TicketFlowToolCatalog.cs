using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using TicketFlow.Access;
using TicketFlow.Assist;
using TicketFlow.Automation;
using TicketFlow.Channels;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.HelpCenter;
using TicketFlow.Reporting;
using TicketFlow.Sla;
using TicketFlow.Surveys;

namespace TicketFlow.Mcp;

/// <summary>One TicketFlow MCP tool as described by the catalog.</summary>
public sealed record TicketFlowToolDescriptor(
    string Name,
    string? Title,
    string Description,
    TicketFlowToolsets Toolset,
    IReadOnlyList<string> Permissions,
    bool AnyOf,
    IReadOnlyList<string> OptionalPermissions,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    IReadOnlyList<Type> Services,
    Type DeclaringType,
    MethodInfo Method,
    Type OutputType)
{
    private IReadOnlyList<string>? grantedByBuiltInRoles;

    /// <summary>The toolset name (<c>self</c> for the self-awareness tools).</summary>
    public string ToolsetName => TicketFlowToolsetCatalog.NameOf(Toolset);

    /// <summary>Built-in roles that hold every required permission (any one of them when <see cref="AnyOf"/>).</summary>
    public IReadOnlyList<string> GrantedByBuiltInRoles => grantedByBuiltInRoles ??= TicketFlowToolCatalog.RolesGranting(Permissions, AnyOf);
}

/// <summary>
/// The catalog of every TicketFlow MCP tool in this package, across all toolsets (whether or not a host plugs them in), built from the
/// SDK <c>[McpServerTool]</c> attributes plus <see cref="TicketFlowToolAttribute"/> on each tool method.
/// </summary>
public static class TicketFlowToolCatalog
{
    private static readonly Lazy<IReadOnlyList<TicketFlowToolDescriptor>> Lazy = new(Build);
    private static readonly Lazy<FrozenDictionary<string, TicketFlowToolDescriptor>> ByName =
        new(() => Lazy.Value.ToFrozenDictionary(tool => tool.Name, StringComparer.Ordinal));
    private static readonly IReadOnlyList<string> AllBuiltInRoleIds = [.. BuiltInRoles.All.Select(role => role.Id.Value)];
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> SinglePermissionRoles = new(StringComparer.Ordinal);

    /// <summary>Every tool, ordered by toolset (self first) then name.</summary>
    public static IReadOnlyList<TicketFlowToolDescriptor> Tools => Lazy.Value;

    public static TicketFlowToolDescriptor? Find(string name) => ByName.Value.GetValueOrDefault(name);

    /// <summary>
    /// Every permission this package knows how to describe: the Access built-ins plus the definitions of every feature package it
    /// references. The host's runtime <see cref="IPermissionCatalog"/> contains only the features it registered.
    /// </summary>
    public static IReadOnlyList<PermissionDefinition> KnownPermissions { get; } =
    [
        .. AccessPermissions.Definitions
            .Concat(ConversationPermissions.Definitions)
            .Concat(ContactPermissions.Definitions)
            .Concat(SlaPermissions.Definitions)
            .Concat(AutomationPermissions.Definitions)
            .Concat(HelpCenterPermissions.Definitions)
            .Concat(SurveyPermissions.Definitions)
            .Concat(ReportPermissions.Definitions)
            .Concat(AssistPermissions.Definitions)
            .Concat(ChannelPermissions.Definitions)
            .DistinctBy(definition => definition.Permission)
            .OrderBy(definition => definition.Permission),
    ];

    private static readonly FrozenDictionary<string, PermissionDefinition> KnownPermissionsByName =
        KnownPermissions.ToFrozenDictionary(definition => definition.Permission.Value, StringComparer.Ordinal);

    /// <summary>The known definition of <paramref name="permission"/>, or <see langword="null"/>.</summary>
    public static PermissionDefinition? FindPermission(string permission) =>
        KnownPermissionsByName.GetValueOrDefault(permission);

    /// <summary>Cached built-in role identifiers holding a single permission.</summary>
    internal static IReadOnlyList<string> RolesGranting(string permission) =>
        SinglePermissionRoles.GetOrAdd(permission, static value =>
        {
            var parsed = new Permission(value);
            var roles = new List<string>(BuiltInRoles.All.Count);
            foreach (var role in BuiltInRoles.All)
                if (role.Includes(parsed))
                    roles.Add(role.Id.Value);
            return roles;
        });

    /// <summary>Built-in role identifiers holding <paramref name="permissions"/> (Owner holds every registered permission).</summary>
    public static IReadOnlyList<string> RolesGranting(IReadOnlyList<string> permissions, bool anyOf = false)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Count == 0)
            return AllBuiltInRoleIds;
        if (permissions.Count == 1)
            return RolesGranting(permissions[0]);
        var parsed = new Permission[permissions.Count];
        for (var i = 0; i < permissions.Count; i++)
            parsed[i] = new Permission(permissions[i]);
        var roles = new List<string>(BuiltInRoles.All.Count);
        foreach (var role in BuiltInRoles.All)
        {
            var match = anyOf ? AnyIncluded(role, parsed) : AllIncluded(role, parsed);
            if (match)
                roles.Add(role.Id.Value);
        }
        return roles;
    }

    private static bool AnyIncluded(AccessRole role, Permission[] parsed)
    {
        for (var i = 0; i < parsed.Length; i++)
            if (role.Includes(parsed[i]))
                return true;
        return false;
    }

    private static bool AllIncluded(AccessRole role, Permission[] parsed)
    {
        for (var i = 0; i < parsed.Length; i++)
            if (!role.Includes(parsed[i]))
                return false;
        return true;
    }

    private static IReadOnlyList<TicketFlowToolDescriptor> Build()
    {
        var toolsetOrder = new Dictionary<TicketFlowToolsets, int>(TicketFlowToolsetCatalog.All.Count + 1) { [TicketFlowToolsets.None] = -1 };
        for (var i = 0; i < TicketFlowToolsetCatalog.All.Count; i++)
            toolsetOrder[TicketFlowToolsetCatalog.All[i].Toolset] = i;

        var tools = new List<TicketFlowToolDescriptor>();
        foreach (var type in typeof(TicketFlowToolCatalog).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null || type.GetCustomAttribute<TicketFlowToolsetAttribute>() is not { } toolset)
                continue;
            var primary = toolset.Toolset == TicketFlowToolsets.None ? null : TicketFlowToolsetCatalog.Get(toolset.Toolset).PrimaryService;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } tool)
                    continue;
                var entry = method.GetCustomAttribute<TicketFlowToolAttribute>()
                    ?? throw new InvalidOperationException($"Tool method {type.Name}.{method.Name} lacks [TicketFlowTool].");
                var services = new List<Type>();
                if (primary is not null)
                    services.Add(primary);
                if (entry.Service is { } service && service != primary)
                    services.Add(service);
                tools.Add(new TicketFlowToolDescriptor(
                    tool.Name ?? throw new InvalidOperationException($"Tool method {type.Name}.{method.Name} needs an explicit name."),
                    tool.Title,
                    method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                    toolset.Toolset,
                    [.. entry.Permissions],
                    entry.AnyOf,
                    [.. entry.Optional],
                    tool.ReadOnly,
                    !tool.ReadOnly && tool.Destructive,
                    tool.Idempotent,
                    services,
                    type,
                    method,
                    tool.OutputSchemaType ?? throw new InvalidOperationException($"Tool method {type.Name}.{method.Name} needs an OutputSchemaType.")));
            }
        }
        return
        [
            .. tools.OrderBy(tool => toolsetOrder.GetValueOrDefault(tool.Toolset, int.MaxValue))
                .ThenBy(tool => tool.Name, StringComparer.Ordinal),
        ];
    }
}
