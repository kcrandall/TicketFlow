using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Identity;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.IdentityFlow;

/// <summary>
/// Resolves TicketFlow principals from IdentityFlow-authenticated requests (<see cref="IdentityFlowClaimsMapper"/>). Users are looked up
/// through the optional <see cref="IStaffIdentityResolver"/> first (provider <see cref="IdentityFlowPrincipalOptions.StaffIdentityProvider"/>,
/// the normalized subject); unmapped users fall back to the prefixed subject or are refused per
/// <see cref="IdentityFlowPrincipalOptions.UnmappedStaff"/>. Users get a <see cref="UserPrincipal"/> with the workspace's current team
/// snapshot from <see cref="ITeamMembershipResolver"/>; API clients an <see cref="ApiClientPrincipal"/>. Authorization stays with Access.
/// </summary>
public sealed class IdentityFlowPrincipalResolver(ITeamMembershipResolver teams, IOptions<IdentityFlowPrincipalOptions> options, IStaffIdentityResolver? staff = null)
    : ITicketFlowPrincipalResolver
{
    public async ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        return await ResolveAsync(httpContext.User, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps <paramref name="user"/> within <paramref name="scope"/>; <see langword="null"/> when no principal applies.</summary>
    public async ValueTask<PrincipalRef?> ResolveAsync(ClaimsPrincipal user, TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        switch (IdentityFlowClaimsMapper.Map(user, options.Value))
        {
            case { Kind: IdentityFlowPrincipalKind.User } identity:
                var userId = new UserId(identity.Id);
                if (staff is not null)
                {
                    StaffIdentityRef reference;
                    try
                    {
                        reference = new StaffIdentityRef(options.Value.StaffIdentityProvider, identity.Subject);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                    if (await staff.ResolveUserAsync(scope, reference, cancellationToken).ConfigureAwait(false) is { } linked)
                        userId = linked;
                    else if (options.Value.UnmappedStaff == IdentityFlowUnmappedStaff.Refuse)
                        return null;
                }
                return await teams.ResolveUserPrincipalAsync(scope, userId, cancellationToken).ConfigureAwait(false);
            case { Kind: IdentityFlowPrincipalKind.ApiClient } identity:
                if (IsTicketFlowApiKey(user) && !VerifyScopeClaims(user, scope))
                    return null;
                return new ApiClientPrincipal(scope, new ApiClientId(identity.Id));
            default:
                return null;
        }
    }

    private static bool IsTicketFlowApiKey(ClaimsPrincipal? user) =>
        user?.Identity is { IsAuthenticated: true } identity
        && string.Equals(identity.AuthenticationType, IdentityFlowClaimsMapper.TicketFlowApiKeyScheme, StringComparison.Ordinal)
        && user.HasClaim(static c => string.Equals(c.Type, IdentityFlowClaimsMapper.TicketFlowApiKeyIdClaim, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(c.Value));

    private static bool VerifyScopeClaims(ClaimsPrincipal? user, TicketScope scope)
    {
        if (user is null)
            return false;
        foreach (var claim in user.Claims)
        {
            var expected = claim.Type switch
            {
                "tenant_id" => scope.TenantId,
                "project_id" => scope.ProjectId,
                "environment_id" or "environment" => scope.EnvironmentId,
                "app_id" or "app_name" => scope.AppId,
                "workspace_id" or "workspace" => scope.WorkspaceId.Value,
                _ => null,
            };
            if (expected is null)
                continue;
            var trimmed = claim.Value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !string.Equals(trimmed, expected, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
