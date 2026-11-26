using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace TicketFlow.Access.AspNetCore;

/// <summary>
/// Options for <see cref="ExternalIdentityAuthenticationHandler"/>. Zero-trust fail-closed: at least one
/// trust verification mechanism (shared secret, trusted proxy IP/CIDR, or custom validator) must be configured.
/// </summary>
public sealed class ExternalIdentityAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ExternalIdentity";

    public string? SharedSecretHeader { get; set; }

    public string? SharedSecret { get; set; }

    public IList<IPAddress> TrustedProxies { get; } = [];

    public IList<IPNetwork> TrustedProxyNetworks { get; } = [];

    public Func<HttpContext, ValueTask<bool>>? ValidateUpstreamRequest { get; set; }

    public string SubjectHeader { get; set; } = "X-Auth-Subject";

    public string? ClientIdHeader { get; set; } = "X-Auth-Client-Id";

    public string? PrincipalTypeHeader { get; set; } = "X-Auth-Principal-Type";

    public string? DisplayNameHeader { get; set; } = "X-Auth-Name";

    public string? EmailHeader { get; set; } = "X-Auth-Email";

    public string? TeamsHeader { get; set; } = "X-Auth-Groups";

    public string? RolesHeader { get; set; } = "X-Auth-Roles";

    public bool AcceptRoleHeaders { get; set; }

    public string? TenantHeader { get; set; } = "X-Auth-Tenant";

    public string? ProjectHeader { get; set; } = "X-Auth-Project";

    public string? EnvironmentHeader { get; set; } = "X-Auth-Environment";

    public string? AppHeader { get; set; } = "X-Auth-App";

    public string? WorkspaceHeader { get; set; } = "X-Auth-Workspace";

    public string ProviderName { get; set; } = "external";

    public bool HasTrustGate =>
        (!string.IsNullOrWhiteSpace(SharedSecretHeader) && !string.IsNullOrWhiteSpace(SharedSecret))
        || TrustedProxies.Count > 0
        || TrustedProxyNetworks.Count > 0
        || ValidateUpstreamRequest is not null;

    public ExternalIdentityAuthenticationOptions UseIdentityFlowDefaults(
        string? sharedSecret = null,
        bool acceptFederatedRoles = false)
    {
        ProviderName = "identityflow";
        SharedSecretHeader = "X-IdentityFlow-Proxy-Secret";
        SharedSecret = sharedSecret;
        SubjectHeader = "X-Auth-Subject";
        ClientIdHeader = "X-Auth-Client-Id";
        PrincipalTypeHeader = "X-Auth-Principal-Type";
        DisplayNameHeader = "X-Auth-Name";
        EmailHeader = "X-Auth-Email";
        TeamsHeader = "X-Auth-Groups";
        RolesHeader = "X-Auth-Roles";
        AcceptRoleHeaders = acceptFederatedRoles;
        TenantHeader = "X-Auth-Tenant";
        ProjectHeader = "X-Auth-Project";
        EnvironmentHeader = "X-Auth-Environment";
        AppHeader = "X-Auth-App";
        WorkspaceHeader = "X-Auth-Workspace";
        return this;
    }

    public ExternalIdentityAuthenticationOptions UseOAuth2ProxyDefaults(
        string? sharedSecret = null,
        bool acceptFederatedRoles = false)
    {
        ProviderName = "oauth2-proxy";
        SubjectHeader = "X-Forwarded-User";
        ClientIdHeader = null;
        PrincipalTypeHeader = null;
        EmailHeader = "X-Forwarded-Email";
        DisplayNameHeader = "X-Forwarded-Preferred-Username";
        TeamsHeader = "X-Forwarded-Groups";
        RolesHeader = acceptFederatedRoles ? "X-Forwarded-Groups" : null;
        AcceptRoleHeaders = acceptFederatedRoles;
        TenantHeader = null;
        ProjectHeader = null;
        EnvironmentHeader = null;
        AppHeader = null;
        WorkspaceHeader = null;
        if (!string.IsNullOrWhiteSpace(sharedSecret))
        {
            if (string.IsNullOrWhiteSpace(SharedSecretHeader))
                SharedSecretHeader = "X-OAuth2-Proxy-Secret";

            SharedSecret = sharedSecret;
        }

        return this;
    }
}
