using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace TicketFlow.Access.AspNetCore;

/// <summary>
/// Zero-trust fail-closed external identity authentication handler for reverse-proxy / identity-gateway termination
/// (such as IdentityFlow or OAuth2-Proxy).
/// </summary>
public sealed class ExternalIdentityAuthenticationHandler(
    IOptionsMonitor<ExternalIdentityAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ExternalIdentityAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var trustFailure = await VerifyTrustAsync().ConfigureAwait(false);
        if (trustFailure is not null)
            return AuthenticateResult.Fail(trustFailure);

        if (!TryReadSingleHeader(Request, Options.SubjectHeader, 256, out var subject, out var error)
            || !TryReadSingleHeader(Request, Options.ClientIdHeader, 256, out var clientId, out error)
            || !TryReadSingleHeader(Request, Options.PrincipalTypeHeader, 64, out var principalType, out error)
            || !TryReadSingleHeader(Request, Options.DisplayNameHeader, 256, out var displayName, out error, allowCommas: true)
            || !TryReadSingleHeader(Request, Options.EmailHeader, 320, out var email, out error)
            || !TryReadSingleHeader(Request, Options.TenantHeader, 256, out var tenant, out error)
            || !TryReadSingleHeader(Request, Options.ProjectHeader, 256, out var project, out error)
            || !TryReadSingleHeader(Request, Options.EnvironmentHeader, 256, out var environment, out error)
            || !TryReadSingleHeader(Request, Options.AppHeader, 256, out var app, out error)
            || !TryReadSingleHeader(Request, Options.WorkspaceHeader, 256, out var workspace, out error))
            return AuthenticateResult.Fail(error!);

        if (!TryReadMultiHeader(Request, Options.TeamsHeader, 256, out var teams, out error))
            return AuthenticateResult.Fail(error!);

        IReadOnlyList<string> roles = [];
        if (Options.AcceptRoleHeaders && !TryReadMultiHeader(Request, Options.RolesHeader, 256, out roles, out error))
            return AuthenticateResult.Fail(error!);

        if (subject is null && clientId is null)
            return AuthenticateResult.NoResult();

        var claims = new List<Claim>();
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        }

        if (clientId is not null)
            claims.Add(new Claim("client_id", clientId));

        if (principalType is not null)
            claims.Add(new Claim("principal_type", principalType));

        if (displayName is not null)
        {
            claims.Add(new Claim("display_name", displayName));
            claims.Add(new Claim("name", displayName));
            claims.Add(new Claim(ClaimTypes.Name, displayName));
        }

        if (email is not null)
        {
            claims.Add(new Claim("email", email));
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        foreach (var team in teams)
            claims.Add(new Claim("groups", team));

        if (Options.AcceptRoleHeaders)
            foreach (var role in roles)
                claims.Add(new Claim("role", role));

        if (tenant is not null)
            claims.Add(new Claim(TicketFlowClaimsScopeResolver.TenantClaimType, tenant));

        if (project is not null)
            claims.Add(new Claim(TicketFlowClaimsScopeResolver.ProjectClaimType, project));

        if (environment is not null)
            claims.Add(new Claim(TicketFlowClaimsScopeResolver.EnvironmentClaimType, environment));

        if (app is not null)
            claims.Add(new Claim(TicketFlowClaimsScopeResolver.AppClaimType, app));

        if (workspace is not null)
            claims.Add(new Claim(TicketFlowClaimsScopeResolver.WorkspaceClaimType, workspace));

        if (!string.IsNullOrWhiteSpace(Options.ProviderName) && AccessText.IsValidText(Options.ProviderName, 128))
            claims.Add(new Claim("idp", Options.ProviderName.Trim()));

        claims.Add(new Claim("amr", "external"));

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, "role");
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private async ValueTask<string?> VerifyTrustAsync()
    {
        var hasSharedSecretValue = !string.IsNullOrWhiteSpace(Options.SharedSecret);
        var hasSharedSecretHeader = !string.IsNullOrWhiteSpace(Options.SharedSecretHeader);
        if (hasSharedSecretValue && !hasSharedSecretHeader)
            return "External identity shared secret is configured without SharedSecretHeader.";

        var hasSharedSecret = hasSharedSecretValue && hasSharedSecretHeader;
        var hasProxyAllowlist = Options.TrustedProxies.Count > 0 || Options.TrustedProxyNetworks.Count > 0;
        var hasCustomValidator = Options.ValidateUpstreamRequest is not null;

        if (!hasSharedSecret && !hasProxyAllowlist && !hasCustomValidator)
            return "External identity authentication requires at least one trust verification mechanism.";

        if (hasProxyAllowlist && !IsFromTrustedProxy(Context.Connection.RemoteIpAddress))
            return "Upstream request did not originate from a trusted proxy.";

        if (hasSharedSecret)
        {
            if (!Request.Headers.TryGetValue(Options.SharedSecretHeader!, out var secretValues) || secretValues.Count == 0)
                return "Missing upstream trust secret header.";

            if (secretValues.Count > 1)
                return "Multiple upstream trust secret headers are not permitted.";

            var providedSecret = secretValues[0];
            if (string.IsNullOrEmpty(providedSecret))
                return "Missing upstream trust secret header.";

            if (!FixedTimeHashEquals(Options.SharedSecret!, providedSecret))
                return "Invalid upstream trust secret.";
        }

        if (hasCustomValidator)
        {
            var customValid = await Options.ValidateUpstreamRequest!(Context).ConfigureAwait(false);
            if (!customValid)
                return "Upstream request failed trust verification.";
        }

        return null;
    }

    private bool IsFromTrustedProxy(IPAddress? remoteIp)
    {
        if (remoteIp is null)
            return false;

        var normalizedRemote = NormalizeIpAddress(remoteIp);

        for (var i = 0; i < Options.TrustedProxies.Count; i++)
        {
            var trusted = Options.TrustedProxies[i];
            if (trusted is not null && NormalizeIpAddress(trusted).Equals(normalizedRemote))
                return true;
        }

        for (var i = 0; i < Options.TrustedProxyNetworks.Count; i++)
        {
            var network = Options.TrustedProxyNetworks[i];
            if (network.Contains(normalizedRemote)
                || (!ReferenceEquals(normalizedRemote, remoteIp) && network.Contains(remoteIp)))
                return true;
        }

        return false;
    }

    private static IPAddress NormalizeIpAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool FixedTimeHashEquals(string expected, string provided)
    {
        Span<byte> expectedHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> providedHash = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(provided), providedHash);

        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }

    private static bool TryReadSingleHeader(
        HttpRequest request,
        string? headerName,
        int maxLength,
        out string? value,
        out string? error,
        bool allowCommas = false)
    {
        value = null;
        error = null;

        if (string.IsNullOrWhiteSpace(headerName))
            return true;

        if (!request.Headers.TryGetValue(headerName, out var values) || values.Count == 0)
            return true;

        var resolution = ResolveSingleValue(values, allowCommas);
        if (resolution.IsAmbiguous)
        {
            error = $"Multiple distinct '{headerName}' headers are not permitted.";
            return false;
        }

        if (resolution.Value is null)
            return true;

        if (!AccessText.IsValidText(resolution.Value, maxLength))
        {
            error = $"Header '{headerName}' contains invalid characters or exceeds maximum length.";
            return false;
        }

        value = resolution.Value;
        return true;
    }

    private static bool TryReadMultiHeader(
        HttpRequest request,
        string? headerName,
        int maxLength,
        out IReadOnlyList<string> values,
        out string? error)
    {
        values = [];
        error = null;

        if (string.IsNullOrWhiteSpace(headerName))
            return true;

        if (!request.Headers.TryGetValue(headerName, out var headerValues) || headerValues.Count == 0)
            return true;

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < headerValues.Count; i++)
        {
            var raw = headerValues[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (part.Length == 0)
                    continue;

                if (!AccessText.IsValidText(part, maxLength))
                {
                    error = $"Header '{headerName}' contains invalid characters or exceeds maximum length.";
                    return false;
                }

                if (seen.Add(part))
                    results.Add(part);
            }
        }

        values = results.Count == 0 ? [] : results.AsReadOnly();
        return true;
    }

    private static (string? Value, bool IsAmbiguous) ResolveSingleValue(StringValues values, bool allowCommas = false)
    {
        string? resolved = null;

        for (var i = 0; i < values.Count; i++)
        {
            var raw = values[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (!allowCommas && raw.Contains(',', StringComparison.Ordinal))
            {
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                    if (resolved is null)
                        resolved = part;
                    else if (!string.Equals(resolved, part, StringComparison.Ordinal))
                        return (null, true);
                continue;
            }

            var trimmed = raw.Trim();
            if (resolved is null)
                resolved = trimmed;
            else if (!string.Equals(resolved, trimmed, StringComparison.Ordinal))
                return (null, true);
        }

        return (resolved, false);
    }
}
