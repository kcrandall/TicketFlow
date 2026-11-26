using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Validated opaque API key identifier, unique within an <see cref="AccessPartition"/>.</summary>
public readonly record struct ApiKeyId : IComparable<ApiKeyId>
{
    public const int MaximumLength = 128;

    private readonly string? value;

    public ApiKeyId(string value)
    {
        var validated = TicketScope.RequireIdentifier(value, nameof(value));
        if (validated.Length > MaximumLength)
            throw new ArgumentException($"API key identifiers must contain at most {MaximumLength} characters.", nameof(value));
        this.value = validated;
    }

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The ApiKeyId is uninitialized.");

    public bool IsInitialized => value is not null;

    public static ApiKeyId NewId() => new(Guid.NewGuid().ToString("N"));

    public int CompareTo(ApiKeyId other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;
}

/// <summary>A managed service account scoped to a partition and grant scope.</summary>
public sealed record ServiceAccount
{
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumDescriptionLength = 1024;

    public ServiceAccount(
        ApiClientId clientId,
        AccessPartition partition,
        GrantScope scope,
        string displayName,
        string? description = null)
    {
        if (!clientId.IsInitialized)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(scope);

        ClientId = clientId;
        Partition = partition;
        Scope = scope;
        DisplayName = AccessText.RequireText(displayName, MaximumDisplayNameLength, nameof(displayName));
        Description = string.IsNullOrWhiteSpace(description)
            ? null
            : AccessText.RequireText(description, MaximumDescriptionLength, nameof(description));
    }

    public ApiClientId ClientId { get; }
    public AccessPartition Partition { get; }
    public GrantScope Scope { get; }
    public string DisplayName { get; }
    public string? Description { get; }

    /// <summary>The workspace named by <see cref="Scope"/>, or <see langword="null"/> for tenant-wide service accounts.</summary>
    public WorkspaceId? Workspace => Scope switch
    {
        TenantGrantScope => null,
        WorkspaceGrantScope workspace => workspace.Workspace,
        TeamGrantScope team => team.Workspace,
        InboxGrantScope inbox => inbox.Workspace,
    };

    /// <summary>True when this service account is permitted to operate within <paramref name="scope"/>.</summary>
    public bool AppliesTo(TicketScope scope) =>
        scope is not null && Partition.Contains(scope) && (Workspace is not { } workspace || workspace == scope.WorkspaceId);

    /// <summary>True when this service account is permitted to access <paramref name="resource"/>.</summary>
    public bool AppliesTo(AccessResource resource) =>
        resource is not null && Partition.Contains(resource.Scope) && Scope switch
        {
            TenantGrantScope => true,
            WorkspaceGrantScope workspace => workspace.Workspace == resource.Scope.WorkspaceId,
            TeamGrantScope team => team.Workspace == resource.Scope.WorkspaceId && resource.TeamId == team.Team,
            InboxGrantScope inbox => inbox.Workspace == resource.Scope.WorkspaceId && resource.InboxId == inbox.Inbox,
        };

    public override string ToString() => "ServiceAccount { <redacted> }";
}

/// <summary>A stored service account with lifecycle state, version and audit metadata.</summary>
public sealed record StoredServiceAccount
{
    public StoredServiceAccount(
        ServiceAccount account,
        bool isEnabled,
        long version,
        DateTimeOffset createdAtUtc,
        string createdByPrincipalId,
        DateTimeOffset updatedAtUtc,
        string updatedByPrincipalId,
        DateTimeOffset? disabledAtUtc = null,
        string? disabledByPrincipalId = null,
        string? disabledReason = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        Account = account;
        IsEnabled = isEnabled;
        Version = version;
        CreatedAtUtc = Normalize(createdAtUtc);
        CreatedByPrincipalId = TicketScope.RequireIdentifier(createdByPrincipalId, nameof(createdByPrincipalId));
        UpdatedAtUtc = Normalize(updatedAtUtc);
        UpdatedByPrincipalId = TicketScope.RequireIdentifier(updatedByPrincipalId, nameof(updatedByPrincipalId));
        DisabledAtUtc = Normalize(disabledAtUtc);
        DisabledByPrincipalId = string.IsNullOrWhiteSpace(disabledByPrincipalId)
            ? null
            : TicketScope.RequireIdentifier(disabledByPrincipalId, nameof(disabledByPrincipalId));
        DisabledReason = string.IsNullOrWhiteSpace(disabledReason)
            ? null
            : AccessText.RequireText(disabledReason, AccessGrant.MaximumJustificationLength, nameof(disabledReason));
    }

    public ServiceAccount Account { get; }
    public bool IsEnabled { get; }
    public long Version { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string CreatedByPrincipalId { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string UpdatedByPrincipalId { get; }
    public DateTimeOffset? DisabledAtUtc { get; }
    public string? DisabledByPrincipalId { get; }
    public string? DisabledReason { get; }

    public override string ToString() => "StoredServiceAccount { <redacted> }";

    private static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static DateTimeOffset? Normalize(DateTimeOffset? value) =>
        value is { } instant ? Normalize(instant) : null;
}

/// <summary>Metadata-only representation of a stored API key. Never carries plaintext key material or hashes.</summary>
public sealed record StoredApiKey
{
    public const int MaximumNameLength = 256;
    public const int MaximumKeyPrefixLength = 64;

    public StoredApiKey(
        ApiKeyId id,
        AccessPartition partition,
        ApiClientId clientId,
        string name,
        string keyPrefix,
        long version,
        DateTimeOffset createdAtUtc,
        string createdByPrincipalId,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? lastUsedAtUtc = null,
        DateTimeOffset? revokedAtUtc = null,
        string? revokedByPrincipalId = null,
        string? revocationReason = null)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("An API key identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(partition);
        if (!clientId.IsInitialized)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        var validatedPrefix = TicketScope.RequireIdentifier(keyPrefix, nameof(keyPrefix));
        if (validatedPrefix.Length > MaximumKeyPrefixLength)
            throw new ArgumentException($"Key prefix must contain at most {MaximumKeyPrefixLength} characters.", nameof(keyPrefix));

        Id = id;
        Partition = partition;
        ClientId = clientId;
        Name = AccessText.RequireText(name, MaximumNameLength, nameof(name));
        KeyPrefix = validatedPrefix;
        Version = version;
        CreatedAtUtc = Normalize(createdAtUtc);
        CreatedByPrincipalId = TicketScope.RequireIdentifier(createdByPrincipalId, nameof(createdByPrincipalId));
        ExpiresAtUtc = Normalize(expiresAtUtc);
        LastUsedAtUtc = Normalize(lastUsedAtUtc);
        RevokedAtUtc = Normalize(revokedAtUtc);
        RevokedByPrincipalId = string.IsNullOrWhiteSpace(revokedByPrincipalId)
            ? null
            : TicketScope.RequireIdentifier(revokedByPrincipalId, nameof(revokedByPrincipalId));
        RevocationReason = string.IsNullOrWhiteSpace(revocationReason)
            ? null
            : AccessText.RequireText(revocationReason, AccessGrant.MaximumJustificationLength, nameof(revocationReason));
    }

    public ApiKeyId Id { get; }
    public AccessPartition Partition { get; }
    public ApiClientId ClientId { get; }
    public string Name { get; }
    public string KeyPrefix { get; }
    public long Version { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string CreatedByPrincipalId { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
    public DateTimeOffset? LastUsedAtUtc { get; }
    public DateTimeOffset? RevokedAtUtc { get; }
    public string? RevokedByPrincipalId { get; }
    public string? RevocationReason { get; }

    public bool IsRevoked => RevokedAtUtc.HasValue;

    public bool IsExpiredAt(DateTimeOffset utcNow) => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= utcNow;

    public bool IsActiveAt(DateTimeOffset utcNow) => !IsRevoked && !IsExpiredAt(utcNow);

    public override string ToString() => "StoredApiKey { <redacted> }";

    private static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    private static DateTimeOffset? Normalize(DateTimeOffset? value) =>
        value is { } instant ? Normalize(instant) : null;
}

/// <summary>Internal store record pairing <see cref="StoredApiKey"/> metadata with its SHA-256 hex digest.</summary>
public sealed record StoredApiKeySecret
{
    public const int MaximumHashLength = 128;

    public StoredApiKeySecret(StoredApiKey metadata, string keyHashHex)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var validated = TicketScope.RequireIdentifier(keyHashHex, nameof(keyHashHex));
        if (validated.Length > MaximumHashLength)
            throw new ArgumentException($"Key hash must contain at most {MaximumHashLength} characters.", nameof(keyHashHex));

        Metadata = metadata;
        KeyHashHex = validated;
    }

    public StoredApiKey Metadata { get; }
    public string KeyHashHex { get; }

    public override string ToString() => "StoredApiKeySecret { <redacted> }";
}

/// <summary>Returned once when an API key is issued. The plaintext key is never stored or returned again.</summary>
public sealed record IssuedApiKey
{
    public const int MaximumPlaintextLength = 512;

    public IssuedApiKey(StoredApiKey key, string plaintextKey)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        PlaintextKey = AccessText.RequireText(plaintextKey, MaximumPlaintextLength, nameof(plaintextKey));
    }

    public StoredApiKey Key { get; }
    public string PlaintextKey { get; }

    public override string ToString() => "IssuedApiKey { <redacted> }";
}

/// <summary>Request to issue a new API key for a service account.</summary>
public sealed record ApiKeyCreateRequest
{
    public ApiKeyCreateRequest(ApiClientId clientId, string name, DateTimeOffset? expiresAtUtc = null)
    {
        if (!clientId.IsInitialized)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));
        ClientId = clientId;
        Name = AccessText.RequireText(name, StoredApiKey.MaximumNameLength, nameof(name));
        ExpiresAtUtc = expiresAtUtc;
    }

    public ApiClientId ClientId { get; }
    public string Name { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
}

/// <summary>Outcome of authenticating a plaintext service-account API key.</summary>
public sealed record ApiKeyAuthenticationResult
{
    private ApiKeyAuthenticationResult(
        bool isAuthenticated,
        ApiClientPrincipal? principal,
        StoredServiceAccount? serviceAccount,
        StoredApiKey? apiKey,
        string? failureReason)
    {
        IsAuthenticated = isAuthenticated;
        Principal = principal;
        ServiceAccount = serviceAccount;
        ApiKey = apiKey;
        FailureReason = failureReason;
    }

    public bool IsAuthenticated { get; }
    public ApiClientPrincipal? Principal { get; }
    public StoredServiceAccount? ServiceAccount { get; }
    public StoredApiKey? ApiKey { get; }
    public string? FailureReason { get; }

    public static ApiKeyAuthenticationResult Authenticated(
        ApiClientPrincipal principal,
        StoredServiceAccount serviceAccount,
        StoredApiKey apiKey)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(serviceAccount);
        ArgumentNullException.ThrowIfNull(apiKey);
        return new(true, principal, serviceAccount, apiKey, null);
    }

    public static ApiKeyAuthenticationResult Failed(string failureReason) =>
        new(false, null, null, null, AccessText.RequireText(failureReason, 512, nameof(failureReason)));

    public override string ToString() =>
        IsAuthenticated ? "ApiKeyAuthenticationResult { Authenticated }" : "ApiKeyAuthenticationResult { Failed }";
}

/// <summary>Partition-scoped store for managed service accounts.</summary>
public interface IServiceAccountStore
{
    ValueTask<StoredServiceAccount?> FindAsync(AccessPartition partition, ApiClientId clientId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredServiceAccount>> ListForPartitionAsync(AccessPartition partition, bool includeDisabled = true, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<StoredServiceAccount>> CreateAsync(ServiceAccount account, string actorPrincipalId, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<StoredServiceAccount>> UpdateAsync(ServiceAccount account, string actorPrincipalId, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<StoredServiceAccount>> SetEnabledAsync(AccessPartition partition, ApiClientId clientId, bool isEnabled, string actorPrincipalId, string? reason = null, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<bool>> DeleteAsync(AccessPartition partition, ApiClientId clientId, long? expectedVersion = null, CancellationToken cancellationToken = default);
}

/// <summary>Partition-scoped store for hashed service-account API keys.</summary>
public interface IApiKeyStore
{
    ValueTask<StoredApiKey?> FindByIdAsync(AccessPartition partition, ApiKeyId keyId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredApiKeySecret>> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredApiKey>> ListForClientAsync(AccessPartition partition, ApiClientId clientId, bool includeInactive = false, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredApiKey>> ListForPartitionAsync(AccessPartition partition, bool includeInactive = false, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<StoredApiKey>> CreateAsync(ApiKeyId keyId, AccessPartition partition, ApiClientId clientId, string name, string keyPrefix, string keyHashHex, string actorPrincipalId, DateTimeOffset? expiresAtUtc = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<StoredApiKey>> RevokeAsync(AccessPartition partition, ApiKeyId keyId, string actorPrincipalId, string? reason = null, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<int> RevokeAllForClientAsync(AccessPartition partition, ApiClientId clientId, string actorPrincipalId, string? reason = null, CancellationToken cancellationToken = default);

    ValueTask RecordUsageAsync(AccessPartition partition, ApiKeyId keyId, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default);
}

/// <summary>Authorized, audited lifecycle management and authentication for service accounts and API keys.</summary>
public interface IServiceAccountService
{
    ValueTask<TicketResult<IReadOnlyList<StoredServiceAccount>>> ListServiceAccountsAsync(PrincipalRef actor, bool includeDisabled = true, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<StoredServiceAccount>> GetServiceAccountAsync(PrincipalRef actor, ApiClientId clientId, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<StoredServiceAccount>>> CreateServiceAccountAsync(PrincipalRef actor, ServiceAccount account, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<StoredServiceAccount>>> UpdateServiceAccountAsync(PrincipalRef actor, ApiClientId clientId, string displayName, string? description = null, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<StoredServiceAccount>>> SetServiceAccountEnabledAsync(PrincipalRef actor, ApiClientId clientId, bool isEnabled, string? reason = null, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<ApiClientId>>> DeleteServiceAccountAsync(PrincipalRef actor, ApiClientId clientId, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<IReadOnlyList<StoredApiKey>>> ListApiKeysAsync(PrincipalRef actor, ApiClientId clientId, bool includeInactive = false, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<IssuedApiKey>>> IssueApiKeyAsync(PrincipalRef actor, ApiKeyCreateRequest request, CancellationToken cancellationToken = default);

    ValueTask<TicketResult<AccessChangeApplied<StoredApiKey>>> RevokeApiKeyAsync(PrincipalRef actor, ApiKeyId keyId, string? reason = null, long? expectedVersion = null, CancellationToken cancellationToken = default);

    ValueTask<ApiKeyAuthenticationResult> AuthenticateApiKeyAsync(string plaintextKey, TicketScope targetScope, CancellationToken cancellationToken = default);
}
