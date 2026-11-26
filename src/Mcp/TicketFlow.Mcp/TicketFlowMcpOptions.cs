using System.Security.Claims;
using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Mcp;

/// <summary>How <c>tools/list</c> presents TicketFlow tools the caller may not be able to use.</summary>
public enum TicketFlowToolListMode
{
    /// <summary>List every enabled tool; each carries <c>_meta["ticketflow/permitted"]</c> (true, false or absent when not probed). Default.</summary>
    ShowAll = 1,

    /// <summary>Omit tools whose required permissions the caller does not hold at workspace level.</summary>
    HideUnpermitted = 2,
}

/// <summary>The MCP request's caller as seen by the TicketFlow tools: the host-authenticated user (if any) and the request services.</summary>
public sealed record TicketFlowMcpCaller(ClaimsPrincipal? User, IServiceProvider Services)
{
    public override string ToString() => "TicketFlowMcpCaller { <redacted> }";
}

/// <summary>
/// Maps the host-authenticated MCP caller to a TicketFlow principal of exactly the requested workspace. Application-owned, like
/// <c>ITicketFlowPrincipalResolver</c> for HTTP endpoints: read authenticated identity only (never tool arguments) and return
/// <see langword="null"/> to reject the call as unauthenticated. The principal is an identity, not a permission: every tool still
/// authorizes through Access. <c>TicketFlow.Mcp.AspNetCore</c> supplies an implementation over the hosting resolvers.
/// </summary>
public interface ITicketFlowMcpPrincipalResolver
{
    ValueTask<PrincipalRef?> ResolveAsync(TicketFlowMcpCaller caller, WorkspaceId workspace, CancellationToken cancellationToken = default);
}

public sealed class TicketFlowMcpOptions
{
    /// <summary>Toolsets registered by <c>WithTicketFlowTools</c> (default <see cref="TicketFlowToolsets.Standard"/>, everything except Access).</summary>
    public TicketFlowToolsets Toolsets { get; set; } = TicketFlowToolsets.Standard;

    /// <summary>Workspace used when a tool call omits <c>workspace</c> (and for tool-list marking). Without it every call must name one.</summary>
    public string? DefaultWorkspace { get; set; }

    /// <summary>How <c>tools/list</c> presents tools the caller cannot use (default <see cref="TicketFlowToolListMode.ShowAll"/>).</summary>
    public TicketFlowToolListMode ToolList { get; set; } = TicketFlowToolListMode.ShowAll;

    /// <summary>
    /// Probe sensitive permissions when marking or filtering <c>tools/list</c> (default false). Access audits every sensitive decision,
    /// so enabling this records audit events on every listing; when disabled, sensitive tools stay listed without a verdict.
    /// </summary>
    public bool ProbeSensitivePermissions { get; set; }

    /// <summary>Upper bound of one serialized tool result in UTF-8 bytes (default 256 KiB); larger results become <c>result-too-large</c>.</summary>
    public int MaximumResultBytes { get; set; } = 256 * 1024;

    /// <summary>Message, note and article bodies longer than this are truncated with a <c>truncated</c> flag (default 8000 characters).</summary>
    public int MaximumTextLength { get; set; } = 8000;

    /// <summary>Fallback principal resolution when no <see cref="ITicketFlowMcpPrincipalResolver"/> is registered.</summary>
    public Func<TicketFlowMcpCaller, WorkspaceId, CancellationToken, ValueTask<PrincipalRef?>>? ResolvePrincipal { get; set; }

    internal void Validate()
    {
        if ((Toolsets & ~TicketFlowToolsets.All) != TicketFlowToolsets.None || MaximumResultBytes < 4096 || MaximumTextLength < 256)
            throw new InvalidOperationException("TicketFlow MCP options are invalid.");
        if (DefaultWorkspace is not null)
            _ = new WorkspaceId(DefaultWorkspace);
    }
}

internal sealed class OptionsMcpPrincipalResolver(IOptions<TicketFlowMcpOptions> options) : ITicketFlowMcpPrincipalResolver
{
    public ValueTask<PrincipalRef?> ResolveAsync(TicketFlowMcpCaller caller, WorkspaceId workspace, CancellationToken cancellationToken = default) =>
        options.Value.ResolvePrincipal is { } resolve ? resolve(caller, workspace, cancellationToken) : ValueTask.FromResult<PrincipalRef?>(null);
}
