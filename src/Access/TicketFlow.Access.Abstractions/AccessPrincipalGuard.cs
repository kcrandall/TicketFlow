using System.Security.Claims;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Shared scope-claim pinning and service-account status checks applied across HTTP endpoints, SignalR hubs, MCP, and UI sessions.
/// </summary>
public static class AccessPrincipalGuard
{
    /// <summary>
    /// Verifies that every scope-pinning claim present on <paramref name="principal"/> (<c>tenant_id</c>, <c>project_id</c>,
    /// <c>environment_id</c>/<c>environment</c>, <c>app_id</c>/<c>app_name</c>, <c>workspace_id</c>/<c>workspace</c>) matches
    /// <paramref name="scope"/>. Returns <see langword="true"/> when <paramref name="principal"/> is <see langword="null"/> or carries
    /// no scope-pinning claims.
    /// </summary>
    public static bool VerifyScopeClaims(ClaimsPrincipal? principal, TicketScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (principal is null)
            return true;

        foreach (var claim in principal.Claims)
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

    /// <summary>
    /// Verifies that an <see cref="ApiClientPrincipal"/> backed by <paramref name="serviceAccounts"/> (when registered) is enabled
    /// and applies to <paramref name="scope"/>. Returns <see langword="true"/> for non-API-client principals, when no store is
    /// provided, or when no service-account record exists for the client.
    /// </summary>
    public static async ValueTask<bool> IsServiceAccountActiveForScopeAsync(
        PrincipalRef principal,
        TicketScope scope,
        IServiceAccountStore? serviceAccounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(scope);
        if (principal is not ApiClientPrincipal client || serviceAccounts is null)
            return true;

        var stored = await serviceAccounts.FindAsync(AccessPartition.From(scope), client.ClientId, cancellationToken).ConfigureAwait(false);
        return stored is null || (stored.IsEnabled && stored.Account.AppliesTo(scope));
    }
}
