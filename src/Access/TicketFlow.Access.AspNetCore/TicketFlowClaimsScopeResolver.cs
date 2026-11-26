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
        var tenantId = FindClaimValue(principal, TenantClaimType) ?? fallbackScope?.TenantId;
        var projectId = FindClaimValue(principal, ProjectClaimType) ?? fallbackScope?.ProjectId;
        var environmentId = FindClaimValue(principal, EnvironmentClaimType)
            ?? FindClaimValue(principal, LegacyEnvironmentClaimType)
            ?? fallbackScope?.EnvironmentId;
        var appId = FindClaimValue(principal, AppClaimType)
            ?? FindClaimValue(principal, LegacyAppClaimType)
            ?? fallbackScope?.AppId;
        var workspaceRaw = FindClaimValue(principal, WorkspaceClaimType)
            ?? FindClaimValue(principal, LegacyWorkspaceClaimType);

        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(environmentId)
            || string.IsNullOrWhiteSpace(appId))
            return null;

        WorkspaceId workspaceId;
        if (workspaceRaw is not null)
            workspaceId = new WorkspaceId(workspaceRaw);
        else if (fallbackScope is not null)
            workspaceId = fallbackScope.WorkspaceId;
        else
            return null;

        return new TicketScope(tenantId, projectId, environmentId, appId, workspaceId);
    }

    private static string? FindClaimValue(ClaimsPrincipal? principal, string claimType)
    {
        if (principal is null)
            return null;

        foreach (var claim in principal.Claims)
            if (string.Equals(claim.Type, claimType, StringComparison.Ordinal)
                && AccessText.IsValidText(claim.Value, TicketScope.MaximumIdentifierLength))
            {
                var trimmed = claim.Value.Trim();
                if (AccessText.IsValidText(trimmed, TicketScope.MaximumIdentifierLength))
                    return trimmed;
            }

        return null;
    }
}
