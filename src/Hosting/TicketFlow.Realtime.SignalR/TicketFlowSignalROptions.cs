using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Realtime.SignalR;

/// <summary>Maps the host-authenticated user of an agent connection to a TicketFlow principal (application-owned; never trusts client input).</summary>
public interface IRealtimePrincipalResolver
{
    /// <summary>The principal, or <see langword="null"/> to reject the connection.</summary>
    ValueTask<PrincipalRef?> ResolveAsync(ClaimsPrincipal user, HttpContext? httpContext, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the partition (tenant, project, environment, app) visitors connect to, from host routing configuration.</summary>
public interface IVisitorPartitionResolver
{
    /// <summary>The partition, or <see langword="null"/> to reject the request.</summary>
    AccessPartition? Resolve(HttpContext? httpContext);
}

public sealed class TicketFlowSignalROptions
{
    /// <summary>Fallback for <see cref="IRealtimePrincipalResolver"/> when the application registers none.</summary>
    public Func<ClaimsPrincipal, HttpContext?, ValueTask<PrincipalRef?>>? ResolvePrincipal { get; set; }

    /// <summary>The partition visitors connect to when the application registers no <see cref="IVisitorPartitionResolver"/> (single-partition hosts).</summary>
    public AccessPartition? VisitorPartition { get; set; }

    /// <summary>
    /// Allowed browser origins of agent hub connections (exact <c>scheme://host[:port]</c>). When non-empty, connections whose
    /// <c>Origin</c> header is present and not listed are aborted (cross-site WebSocket hijacking defense for cookie-authenticated hosts).
    /// Requests without an Origin (non-browser clients) are accepted unless <see cref="RequireAgentOrigin"/> is set.
    /// </summary>
    public IList<string> AgentAllowedOrigins { get; } = [];

    /// <summary>Reject agent connections without an Origin header (default <see langword="false"/>).</summary>
    public bool RequireAgentOrigin { get; set; }

    /// <summary>Minimum interval between typing events per connection and conversation (0 to 1 minute, default 2 seconds).</summary>
    public TimeSpan TypingThrottleInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum attachment upload request size accepted by the upload endpoint before the widget policy applies (default 100 MiB).</summary>
    public long MaximumUploadBytes { get; set; } = 100L * 1024 * 1024;
}

internal sealed class OptionsPrincipalResolver(Microsoft.Extensions.Options.IOptions<TicketFlowSignalROptions> options) : IRealtimePrincipalResolver
{
    public ValueTask<PrincipalRef?> ResolveAsync(ClaimsPrincipal user, HttpContext? httpContext, CancellationToken cancellationToken = default) =>
        options.Value.ResolvePrincipal is { } resolve ? resolve(user, httpContext) : ValueTask.FromResult<PrincipalRef?>(null);
}

internal sealed class OptionsPartitionResolver(Microsoft.Extensions.Options.IOptions<TicketFlowSignalROptions> options) : IVisitorPartitionResolver
{
    public AccessPartition? Resolve(HttpContext? httpContext) => options.Value.VisitorPartition;
}
