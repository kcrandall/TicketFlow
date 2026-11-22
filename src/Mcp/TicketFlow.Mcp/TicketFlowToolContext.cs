using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using TicketFlow.Core;

namespace TicketFlow.Mcp;

/// <summary>
/// The per-call security wrapper every TicketFlow tool runs through (modelled on a host's <c>McpToolSecurity</c>): resolves the
/// requested workspace and the caller's <see cref="PrincipalRef"/> through <see cref="ITicketFlowMcpPrincipalResolver"/>, runs the
/// tool body as that principal, maps <see cref="TicketFlowToolError"/>s and TicketFlow validation exceptions to MCP error results,
/// bounds the result size and logs the tool name and outcome code only (never arguments, bodies or addresses).
/// </summary>
public sealed class TicketFlowToolContext
{
    private readonly TicketFlowMcpOptions options;
    private readonly ILogger logger;

    public TicketFlowToolContext(IServiceProvider services, ClaimsPrincipal? user, TicketFlowToolDescriptor tool)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        User = user;
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        options = services.GetService<IOptions<TicketFlowMcpOptions>>()?.Value ?? new TicketFlowMcpOptions();
        logger = services.GetService<ILoggerFactory>()?.CreateLogger("TicketFlow.Mcp") ?? NullLogger.Instance;
    }

    /// <summary>The request services (an MCP request scope when the SDK scopes requests).</summary>
    public IServiceProvider Services { get; }

    /// <summary>The host-authenticated user of the MCP request, when the transport has one.</summary>
    public ClaimsPrincipal? User { get; }

    public TicketFlowToolDescriptor Tool { get; }

    public TicketFlowMcpOptions Options => options;

    /// <summary>Resolves a feature service of the request scope.</summary>
    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Truncates long text to <see cref="TicketFlowMcpOptions.MaximumTextLength"/>; <paramref name="truncated"/> reports whether it did.</summary>
    public string Text(string value, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        truncated = value.Length > options.MaximumTextLength;
        if (!truncated)
            return value;
        var end = options.MaximumTextLength;
        if (char.IsHighSurrogate(value[end - 1]))
            end--;
        return value[..end];
    }

    /// <summary>The workspace for a call: the argument, else <see cref="TicketFlowMcpOptions.DefaultWorkspace"/>; <see langword="null"/> when neither.</summary>
    public WorkspaceId? Workspace(string? workspace)
    {
        var value = workspace ?? options.DefaultWorkspace;
        return value is null ? null : new WorkspaceId(value);
    }

    /// <summary>Resolves the caller for <paramref name="workspace"/>; <see langword="null"/> when unauthenticated or the scope does not match.</summary>
    public async ValueTask<PrincipalRef?> ResolvePrincipalAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var resolver = Services.GetService<ITicketFlowMcpPrincipalResolver>();
        if (resolver is null)
            return null;
        if (await resolver.ResolveAsync(new TicketFlowMcpCaller(User, Services), workspace, cancellationToken).ConfigureAwait(false) is not { } principal
            || principal.Value is null || principal.Scope.WorkspaceId != workspace)
            return null;
        return principal;
    }

    /// <summary>
    /// Runs <paramref name="body"/> as the caller of <paramref name="workspace"/>. The body returns the success payload (an object
    /// serialized as JSON) or a <see cref="TicketFlowToolError"/>.
    /// </summary>
    public async Task<CallToolResult> RunAsync(string? workspace, Func<PrincipalRef, Task<object>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var started = Stopwatch.GetTimestamp();
        CallToolResult result;
        try
        {
            result = await RunCoreAsync(workspace, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            result = Fail(TicketFlowToolError.Invalid([SafeMessage(error)]));
        }
        Log(result, Stopwatch.GetElapsedTime(started));
        return result;
    }

    private async Task<CallToolResult> RunCoreAsync(string? workspace, Func<PrincipalRef, Task<object>> body, CancellationToken cancellationToken)
    {
        if (Workspace(workspace) is not { } id)
            return Fail(new TicketFlowToolError("workspace-required", "Name a 'workspace' (this server has no default workspace)."));
        if (await ResolvePrincipalAsync(id, cancellationToken).ConfigureAwait(false) is not { } principal)
            return Fail(new TicketFlowToolError("unauthenticated", "The MCP caller could not be resolved to a TicketFlow principal for this workspace."));
        var outcome = await body(principal).ConfigureAwait(false);
        return outcome is TicketFlowToolError error ? Fail(error) : TicketFlowToolResults.Success(outcome, options.MaximumResultBytes);
    }

    /// <summary>An error result; forbidden errors carry the tool's required permissions and the built-in roles that grant them.</summary>
    public CallToolResult Fail(TicketFlowToolError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Code == "forbidden" && error.Details is null)
            error = error with
            {
                Details = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tool"] = Tool.Name,
                    ["requiredPermissions"] = Tool.Permissions,
                    ["anyOf"] = Tool.AnyOf,
                    ["grantedByBuiltInRoles"] = Tool.GrantedByBuiltInRoles,
                    ["hint"] = "Call explain_access with this tool (and the resource) to see which permission is missing. Access may also be limited by deny grants, conditions or team/inbox scopes.",
                },
            };
        return TicketFlowToolResults.Error(error);
    }

    private void Log(CallToolResult result, TimeSpan elapsed)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;
        var code = result.IsError == true && result.StructuredContent is { } content && content.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var value)
            ? value.GetString()
            : "ok";
        logger.LogDebug("TicketFlow MCP tool {Tool} completed with {Outcome} in {ElapsedMs} ms.", Tool.Name, code, (long)elapsed.TotalMilliseconds);
    }

    /// <summary>Validation messages are returned only when TicketFlow code produced them (they never carry input values).</summary>
    private static string SafeMessage(Exception error)
    {
        var origin = error.TargetSite?.DeclaringType?.Assembly.GetName().Name;
        return origin is not null && origin.StartsWith("TicketFlow.", StringComparison.Ordinal) && error.Message.Length <= 512 ? error.Message : "The request is invalid.";
    }
}
