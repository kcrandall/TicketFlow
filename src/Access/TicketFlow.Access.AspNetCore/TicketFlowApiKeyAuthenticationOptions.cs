using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using TicketFlow.Core;

namespace TicketFlow.Access.AspNetCore;

/// <summary>Options for <see cref="TicketFlowApiKeyAuthenticationHandler"/>.</summary>
public sealed class TicketFlowApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";

    public const string DefaultHeaderName = "X-Api-Key";

    public string HeaderName { get; set; } = DefaultHeaderName;

    public bool AllowBearerToken { get; set; } = true;

    public TicketScope? DefaultScope { get; set; }

    public Func<HttpContext, ValueTask<TicketScope?>>? ResolveScope { get; set; }

    public string? WorkspaceRouteParameter { get; set; } = "workspaceId";

    public string? WorkspaceQueryParameter { get; set; } = "workspace";

    public string? WorkspaceCookieName { get; set; } = "tf-workspace";
}
