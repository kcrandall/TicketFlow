namespace TicketFlow.Access;

/// <summary>Controls how external identity provider roles participate in authorization.</summary>
public enum RoleFederationMode
{
    /// <summary>External roles are ignored; only local grants apply. Default.</summary>
    Disabled = 0,

    /// <summary>Federated roles are merged with local allow grants; local explicit denies and field policies always apply.</summary>
    MergeWithLocalGrants = 1,

    /// <summary>Local allow grants are ignored; only federated roles allow access, while local explicit denies and field policies always apply.</summary>
    FederatedOnly = 2,
}

/// <summary>Role federation configuration options.</summary>
public sealed class RoleFederationOptions
{
    public RoleFederationMode Mode { get; set; } = RoleFederationMode.Disabled;

    /// <summary>
    /// When <see langword="false"/> (default), administrative permissions (<c>access:manage</c>, <c>access:manage-tenant</c>,
    /// <c>access:break-glass</c>) are stripped from federated roles so IdP claims cannot escalate to access administration.
    /// </summary>
    public bool AllowAdministrativeRoles { get; set; }

    /// <summary>When <see langword="true"/>, external role tokens matching a valid TicketFlow role id map directly to that role.</summary>
    public bool AllowDirectRoleIdMatching { get; set; }

    /// <summary>When <see langword="true"/>, a user's team memberships are also evaluated as candidate external role tokens.</summary>
    public bool MapTeamsAsExternalRoles { get; set; }

    /// <summary>Optional allowlist of TicketFlow role identifiers permitted to be granted via federation. Empty allows all roles.</summary>
    public ISet<RoleId> AllowedRoles { get; } = new HashSet<RoleId>();

    /// <summary>Static case-insensitive mappings from external role token to TicketFlow role identifier.</summary>
    public IDictionary<string, RoleId> StaticMappings { get; } =
        new Dictionary<string, RoleId>(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
            throw new InvalidOperationException($"Unsupported role federation mode '{Mode}'.");

        foreach (var roleId in AllowedRoles)
            if (!roleId.IsInitialized)
                throw new InvalidOperationException("RoleFederation.AllowedRoles cannot contain an uninitialized role id.");

        foreach (var (externalRole, roleId) in StaticMappings)
        {
            if (!AccessText.IsValidText(externalRole, RoleFederationMapping.MaximumExternalRoleLength))
                throw new InvalidOperationException("RoleFederation.StaticMappings contains an invalid external role key.");

            if (!roleId.IsInitialized)
                throw new InvalidOperationException($"RoleFederation.StaticMappings entry '{externalRole}' has an uninitialized role id.");
        }
    }
}
