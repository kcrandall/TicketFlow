namespace TicketFlow.Mcp;

/// <summary>
/// Marks an SDK <c>[McpServerToolType]</c> class as belonging to a toolset. <see cref="TicketFlowToolsets.None"/> is the always-on
/// self-awareness group.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TicketFlowToolsetAttribute(TicketFlowToolsets toolset) : Attribute
{
    public TicketFlowToolsets Toolset { get; } = toolset;
}

/// <summary>
/// The TicketFlow permission catalog entry of one MCP tool method. The permissions are descriptive: they drive tool-list marking and
/// filtering, <c>describe_tools</c>, <c>explain_access</c> and forbidden-error hints, but every call is still decided by the feature
/// service through Access.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class TicketFlowToolAttribute(params string[] permissions) : Attribute
{
    /// <summary>The permissions the operation requires (all of them, or one of them when <see cref="AnyOf"/>).</summary>
    public string[] Permissions { get; } = permissions;

    /// <summary>When true, holding any one of <see cref="Permissions"/> suffices.</summary>
    public bool AnyOf { get; init; }

    /// <summary>Permissions that change what the result contains (for example <c>note:read</c> for timelines) without being required.</summary>
    public string[] Optional { get; init; } = [];

    /// <summary>The feature service the tool calls when it differs from its toolset's primary service.</summary>
    public Type? Service { get; init; }
}
