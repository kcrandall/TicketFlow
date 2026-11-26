using System.Security.Claims;

namespace TicketFlow.Access;

/// <summary>Configures claim types inspected by <see cref="ClaimsPrincipalResolver"/>.</summary>
public sealed class ClaimsPrincipalResolverOptions
{
    public IList<string> SubjectClaimTypes { get; } = ["sub", ClaimTypes.NameIdentifier];

    public IList<string> ClientIdClaimTypes { get; } = ["client_id", "identityflow:client_id", "azp"];

    public IList<string> TeamClaimTypes { get; } = ["team", "teams", "group", "groups", ClaimTypes.GroupSid];

    public IList<string> RoleClaimTypes { get; } = ["role", "roles", ClaimTypes.Role];

    public IList<string> AuthMethodClaimTypes { get; } =
        ["amr", "auth_method", "identityflow:primary_method", "identityflow_auth_method", ClaimTypes.AuthenticationMethod];

    public IList<string> PrincipalTypeClaimTypes { get; } = ["principal_type", "actor_type", "gty", "grant_type"];

    public IList<string> DisplayNameClaimTypes { get; } = ["display_name", "name", ClaimTypes.Name];
}
