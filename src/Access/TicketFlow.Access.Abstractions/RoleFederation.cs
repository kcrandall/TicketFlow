using System.Security.Claims;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Validated opaque identifier for a dynamic role federation mapping.</summary>
public readonly record struct RoleFederationMappingId : IComparable<RoleFederationMappingId>
{
    public const int MaximumLength = 128;

    private readonly string? value;

    public RoleFederationMappingId(string value)
    {
        var validated = TicketScope.RequireIdentifier(value, nameof(value));
        if (validated.Length > MaximumLength)
            throw new ArgumentException($"Mapping identifiers must contain at most {MaximumLength} characters.", nameof(value));
        this.value = validated;
    }

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The RoleFederationMappingId is uninitialized.");

    public bool IsInitialized => value is not null;

    public static RoleFederationMappingId NewId() => new(Guid.NewGuid().ToString("N"));

    public int CompareTo(RoleFederationMappingId other) => string.CompareOrdinal(value, other.value);

    public static bool operator <(RoleFederationMappingId left, RoleFederationMappingId right) => left.CompareTo(right) < 0;
    public static bool operator <=(RoleFederationMappingId left, RoleFederationMappingId right) => left.CompareTo(right) <= 0;
    public static bool operator >(RoleFederationMappingId left, RoleFederationMappingId right) => left.CompareTo(right) > 0;
    public static bool operator >=(RoleFederationMappingId left, RoleFederationMappingId right) => left.CompareTo(right) >= 0;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Maps an external identity provider role name to a TicketFlow role within a partition and grant scope.</summary>
public sealed record RoleFederationMapping
{
    public const int MaximumProviderLength = 128;
    public const int MaximumExternalRoleLength = 256;
    public const int MaximumDescriptionLength = 1024;

    public RoleFederationMapping(
        string provider,
        string externalRole,
        RoleId roleId,
        AccessPartition partition,
        GrantScope scope,
        string? description = null)
    {
        if (!roleId.IsInitialized)
            throw new ArgumentException("A role identifier is required.", nameof(roleId));
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(scope);

        Provider = AccessText.RequireText(provider, MaximumProviderLength, nameof(provider));
        ExternalRole = AccessText.RequireText(externalRole, MaximumExternalRoleLength, nameof(externalRole));
        RoleId = roleId;
        Partition = partition;
        Scope = scope;
        Description = string.IsNullOrWhiteSpace(description)
            ? null
            : AccessText.RequireText(description, MaximumDescriptionLength, nameof(description));
    }

    public string Provider { get; }
    public string ExternalRole { get; }
    public RoleId RoleId { get; }
    public AccessPartition Partition { get; }
    public GrantScope Scope { get; }
    public string? Description { get; }

    /// <summary>The workspace named by <see cref="Scope"/>, or <see langword="null"/> for tenant-wide mappings.</summary>
    public WorkspaceId? Workspace => Scope switch
    {
        TenantGrantScope => null,
        WorkspaceGrantScope workspace => workspace.Workspace,
        TeamGrantScope team => team.Workspace,
        InboxGrantScope inbox => inbox.Workspace,
    };

    public override string ToString() => "RoleFederationMapping { <redacted> }";
}

/// <summary>A stored role federation mapping with version and audit metadata.</summary>
public sealed record StoredRoleFederationMapping
{
    public StoredRoleFederationMapping(
        RoleFederationMappingId id,
        RoleFederationMapping mapping,
        bool isEnabled,
        long version,
        DateTimeOffset createdAtUtc,
        string createdByPrincipalId,
        DateTimeOffset updatedAtUtc,
        string updatedByPrincipalId)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A mapping identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        Id = id;
        Mapping = mapping;
        IsEnabled = isEnabled;
        Version = version;
        CreatedAtUtc = Normalize(createdAtUtc);
        CreatedByPrincipalId = TicketScope.RequireIdentifier(createdByPrincipalId, nameof(createdByPrincipalId));
        UpdatedAtUtc = Normalize(updatedAtUtc);
        UpdatedByPrincipalId = TicketScope.RequireIdentifier(updatedByPrincipalId, nameof(updatedByPrincipalId));
    }

    public RoleFederationMappingId Id { get; }
    public RoleFederationMapping Mapping { get; }
    public bool IsEnabled { get; }
    public long Version { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string CreatedByPrincipalId { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string UpdatedByPrincipalId { get; }

    public override string ToString() => "StoredRoleFederationMapping { <redacted> }";

    private static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}

/// <summary>Serializable summary of one dynamic federation mapping in a <see cref="FederatedRoleCatalog"/>.</summary>
public sealed record FederatedRoleMappingSummary(
    string MappingId,
    string Provider,
    string ExternalRole,
    string ScopeKind,
    string? WorkspaceId,
    string? TeamId,
    string? InboxId,
    bool IsEnabled);

/// <summary>Exportable descriptor of a built-in or custom TicketFlow role and its active federation mappings.</summary>
public sealed record FederatedRoleDescriptor
{
    public FederatedRoleDescriptor(
        RoleId roleId,
        string displayName,
        bool isBuiltIn,
        bool includesAllPermissions,
        IReadOnlyList<string> permissions,
        IReadOnlyList<FederatedRoleMappingSummary> mappings)
    {
        if (!roleId.IsInitialized)
            throw new ArgumentException("A role identifier is required.", nameof(roleId));
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(mappings);

        RoleId = roleId;
        DisplayName = AccessText.RequireText(displayName, AccessRole.MaximumNameLength, nameof(displayName));
        IsBuiltIn = isBuiltIn;
        IncludesAllPermissions = includesAllPermissions;
        Permissions = permissions.Count == 0 ? [] : permissions.ToArray().AsReadOnly();
        Mappings = mappings.Count == 0 ? [] : mappings.ToArray().AsReadOnly();
    }

    public RoleId RoleId { get; }
    public string DisplayName { get; }
    public bool IsBuiltIn { get; }
    public bool IncludesAllPermissions { get; }
    public IReadOnlyList<string> Permissions { get; }
    public IReadOnlyList<FederatedRoleMappingSummary> Mappings { get; }
}

/// <summary>Exportable role catalog for external identity providers and discovery endpoints.</summary>
public sealed record FederatedRoleCatalog
{
    public FederatedRoleCatalog(
        string tenantId,
        string projectId,
        string environmentId,
        string appId,
        WorkspaceId workspaceId,
        DateTimeOffset generatedAtUtc,
        IReadOnlyList<FederatedRoleDescriptor> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (workspaceId == default)
            throw new ArgumentException("A workspace identifier is required.", nameof(workspaceId));

        TenantId = TicketScope.RequireIdentifier(tenantId, nameof(tenantId));
        ProjectId = TicketScope.RequireIdentifier(projectId, nameof(projectId));
        EnvironmentId = TicketScope.RequireIdentifier(environmentId, nameof(environmentId));
        AppId = TicketScope.RequireIdentifier(appId, nameof(appId));
        WorkspaceId = workspaceId;
        GeneratedAtUtc = new DateTimeOffset(generatedAtUtc.UtcTicks - generatedAtUtc.UtcTicks % 10, TimeSpan.Zero);
        Roles = roles.Count == 0 ? [] : roles.ToArray().AsReadOnly();
    }

    public string TenantId { get; }
    public string ProjectId { get; }
    public string EnvironmentId { get; }
    public string AppId { get; }
    public WorkspaceId WorkspaceId { get; }
    public DateTimeOffset GeneratedAtUtc { get; }
    public IReadOnlyList<FederatedRoleDescriptor> Roles { get; }
}

/// <summary>Partition-scoped store for dynamic external-role-to-TicketFlow-role federation mappings.</summary>
public interface IRoleFederationMappingStore
{
    ValueTask<StoredRoleFederationMapping?> FindAsync(AccessPartition partition, RoleFederationMappingId mappingId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredRoleFederationMapping>> ListForPartitionAsync(AccessPartition partition, bool includeDisabled = true, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<StoredRoleFederationMapping>> UpsertAsync(RoleFederationMappingId mappingId, RoleFederationMapping mapping, bool isEnabled, string actorPrincipalId, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<bool>> DeleteAsync(AccessPartition partition, RoleFederationMappingId mappingId, long? expectedVersion = null, CancellationToken cancellationToken = default);
}

/// <summary>Authorized, audited management of dynamic role federation mappings and role catalog export.</summary>
public interface IRoleFederationService
{
    ValueTask<TicketResult<IReadOnlyList<StoredRoleFederationMapping>>> ListMappingsAsync(PrincipalRef actor, bool includeDisabled = true, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<StoredRoleFederationMapping>>> UpsertMappingAsync(PrincipalRef actor, RoleFederationMappingId mappingId, RoleFederationMapping mapping, bool isEnabled = true, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<RoleFederationMappingId>>> DeleteMappingAsync(PrincipalRef actor, RoleFederationMappingId mappingId, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<FederatedRoleCatalog>> GetRoleCatalogAsync(PrincipalRef actor, CancellationToken cancellationToken = default);
}

/// <summary>Resolves a TicketFlow <see cref="PrincipalRef"/> from an authenticated <see cref="ClaimsPrincipal"/> within a <see cref="TicketScope"/>.</summary>
public interface IAccessPrincipalResolver
{
    ValueTask<PrincipalRef?> ResolveAsync(ClaimsPrincipal claimsPrincipal, TicketScope scope, CancellationToken cancellationToken = default);
}
