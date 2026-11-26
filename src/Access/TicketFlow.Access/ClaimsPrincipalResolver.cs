using System.Security.Claims;
using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Resolves <see cref="PrincipalRef"/> instances from <see cref="ClaimsPrincipal"/> claims, supporting
/// native IdentityFlow claims (<c>sub</c>, <c>identityflow:client_id</c>, <c>identityflow:primary_method</c>,
/// <c>role</c>, <see cref="ClaimTypes.Role"/>, <c>display_name</c>) as well as standard OAuth2/OIDC
/// <c>client_credentials</c> and M2M tokens (<c>client_id</c>, <c>azp</c>, <c>gty</c>/<c>grant_type</c>, <c>principal_type</c>).
/// </summary>
public sealed class ClaimsPrincipalResolver : IAccessPrincipalResolver
{
    private static readonly string[] ClientPrincipalTypeValues =
    [
        "service_account",
        "api_client",
        "client",
        "client_credentials",
        "client-credentials",
    ];

    private readonly ClaimsPrincipalResolverOptions options;

    public ClaimsPrincipalResolver(IOptions<ClaimsPrincipalResolverOptions>? options = null) =>
        this.options = options?.Value ?? new ClaimsPrincipalResolverOptions();

    public ValueTask<PrincipalRef?> ResolveAsync(ClaimsPrincipal claimsPrincipal, TicketScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(claimsPrincipal);
        ArgumentNullException.ThrowIfNull(scope);

        if (claimsPrincipal.Identity?.IsAuthenticated != true)
            return ValueTask.FromResult<PrincipalRef?>(null);

        var externalRoles = ExtractExternalRoles(claimsPrincipal);

        if (IsApiKeyAuthentication(claimsPrincipal))
        {
            var apiKeyClientId = FindFirstValidIdentifier(claimsPrincipal, options.ClientIdClaimTypes);
            if (apiKeyClientId is null)
                return ValueTask.FromResult<PrincipalRef?>(null);

            return ValueTask.FromResult<PrincipalRef?>(
                new ApiClientPrincipal(scope, new ApiClientId(apiKeyClientId), externalRoles));
        }

        var clientId = FindFirstValidIdentifier(claimsPrincipal, options.ClientIdClaimTypes);
        var subject = FindFirstValidIdentifier(claimsPrincipal, options.SubjectClaimTypes);

        if (HasClientPrincipalTypeClaim(claimsPrincipal))
        {
            if (clientId is null)
                return ValueTask.FromResult<PrincipalRef?>(null);

            return ValueTask.FromResult<PrincipalRef?>(
                new ApiClientPrincipal(scope, new ApiClientId(clientId), externalRoles));
        }

        if (clientId is not null
            && (subject is null
                || string.Equals(subject, clientId, StringComparison.Ordinal)
                || string.Equals(subject, $"{clientId}@clients", StringComparison.OrdinalIgnoreCase)))
            return ValueTask.FromResult<PrincipalRef?>(
                new ApiClientPrincipal(scope, new ApiClientId(clientId), externalRoles));

        if (subject is null)
            return ValueTask.FromResult<PrincipalRef?>(null);

        var teams = ExtractTeams(claimsPrincipal);
        return ValueTask.FromResult<PrincipalRef?>(
            new UserPrincipal(scope, new UserId(subject), teams, externalRoles));
    }

    /// <summary>Extracts a validated display name from the configured <see cref="ClaimsPrincipalResolverOptions.DisplayNameClaimTypes"/>.</summary>
    public string? TryGetDisplayName(ClaimsPrincipal claimsPrincipal)
    {
        ArgumentNullException.ThrowIfNull(claimsPrincipal);
        return FindFirstValidIdentifier(claimsPrincipal, options.DisplayNameClaimTypes);
    }

    private bool IsApiKeyAuthentication(ClaimsPrincipal principal)
    {
        if (IsApiKeyMethod(principal.Identity?.AuthenticationType))
            return true;

        foreach (var claim in principal.Claims)
            if (ContainsClaimType(options.AuthMethodClaimTypes, claim.Type, ignoreCase: true) && IsApiKeyMethod(claim.Value))
                return true;

        return false;
    }

    private bool HasClientPrincipalTypeClaim(ClaimsPrincipal principal)
    {
        foreach (var claim in principal.Claims)
            if (ContainsClaimType(options.PrincipalTypeClaimTypes, claim.Type, ignoreCase: true) && IsClientPrincipalTypeValue(claim.Value))
                return true;

        return false;
    }

    private static bool IsApiKeyMethod(string? value) =>
        string.Equals(value, "ApiKey", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "api_key", StringComparison.OrdinalIgnoreCase);

    private static bool IsClientPrincipalTypeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        foreach (var candidate in ClientPrincipalTypeValues)
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool ContainsClaimType(IList<string> claimTypes, string claimType, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < claimTypes.Count; i++)
            if (string.Equals(claimTypes[i], claimType, comparison))
                return true;

        return false;
    }

    private static string? FindFirstValidIdentifier(ClaimsPrincipal principal, IList<string> claimTypes)
    {
        for (var i = 0; i < claimTypes.Count; i++)
        {
            var claimType = claimTypes[i];
            foreach (var claim in principal.Claims)
                if (string.Equals(claim.Type, claimType, StringComparison.Ordinal)
                    && AccessText.IsValidText(claim.Value, TicketScope.MaximumIdentifierLength))
                {
                    var trimmed = claim.Value.Trim();
                    if (AccessText.IsValidText(trimmed, TicketScope.MaximumIdentifierLength))
                        return trimmed;
                }
        }

        return null;
    }

    private List<TeamId> ExtractTeams(ClaimsPrincipal principal)
    {
        var teams = new List<TeamId>();
        var seen = new HashSet<TeamId>();

        foreach (var claim in principal.Claims)
        {
            if (!ContainsClaimType(options.TeamClaimTypes, claim.Type, ignoreCase: false))
                continue;

            if (string.IsNullOrWhiteSpace(claim.Value))
                continue;

            var parts = claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (teams.Count >= UserPrincipal.MaximumTeamMemberships)
                    return teams;

                if (!AccessText.IsValidText(part, TicketScope.MaximumIdentifierLength))
                    continue;

                var teamId = new TeamId(part);
                if (seen.Add(teamId))
                    teams.Add(teamId);
            }
        }

        return teams;
    }

    private List<string> ExtractExternalRoles(ClaimsPrincipal principal)
    {
        var roles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in principal.Claims)
        {
            if (!ContainsClaimType(options.RoleClaimTypes, claim.Type, ignoreCase: false))
                continue;

            if (string.IsNullOrWhiteSpace(claim.Value))
                continue;

            var parts = claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (roles.Count >= PrincipalRef.MaximumExternalRoles)
                    return roles;

                if (!AccessText.IsValidText(part, TicketScope.MaximumIdentifierLength))
                    continue;

                if (seen.Add(part))
                    roles.Add(part);
            }
        }

        return roles;
    }
}
