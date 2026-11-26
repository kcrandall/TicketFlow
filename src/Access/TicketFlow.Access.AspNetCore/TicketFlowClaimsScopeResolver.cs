using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TicketFlow.Core;

namespace TicketFlow.Access.AspNetCore;

/// <summary>Resolves a <see cref="TicketScope"/> from claims on an authenticated <see cref="ClaimsPrincipal"/>.</summary>
public static class TicketFlowClaimsScopeResolver
{
    public const string TenantClaimType = "tenant_id";
    public const string ProjectClaimType = "project_id";
    public const string EnvironmentClaimType = "environment_id";
    public const string LegacyEnvironmentClaimType = "environment";
    public const string AppClaimType = "app_id";
    public const string LegacyAppClaimType = "app_name";
    public const string WorkspaceClaimType = "workspace_id";
    public const string LegacyWorkspaceClaimType = "workspace";

    public static TicketScope? FromClaims(HttpContext http, TicketScope? fallbackScope = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        return FromPrincipal(http.User, fallbackScope);
    }

    public static TicketScope? FromPrincipal(ClaimsPrincipal? principal, TicketScope? fallbackScope = null)
    {
        if (!TryResolveDimension(principal, TenantClaimType, legacyType: null, out var claimedTenant)
            || !TryResolveDimension(principal, ProjectClaimType, legacyType: null, out var claimedProject)
            || !TryResolveDimension(principal, EnvironmentClaimType, LegacyEnvironmentClaimType, out var claimedEnvironment)
            || !TryResolveDimension(principal, AppClaimType, LegacyAppClaimType, out var claimedApp)
            || !TryResolveDimension(principal, WorkspaceClaimType, LegacyWorkspaceClaimType, out var claimedWorkspace))
            return null;

        var canInherit = fallbackScope is not null;

        var tenantId = claimedTenant ?? (canInherit ? fallbackScope!.TenantId : null);
        if (tenantId is null)
            return null;
        if (canInherit && !string.Equals(tenantId, fallbackScope!.TenantId, StringComparison.Ordinal))
            canInherit = false;

        var projectId = claimedProject ?? (canInherit ? fallbackScope!.ProjectId : null);
        if (projectId is null)
            return null;
        if (canInherit && !string.Equals(projectId, fallbackScope!.ProjectId, StringComparison.Ordinal))
            canInherit = false;

        var environmentId = claimedEnvironment ?? (canInherit ? fallbackScope!.EnvironmentId : null);
        if (environmentId is null)
            return null;
        if (canInherit && !string.Equals(environmentId, fallbackScope!.EnvironmentId, StringComparison.Ordinal))
            canInherit = false;

        var appId = claimedApp ?? (canInherit ? fallbackScope!.AppId : null);
        if (appId is null)
            return null;
        if (canInherit && !string.Equals(appId, fallbackScope!.AppId, StringComparison.Ordinal))
            canInherit = false;

        WorkspaceId workspaceId;
        if (claimedWorkspace is not null)
            workspaceId = new WorkspaceId(claimedWorkspace);
        else if (canInherit)
            workspaceId = fallbackScope!.WorkspaceId;
        else
            return null;

        return new TicketScope(tenantId, projectId, environmentId, appId, workspaceId);
    }

    private static bool TryResolveDimension(ClaimsPrincipal? principal, string primaryType, string? legacyType, out string? value)
    {
        value = null;
        if (principal is null)
            return true;

        foreach (var claim in principal.Claims)
        {
            if (!string.Equals(claim.Type, primaryType, StringComparison.Ordinal)
                && (legacyType is null || !string.Equals(claim.Type, legacyType, StringComparison.Ordinal)))
                continue;

            var trimmed = claim.Value?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !AccessText.IsValidText(trimmed, TicketScope.MaximumIdentifierLength))
            {
                value = null;
                return false;
            }

            if (value is null)
                value = trimmed;
            else if (!string.Equals(value, trimmed, StringComparison.Ordinal))
            {
                value = null;
                return false;
            }
        }

        return true;
    }
}
