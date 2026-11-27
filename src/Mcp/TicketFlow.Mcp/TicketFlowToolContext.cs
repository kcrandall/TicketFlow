using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Mcp;

/// <summary>
/// The per-call security wrapper every TicketFlow tool runs through (modelled on a host's <c>McpToolSecurity</c>): resolves the
/// requested workspace and the caller's <see cref="PrincipalRef"/> through <see cref="ITicketFlowMcpPrincipalResolver"/>, runs the
/// tool body as that principal, maps <see cref="TicketFlowToolError"/>s and TicketFlow validation exceptions to MCP error results,
/// bounds the result size and logs the tool name and outcome code only (never arguments, bodies or addresses).
/// </summary>
public sealed partial class TicketFlowToolContext
{
    private readonly TicketFlowMcpOptions options;
    private readonly ILogger logger;

    public TicketFlowToolContext(IServiceProvider services, ClaimsPrincipal? user, TicketFlowToolDescriptor tool)
        : this(services, user, tool,
            (services ?? throw new ArgumentNullException(nameof(services))).GetService<IOptions<TicketFlowMcpOptions>>()?.Value ?? new TicketFlowMcpOptions(),
            services.GetService<ILoggerFactory>()?.CreateLogger("TicketFlow.Mcp") ?? NullLogger.Instance)
    {
    }

    internal TicketFlowToolContext(IServiceProvider services, ClaimsPrincipal? user, TicketFlowToolDescriptor tool, TicketFlowMcpOptions options, ILogger logger)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        User = user;
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
        this.options = options;
        this.logger = logger;
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

    /// <summary>Resolves the caller for <paramref name="workspace"/>; <see langword="null"/> when unauthenticated, scope-mismatched, or backed by an inactive or out-of-scope service account.</summary>
    public async ValueTask<PrincipalRef?> ResolvePrincipalAsync(WorkspaceId workspace, CancellationToken cancellationToken)
    {
        var resolver = Services.GetService<ITicketFlowMcpPrincipalResolver>();
        if (resolver is null)
            return null;
        if (await resolver.ResolveAsync(new TicketFlowMcpCaller(User, Services), workspace, cancellationToken).ConfigureAwait(false) is not { } principal
            || principal.Scope.WorkspaceId != workspace)
            return null;
        if (!AccessPrincipalGuard.VerifyScopeClaims(User, principal.Scope))
            return null;
        if (!await AccessPrincipalGuard.IsServiceAccountActiveForScopeAsync(principal, principal.Scope, Services.GetService<IServiceAccountStore>(), cancellationToken).ConfigureAwait(false))
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
        var debug = logger.IsEnabled(LogLevel.Debug);
        var started = debug ? Stopwatch.GetTimestamp() : 0;
        CallToolResult result;
        try
        {
            result = await RunCoreAsync(workspace, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or OverflowException && !cancellationToken.IsCancellationRequested)
        {
            result = Fail(TicketFlowToolError.Invalid([SafeMessage(error)]));
        }
        if (debug)
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
        if (string.Equals(error.Code, "forbidden", StringComparison.Ordinal) && error.Tool is null)
            error = error with
            {
                Tool = Tool.Name,
                RequiredPermissions = Tool.Permissions,
                AnyOf = Tool.AnyOf,
                GrantedByBuiltInRoles = Tool.GrantedByBuiltInRoles,
                Hint = "Call explain_access with this tool (and the resource) to see which permission is missing. Access may also be limited by deny grants, conditions or team/inbox scopes.",
            };
        return TicketFlowToolResults.Error(error);
    }

    /// <summary>Wraps customer-authored or otherwise uncontrolled text (truncated to <see cref="TicketFlowMcpOptions.MaximumTextLength"/>) as <see cref="UntrustedContent"/>.</summary>
    public UntrustedContent Untrusted(string source, string text) => new(new UntrustedText(source, Text(text, out var truncated), truncated));

    public UntrustedContent? UntrustedOrNull(string source, string? text) => text is null ? null : Untrusted(source, text);

    private void Log(CallToolResult result, TimeSpan elapsed)
    {
        var code = result.IsError == true && result.StructuredContent is { } content && content.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var value)
            ? value.GetString() ?? "error"
            : "ok";
        LogCompleted(logger, Tool.Name, code, (long)elapsed.TotalMilliseconds);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "TicketFlow MCP tool {Tool} completed with {Outcome} in {ElapsedMs} ms.")]
    private static partial void LogCompleted(ILogger logger, string tool, string outcome, long elapsedMs);

    /// <summary>Validation messages are returned only when TicketFlow code produced them (they never carry input values).</summary>
    private static string SafeMessage(Exception error)
    {
        var origin = error.TargetSite?.DeclaringType?.Assembly.GetName().Name;
        return origin is not null && origin.StartsWith("TicketFlow.", StringComparison.Ordinal) && error.Message.Length <= 512 ? error.Message : "The request is invalid.";
    }
}
