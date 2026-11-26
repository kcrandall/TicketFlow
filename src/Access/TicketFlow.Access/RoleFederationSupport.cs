using TicketFlow.Core;

namespace TicketFlow.Access;

internal static class RoleFederationSupport
{
    public static bool IsAdministrativePermission(Permission permission) =>
        permission == AccessPermissions.AccessManage
        || permission == AccessPermissions.AccessManageTenant
        || permission == AccessPermissions.AccessBreakGlass;

    public static HashSet<string> CollectCandidateExternalRoles(PrincipalRef principal, RoleFederationOptions options)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in principal.ExternalRoles)
            candidates.Add(role);

        if (options.MapTeamsAsExternalRoles && principal is UserPrincipal user)
            foreach (var team in user.Teams)
                candidates.Add(team.Value);

        return candidates;
    }

    private static bool MatchesMappingProvider(string mappingProvider, string? principalProvider, bool matchedByTeamOnly)
    {
        if (matchedByTeamOnly)
            return string.Equals(mappingProvider, "local", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(mappingProvider, "*", StringComparison.Ordinal))
            return true;

        return principalProvider is not null && string.Equals(mappingProvider, principalProvider, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlyList<ResolvedGrant>> ResolveFederatedGrantsAsync(
        PrincipalRef principal,
        TicketScope scope,
        RoleFederationOptions options,
        IRoleFederationMappingStore? mappingStore,
        IAccessRoleStore roleStore,
        IPermissionCatalog catalog,
        Dictionary<RoleId, AccessRole>? existingCustomRoles,
        CancellationToken cancellationToken)
    {
        if (options.Mode == RoleFederationMode.Disabled)
            return [];

        GrantSubject? subject = principal switch
        {
            UserPrincipal user => user.UserId,
            ApiClientPrincipal client => client.ClientId,
            AutomationPrincipal => null,
            SystemPrincipal => null,
        };
        if (subject is null)
            return [];

        HashSet<string>? teamTokens = null;
        if (options.MapTeamsAsExternalRoles && principal is UserPrincipal userPrincipal && userPrincipal.Teams.Count > 0)
        {
            teamTokens = new HashSet<string>(userPrincipal.Teams.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var team in userPrincipal.Teams)
                teamTokens.Add(team.Value);
        }

        var candidateRoles = CollectCandidateExternalRoles(principal, options);
        if (candidateRoles.Count == 0)
            return [];

        var partition = AccessPartition.From(scope);
        var matches = new List<(RoleId RoleId, GrantScope? ExplicitScope)>();
        var seen = new HashSet<(RoleId RoleId, GrantScope? ExplicitScope)>();

        void AddMatch(RoleId roleId, GrantScope? explicitScope)
        {
            if (options.AllowedRoles.Count > 0 && !options.AllowedRoles.Contains(roleId))
                return;
            if (seen.Add((roleId, explicitScope)))
                matches.Add((roleId, explicitScope));
        }

        foreach (var token in candidateRoles)
            if (options.StaticMappings.TryGetValue(token, out var staticRoleId))
                AddMatch(staticRoleId, explicitScope: null);

        if (mappingStore is not null)
        {
            var storedMappings = await mappingStore
                .ListForPartitionAsync(partition, includeDisabled: false, cancellationToken)
                .ConfigureAwait(false);
            foreach (var stored in storedMappings)
            {
                if (!stored.IsEnabled
                    || stored.Mapping.Partition != partition
                    || (stored.Mapping.Workspace is { } mappingWorkspace && mappingWorkspace != scope.WorkspaceId))
                    continue;

                var externalRole = stored.Mapping.ExternalRole;
                var inExternalRoles = principal.ExternalRoles.Contains(externalRole);
                var inTeamTokens = teamTokens is not null && teamTokens.Contains(externalRole);
                if (!inExternalRoles && !inTeamTokens)
                    continue;

                var providerMatched = (inExternalRoles && MatchesMappingProvider(stored.Mapping.Provider, principal.IdentityProvider, matchedByTeamOnly: false))
                    || (inTeamTokens && MatchesMappingProvider(stored.Mapping.Provider, principal.IdentityProvider, matchedByTeamOnly: true));
                if (!providerMatched)
                    continue;

                AddMatch(stored.Mapping.RoleId, stored.Mapping.Scope);
            }
        }

        if (options.AllowDirectRoleIdMatching)
            foreach (var token in candidateRoles)
                if (RoleId.TryParse(token.Trim().ToLowerInvariant(), out var directRoleId))
                    AddMatch(directRoleId, explicitScope: null);

        if (matches.Count == 0)
            return [];

        Dictionary<RoleId, AccessRole>? customRoles = existingCustomRoles;
        if (customRoles is null)
        {
            var needsCustom = false;
            for (var i = 0; i < matches.Count; i++)
                if (matches[i].ExplicitScope is not TenantGrantScope && !BuiltInRoles.IsBuiltIn(matches[i].RoleId))
                {
                    needsCustom = true;
                    break;
                }

            if (needsCustom)
            {
                var list = await roleStore.ListAsync(scope, cancellationToken).ConfigureAwait(false);
                customRoles = new Dictionary<RoleId, AccessRole>(list.Count);
                for (var i = 0; i < list.Count; i++)
                    customRoles[list[i].Role.Id] = list[i].Role;
            }
        }

        matches.Sort(static (left, right) => left.RoleId.CompareTo(right.RoleId));
        var synthesized = new List<ResolvedGrant>(matches.Count);
        var defaultWorkspaceScope = new WorkspaceGrantScope(scope.WorkspaceId);

        for (var i = 0; i < matches.Count; i++)
        {
            var (roleId, explicitScope) = matches[i];
            var role = BuiltInRoles.Find(roleId)
                ?? (explicitScope is TenantGrantScope ? null : customRoles?.GetValueOrDefault(roleId));
            if (role is null)
                continue;

            if (explicitScope is TenantGrantScope && !role.IsBuiltIn)
                continue;

            var grantScope = DeriveGrantScope(defaultWorkspaceScope, role, explicitScope);
            var resolved = CreateVirtualGrant(partition, subject.Value, role, grantScope, i, options.AllowAdministrativeRoles, catalog);
            if (resolved is not null)
                synthesized.Add(resolved);
        }

        return synthesized;
    }

    private static GrantScope DeriveGrantScope(WorkspaceGrantScope defaultWorkspaceScope, AccessRole role, GrantScope? explicitScope) =>
        explicitScope is TenantGrantScope && role.IsBuiltIn
            ? TenantGrantScope.Instance
            : explicitScope ?? defaultWorkspaceScope;

    private static ResolvedGrant? CreateVirtualGrant(
        AccessPartition partition,
        GrantSubject subject,
        AccessRole role,
        GrantScope grantScope,
        int index,
        bool allowAdministrativeRoles,
        IPermissionCatalog catalog)
    {
        HashSet<Permission>? stripped = null;
        if (!allowAdministrativeRoles)
        {
            var hasAdmin = role.IncludesAllPermissions
                || role.Permissions.Contains(AccessPermissions.AccessManage)
                || role.Permissions.Contains(AccessPermissions.AccessManageTenant)
                || role.Permissions.Contains(AccessPermissions.AccessBreakGlass);
            if (hasAdmin)
            {
                stripped = [];
                if (role.IncludesAllPermissions)
                {
                    var defs = catalog.Definitions;
                    for (var i = 0; i < defs.Count; i++)
                    {
                        var permission = defs[i].Permission;
                        if (!IsAdministrativePermission(permission))
                            stripped.Add(permission);
                    }
                }
                else
                {
                    foreach (var permission in role.Permissions)
                        if (catalog.Contains(permission) && !IsAdministrativePermission(permission))
                            stripped.Add(permission);
                }

                if (stripped.Count == 0)
                    return null;
            }
        }

        var grant = new AccessGrant(
            new GrantId($"federated:{role.Id.Value}:{index}"),
            partition,
            subject,
            role.Id,
            grantScope,
            GrantEffect.Allow,
            condition: null,
            validFrom: null,
            validUntil: null,
            justification: null,
            kind: GrantKind.Standard);

        return new ResolvedGrant(grant, role, stripped, IsRoleStripped: stripped is not null);
    }

    public static FederatedRoleMappingSummary ToSummary(StoredRoleFederationMapping stored)
    {
        var (scopeKind, workspaceId, teamId, inboxId) = stored.Mapping.Scope switch
        {
            TenantGrantScope => ("tenant", (string?)null, (string?)null, (string?)null),
            WorkspaceGrantScope ws => ("workspace", ws.Workspace.Value, null, null),
            TeamGrantScope team => ("team", team.Workspace.Value, team.Team.Value, null),
            InboxGrantScope inbox => ("inbox", inbox.Workspace.Value, null, inbox.Inbox.Value),
        };

        return new FederatedRoleMappingSummary(
            stored.Id.Value,
            stored.Mapping.Provider,
            stored.Mapping.ExternalRole,
            scopeKind,
            workspaceId,
            teamId,
            inboxId,
            stored.IsEnabled);
    }
}
