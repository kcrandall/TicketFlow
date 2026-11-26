using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Default <see cref="IServiceAccountService"/>. Enforces standard-grant-only administration (no break-glass),
/// scope containment, cascading API key and grant revocation on delete, fixed-time SHA-256 verification, and
/// zero-secret audit recording.
/// </summary>
public sealed class ServiceAccountService : IServiceAccountService
{
    public const string KeyPrefixMarker = "tf_sa_";

    private readonly IServiceAccountStore serviceAccountStore;
    private readonly IApiKeyStore apiKeyStore;
    private readonly IAccessGrantStore grantStore;
    private readonly IAccessAuditSink auditSink;
    private readonly AccessEngine engine;

    public ServiceAccountService(
        IServiceAccountStore serviceAccountStore,
        IApiKeyStore apiKeyStore,
        IAccessGrantStore grantStore,
        IAccessRoleStore roleStore,
        IPermissionCatalog catalog,
        IAccessAuditSink auditSink,
        TimeProvider? timeProvider = null,
        IOptions<AccessControlOptions>? options = null,
        IRoleFederationMappingStore? roleFederationMappings = null)
    {
        ArgumentNullException.ThrowIfNull(serviceAccountStore);
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        ArgumentNullException.ThrowIfNull(grantStore);
        ArgumentNullException.ThrowIfNull(roleStore);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(auditSink);

        var resolvedOptions = options?.Value;
        resolvedOptions?.Validate();

        this.serviceAccountStore = serviceAccountStore;
        this.apiKeyStore = apiKeyStore;
        this.grantStore = grantStore;
        this.auditSink = auditSink;
        engine = new(catalog, grantStore, roleStore, timeProvider ?? TimeProvider.System, resolvedOptions, serviceAccountStore, roleFederationMappings);
    }

    public async ValueTask<TicketResult<IReadOnlyList<StoredServiceAccount>>> ListServiceAccountsAsync(
        PrincipalRef actor,
        bool includeDisabled = true,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var (authFailure, _) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (authFailure is not null)
            return authFailure;

        var partition = AccessPartition.From(actor.Scope);
        var accounts = await serviceAccountStore.ListForPartitionAsync(partition, includeDisabled, cancellationToken).ConfigureAwait(false);
        var visible = new List<StoredServiceAccount>(accounts.Count);
        for (var i = 0; i < accounts.Count; i++)
            if (IsVisibleInWorkspace(accounts[i].Account, actor.Scope))
                visible.Add(accounts[i]);

        return TicketResult.Ok<IReadOnlyList<StoredServiceAccount>>(visible.AsReadOnly());
    }

    public async ValueTask<TicketResult<StoredServiceAccount>> GetServiceAccountAsync(
        PrincipalRef actor,
        ApiClientId clientId,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!clientId.IsInitialized)
            return TicketFailure.Invalid("An API client identifier is required.");

        var (authFailure, _) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (authFailure is not null)
            return authFailure;

        var partition = AccessPartition.From(actor.Scope);
        var existing = await serviceAccountStore.FindAsync(partition, clientId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return TicketFailure.NotFound;

        if (!IsVisibleInWorkspace(existing.Account, actor.Scope))
            return new TicketFailure(TicketFailureKind.Forbidden, ["The service account is outside the actor's scope."], Code: "out-of-scope");

        return TicketResult.Ok(existing);
    }

    public async ValueTask<TicketResult<AccessChangeApplied<StoredServiceAccount>>> CreateServiceAccountAsync(
        PrincipalRef actor,
        ServiceAccount account,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(account);

        var requiredPermission = GetRequiredAdminPermission(account.Scope);
        var targetResource = TargetForAudit(actor.Scope, account.Scope, account.ClientId.Value);

        var (authFailure, _) = await AuthorizeScopeManagementAsync(actor, account.Partition, account.Scope, requiredPermission, context: null, cancellationToken).ConfigureAwait(false);
        if (authFailure is not null)
            return await RefuseAsync<StoredServiceAccount>(actor, AccessAuditKind.ServiceAccountCreated, requiredPermission, targetResource, authFailure, cancellationToken: cancellationToken).ConfigureAwait(false);

        var createResult = await serviceAccountStore.CreateAsync(account, FormatActorId(actor), cancellationToken).ConfigureAwait(false);
        if (createResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<StoredServiceAccount>(actor, AccessAuditKind.ServiceAccountCreated, requiredPermission, targetResource, failure, cancellationToken: cancellationToken).ConfigureAwait(false);

        createResult.TryGetValue(out TicketValue<StoredServiceAccount> ok);
        var auditEvent = await RecordAuditAsync(actor, AccessAuditKind.ServiceAccountCreated, AccessAuditOutcome.Allowed, requiredPermission, targetResource, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<StoredServiceAccount>(ok.Value, auditEvent.Id));
    }

    public async ValueTask<TicketResult<AccessChangeApplied<StoredServiceAccount>>> UpdateServiceAccountAsync(
        PrincipalRef actor,
        ApiClientId clientId,
        string displayName,
        string? description = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!clientId.IsInitialized)
            return TicketFailure.Invalid("An API client identifier is required.");
        if (!AccessText.IsValidText(displayName, ServiceAccount.MaximumDisplayNameLength))
            return TicketFailure.Invalid("Display name must be 1-256 non-control characters.");
        if (!string.IsNullOrWhiteSpace(description) && !AccessText.IsValidText(description, ServiceAccount.MaximumDescriptionLength))
            return TicketFailure.Invalid("Description must be at most 1024 non-control characters.");

        var fallbackResource = new AccessResource(actor.Scope, resourceId: clientId.Value);
        var (baseFailure, context) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (baseFailure is not null)
            return await RefuseAsync<StoredServiceAccount>(actor, AccessAuditKind.ServiceAccountUpdated, AccessPermissions.AccessManage, fallbackResource, baseFailure, cancellationToken: cancellationToken).ConfigureAwait(false);

        var partition = AccessPartition.From(actor.Scope);
        var existing = await serviceAccountStore.FindAsync(partition, clientId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return await RefuseAsync<StoredServiceAccount>(actor, AccessAuditKind.ServiceAccountUpdated, AccessPermissions.AccessManage, fallbackResource, TicketFailure.NotFound, cancellationToken: cancellationToken).ConfigureAwait(false);

        var requiredPermission = GetRequiredAdminPermission(existing.Account.Scope);
        var targetResource = TargetForAudit(actor.Scope, existing.Account.Scope, clientId.Value);
        var (scopeFailure, _) = await AuthorizeScopeManagementAsync(actor, existing.Account.Partition, existing.Account.Scope, requiredPermission, context, cancellationToken).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await RefuseAsync<StoredServiceAccount>(actor, AccessAuditKind.ServiceAccountUpdated, requiredPermission, targetResource, scopeFailure, cancellationToken: cancellationToken).ConfigureAwait(false);

        var updatedAccount = new ServiceAccount(existing.Account.ClientId, existing.Account.Partition, existing.Account.Scope, displayName, description);
        var updateResult = await serviceAccountStore.UpdateAsync(updatedAccount, FormatActorId(actor), expectedVersion ?? existing.Version, cancellationToken).ConfigureAwait(false);
        if (updateResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<StoredServiceAccount>(actor, AccessAuditKind.ServiceAccountUpdated, requiredPermission, targetResource, failure, cancellationToken: cancellationToken).ConfigureAwait(false);

        updateResult.TryGetValue(out TicketValue<StoredServiceAccount> ok);
        var auditEvent = await RecordAuditAsync(actor, AccessAuditKind.ServiceAccountUpdated, AccessAuditOutcome.Allowed, requiredPermission, targetResource, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<StoredServiceAccount>(ok.Value, auditEvent.Id));
    }

    public async ValueTask<TicketResult<AccessChangeApplied<StoredServiceAccount>>> SetServiceAccountEnabledAsync(
        PrincipalRef actor,
        ApiClientId clientId,
        bool isEnabled,
        string? reason = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var auditKind = isEnabled ? AccessAuditKind.ServiceAccountEnabled : AccessAuditKind.ServiceAccountDisabled;
        if (!clientId.IsInitialized)
            return TicketFailure.Invalid("An API client identifier is required.");
        if (!string.IsNullOrWhiteSpace(reason) && !AccessText.IsValidText(reason, AccessGrant.MaximumJustificationLength))
            return TicketFailure.Invalid("Reason must be at most 1024 non-control characters.");

        var safeReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        var fallbackResource = new AccessResource(actor.Scope, resourceId: clientId.Value);
        var (baseFailure, context) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (baseFailure is not null)
            return await RefuseAsync<StoredServiceAccount>(actor, auditKind, AccessPermissions.AccessManage, fallbackResource, baseFailure, safeReason, cancellationToken).ConfigureAwait(false);

        var partition = AccessPartition.From(actor.Scope);
        var existing = await serviceAccountStore.FindAsync(partition, clientId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return await RefuseAsync<StoredServiceAccount>(actor, auditKind, AccessPermissions.AccessManage, fallbackResource, TicketFailure.NotFound, safeReason, cancellationToken).ConfigureAwait(false);

        var requiredPermission = GetRequiredAdminPermission(existing.Account.Scope);
        var targetResource = TargetForAudit(actor.Scope, existing.Account.Scope, clientId.Value);
        var (scopeFailure, _) = await AuthorizeScopeManagementAsync(actor, existing.Account.Partition, existing.Account.Scope, requiredPermission, context, cancellationToken).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await RefuseAsync<StoredServiceAccount>(actor, auditKind, requiredPermission, targetResource, scopeFailure, safeReason, cancellationToken).ConfigureAwait(false);

        var setResult = await serviceAccountStore.SetEnabledAsync(partition, clientId, isEnabled, FormatActorId(actor), safeReason, expectedVersion ?? existing.Version, cancellationToken).ConfigureAwait(false);
        if (setResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<StoredServiceAccount>(actor, auditKind, requiredPermission, targetResource, failure, safeReason, cancellationToken).ConfigureAwait(false);

        setResult.TryGetValue(out TicketValue<StoredServiceAccount> ok);
        var auditEvent = await RecordAuditAsync(actor, auditKind, AccessAuditOutcome.Allowed, requiredPermission, targetResource, justification: safeReason, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<StoredServiceAccount>(ok.Value, auditEvent.Id));
    }

    public async ValueTask<TicketResult<AccessChangeApplied<ApiClientId>>> DeleteServiceAccountAsync(
        PrincipalRef actor,
        ApiClientId clientId,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!clientId.IsInitialized)
            return TicketFailure.Invalid("An API client identifier is required.");

        var fallbackResource = new AccessResource(actor.Scope, resourceId: clientId.Value);
        var (baseFailure, context) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (baseFailure is not null)
            return await RefuseAsync<ApiClientId>(actor, AccessAuditKind.ServiceAccountDeleted, AccessPermissions.AccessManage, fallbackResource, baseFailure, cancellationToken: cancellationToken).ConfigureAwait(false);

        var partition = AccessPartition.From(actor.Scope);
        var existing = await serviceAccountStore.FindAsync(partition, clientId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return await RefuseAsync<ApiClientId>(actor, AccessAuditKind.ServiceAccountDeleted, AccessPermissions.AccessManage, fallbackResource, TicketFailure.NotFound, cancellationToken: cancellationToken).ConfigureAwait(false);

        var requiredPermission = GetRequiredAdminPermission(existing.Account.Scope);
        var targetResource = TargetForAudit(actor.Scope, existing.Account.Scope, clientId.Value);
        var (scopeFailure, _) = await AuthorizeScopeManagementAsync(actor, existing.Account.Partition, existing.Account.Scope, requiredPermission, context, cancellationToken).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await RefuseAsync<ApiClientId>(actor, AccessAuditKind.ServiceAccountDeleted, requiredPermission, targetResource, scopeFailure, cancellationToken: cancellationToken).ConfigureAwait(false);

        var deleteResult = await serviceAccountStore.DeleteAsync(partition, clientId, expectedVersion ?? existing.Version, cancellationToken).ConfigureAwait(false);
        if (deleteResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<ApiClientId>(actor, AccessAuditKind.ServiceAccountDeleted, requiredPermission, targetResource, failure, cancellationToken: cancellationToken).ConfigureAwait(false);

        var actorId = FormatActorId(actor);
        await apiKeyStore.RevokeAllForClientAsync(partition, clientId, actorId, "Service account deleted.", cancellationToken).ConfigureAwait(false);

        var subjectGrants = await grantStore.ListAllForSubjectAsync(partition, clientId, actor.Scope.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (subjectGrants.Count > 0)
        {
            var batch = new (GrantId Id, long ExpectedRevision)[subjectGrants.Count];
            for (var i = 0; i < subjectGrants.Count; i++)
                batch[i] = (subjectGrants[i].Grant.Id, subjectGrants[i].Revision);
            await grantStore.DeleteManyAsync(partition, batch, cancellationToken).ConfigureAwait(false);
        }

        var auditEvent = await RecordAuditAsync(actor, AccessAuditKind.ServiceAccountDeleted, AccessAuditOutcome.Allowed, requiredPermission, targetResource, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<ApiClientId>(clientId, auditEvent.Id));
    }

    public async ValueTask<TicketResult<IReadOnlyList<StoredApiKey>>> ListApiKeysAsync(
        PrincipalRef actor,
        ApiClientId clientId,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!clientId.IsInitialized)
            return TicketFailure.Invalid("An API client identifier is required.");

        var (authFailure, _) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (authFailure is not null)
            return authFailure;

        var partition = AccessPartition.From(actor.Scope);
        var existing = await serviceAccountStore.FindAsync(partition, clientId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return TicketFailure.NotFound;

        if (!IsVisibleInWorkspace(existing.Account, actor.Scope))
            return new TicketFailure(TicketFailureKind.Forbidden, ["The service account is outside the actor's scope."], Code: "out-of-scope");

        var keys = await apiKeyStore.ListForClientAsync(partition, clientId, includeInactive, cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(keys);
    }

    public async ValueTask<TicketResult<AccessChangeApplied<IssuedApiKey>>> IssueApiKeyAsync(
        PrincipalRef actor,
        ApiKeyCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);

        var fallbackResource = new AccessResource(actor.Scope, resourceId: request.ClientId.Value);
        var (baseFailure, context) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (baseFailure is not null)
            return await RefuseAsync<IssuedApiKey>(actor, AccessAuditKind.ApiKeyCreated, AccessPermissions.AccessManage, fallbackResource, baseFailure, cancellationToken: cancellationToken).ConfigureAwait(false);

        var now = engine.Now;
        if (request.ExpiresAtUtc is { } expiresAt && expiresAt <= now)
            return await RefuseAsync<IssuedApiKey>(actor, AccessAuditKind.ApiKeyCreated, AccessPermissions.AccessManage, fallbackResource,
                TicketFailure.Invalid("API key expiration timestamp must be in the future."), cancellationToken: cancellationToken).ConfigureAwait(false);

        var partition = AccessPartition.From(actor.Scope);
        var existing = await serviceAccountStore.FindAsync(partition, request.ClientId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return await RefuseAsync<IssuedApiKey>(actor, AccessAuditKind.ApiKeyCreated, AccessPermissions.AccessManage, fallbackResource, TicketFailure.NotFound, cancellationToken: cancellationToken).ConfigureAwait(false);

        var requiredPermission = GetRequiredAdminPermission(existing.Account.Scope);
        var targetResource = TargetForAudit(actor.Scope, existing.Account.Scope, request.ClientId.Value);
        var (scopeFailure, _) = await AuthorizeScopeManagementAsync(actor, existing.Account.Partition, existing.Account.Scope, requiredPermission, context, cancellationToken).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await RefuseAsync<IssuedApiKey>(actor, AccessAuditKind.ApiKeyCreated, requiredPermission, targetResource, scopeFailure, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!existing.IsEnabled)
            return await RefuseAsync<IssuedApiKey>(actor, AccessAuditKind.ApiKeyCreated, requiredPermission, targetResource,
                TicketFailure.Conflict(existing.Version, ["Cannot issue an API key for a disabled service account."], code: "service-account-disabled"),
                cancellationToken: cancellationToken).ConfigureAwait(false);

        var (keyPrefix, plaintextKey, keyHashHex) = GenerateApiKey();
        var keyId = ApiKeyId.NewId();
        var createResult = await apiKeyStore.CreateAsync(
            keyId,
            partition,
            existing.Account.ClientId,
            request.Name,
            keyPrefix,
            keyHashHex,
            FormatActorId(actor),
            request.ExpiresAtUtc,
            cancellationToken).ConfigureAwait(false);
        if (createResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<IssuedApiKey>(actor, AccessAuditKind.ApiKeyCreated, requiredPermission, targetResource, failure, cancellationToken: cancellationToken).ConfigureAwait(false);

        createResult.TryGetValue(out TicketValue<StoredApiKey> ok);
        var keyResource = TargetForAudit(actor.Scope, existing.Account.Scope, ok.Value.Id.Value);
        // Never log plaintext key or hash; audit carries only the opaque key ID and non-secret prefix.
        var auditEvent = await RecordAuditAsync(actor, AccessAuditKind.ApiKeyCreated, AccessAuditOutcome.Allowed, requiredPermission, keyResource,
            justification: $"prefix:{ok.Value.KeyPrefix}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<IssuedApiKey>(new IssuedApiKey(ok.Value, plaintextKey), auditEvent.Id));
    }

    public async ValueTask<TicketResult<AccessChangeApplied<StoredApiKey>>> RevokeApiKeyAsync(
        PrincipalRef actor,
        ApiKeyId keyId,
        string? reason = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!keyId.IsInitialized)
            return TicketFailure.Invalid("An API key identifier is required.");
        if (!string.IsNullOrWhiteSpace(reason) && !AccessText.IsValidText(reason, AccessGrant.MaximumJustificationLength))
            return TicketFailure.Invalid("Revocation reason must be at most 1024 non-control characters.");

        var safeReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        var fallbackResource = new AccessResource(actor.Scope, resourceId: keyId.Value);
        var (baseFailure, context) = await AuthorizeBaseAdminAsync(actor, cancellationToken).ConfigureAwait(false);
        if (baseFailure is not null)
            return await RefuseAsync<StoredApiKey>(actor, AccessAuditKind.ApiKeyRevoked, AccessPermissions.AccessManage, fallbackResource, baseFailure, safeReason, cancellationToken).ConfigureAwait(false);

        var partition = AccessPartition.From(actor.Scope);
        var existingKey = await apiKeyStore.FindByIdAsync(partition, keyId, cancellationToken).ConfigureAwait(false);
        if (existingKey is null)
            return await RefuseAsync<StoredApiKey>(actor, AccessAuditKind.ApiKeyRevoked, AccessPermissions.AccessManage, fallbackResource, TicketFailure.NotFound, safeReason, cancellationToken).ConfigureAwait(false);

        var owningAccount = await serviceAccountStore.FindAsync(partition, existingKey.ClientId, cancellationToken).ConfigureAwait(false);
        var targetScope = owningAccount?.Account.Scope ?? TenantGrantScope.Instance;
        var requiredPermission = GetRequiredAdminPermission(targetScope);
        var targetResource = TargetForAudit(actor.Scope, targetScope, keyId.Value);

        var (scopeFailure, _) = await AuthorizeScopeManagementAsync(actor, partition, targetScope, requiredPermission, context, cancellationToken).ConfigureAwait(false);
        if (scopeFailure is not null)
            return await RefuseAsync<StoredApiKey>(actor, AccessAuditKind.ApiKeyRevoked, requiredPermission, targetResource, scopeFailure, safeReason, cancellationToken).ConfigureAwait(false);

        var revokeResult = await apiKeyStore.RevokeAsync(partition, keyId, FormatActorId(actor), safeReason, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (revokeResult.TryGetValue(out TicketFailure failure))
            return await RefuseAsync<StoredApiKey>(actor, AccessAuditKind.ApiKeyRevoked, requiredPermission, targetResource, failure, safeReason, cancellationToken).ConfigureAwait(false);

        revokeResult.TryGetValue(out TicketValue<StoredApiKey> ok);
        var auditEvent = await RecordAuditAsync(actor, AccessAuditKind.ApiKeyRevoked, AccessAuditOutcome.Allowed, requiredPermission, targetResource, justification: safeReason, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TicketResult.Ok(new AccessChangeApplied<StoredApiKey>(ok.Value, auditEvent.Id));
    }

    public async ValueTask<ApiKeyAuthenticationResult> AuthenticateApiKeyAsync(
        string plaintextKey,
        TicketScope targetScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetScope);

        if (!TryExtractPrefix(plaintextKey, out var prefix))
            return ApiKeyAuthenticationResult.Failed("API key format is invalid.");

        var providedHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey));
        var candidates = await apiKeyStore.FindByPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            return ApiKeyAuthenticationResult.Failed("Invalid API key.");

        StoredApiKeySecret? matched = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            byte[] storedHashBytes;
            try
            {
                storedHashBytes = Convert.FromHexString(candidate.KeyHashHex);
            }
            catch (FormatException)
            {
                continue;
            }

            if (storedHashBytes.Length == providedHashBytes.Length
                && CryptographicOperations.FixedTimeEquals(providedHashBytes, storedHashBytes))
            {
                matched = candidate;
                break;
            }
        }

        if (matched is null)
            return ApiKeyAuthenticationResult.Failed("Invalid API key.");

        var now = engine.Now;
        if (matched.Metadata.IsRevoked)
            return ApiKeyAuthenticationResult.Failed("API key has been revoked.");
        if (matched.Metadata.IsExpiredAt(now))
            return ApiKeyAuthenticationResult.Failed("API key has expired.");

        var serviceAccount = await serviceAccountStore.FindAsync(matched.Metadata.Partition, matched.Metadata.ClientId, cancellationToken).ConfigureAwait(false);
        if (serviceAccount is null)
            return ApiKeyAuthenticationResult.Failed("Service account was not found.");
        if (!serviceAccount.IsEnabled)
            return ApiKeyAuthenticationResult.Failed("Service account is disabled.");
        if (!serviceAccount.Account.AppliesTo(targetScope))
            return ApiKeyAuthenticationResult.Failed("Service account is outside its permitted scope.");

        await apiKeyStore.RecordUsageAsync(matched.Metadata.Partition, matched.Metadata.Id, now, cancellationToken).ConfigureAwait(false);

        var updatedLastUsed = matched.Metadata.LastUsedAtUtc is { } prior && prior >= now ? prior : now;
        var updatedMetadata = new StoredApiKey(
            matched.Metadata.Id,
            matched.Metadata.Partition,
            matched.Metadata.ClientId,
            matched.Metadata.Name,
            matched.Metadata.KeyPrefix,
            matched.Metadata.Version,
            matched.Metadata.CreatedAtUtc,
            matched.Metadata.CreatedByPrincipalId,
            matched.Metadata.ExpiresAtUtc,
            lastUsedAtUtc: updatedLastUsed,
            matched.Metadata.RevokedAtUtc,
            matched.Metadata.RevokedByPrincipalId,
            matched.Metadata.RevocationReason);

        var principal = new ApiClientPrincipal(targetScope, serviceAccount.Account.ClientId);
        return ApiKeyAuthenticationResult.Authenticated(principal, serviceAccount, updatedMetadata);
    }

    private async Task<(TicketFailure? Failure, AccessContext? Context)> AuthorizeBaseAdminAsync(PrincipalRef actor, CancellationToken cancellationToken)
    {
        if (engine.Precheck(actor, AccessPermissions.AccessManage, actor.Scope) is { } precheck)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: precheck.ToString()), null);

        var (saDenial, _) = await engine.CheckServiceAccountAsync(actor, actor.Scope, resource: null, cancellationToken).ConfigureAwait(false);
        if (saDenial is { } saReason)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: saReason.ToString()), null);

        var context = await engine.LoadAsync(actor, actor.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);
        var workspaceTarget = new AccessResource(actor.Scope);

        var hasManage = AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManage, workspaceTarget, includeBreakGlass: false).Allowed;
        var hasManageTenant = AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManageTenant, workspaceTarget, includeBreakGlass: false, tenantAllowsOnly: true).Allowed;
        if (hasManage || hasManageTenant)
            return (null, context);

        var viaBreakGlass = AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManage, workspaceTarget, includeBreakGlass: true).Allowed
            || AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManageTenant, workspaceTarget, includeBreakGlass: true, tenantAllowsOnly: true).Allowed;
        if (viaBreakGlass)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["Break-glass elevation cannot be used for service account administration."], Code: "break-glass-not-allowed"), context);

        return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor lacks required administrative permission."], Code: "forbidden"), context);
    }

    private async Task<(TicketFailure? Failure, AccessContext? Context)> AuthorizeScopeManagementAsync(
        PrincipalRef actor,
        AccessPartition targetPartition,
        GrantScope targetGrantScope,
        Permission requiredPermission,
        AccessContext? context,
        CancellationToken cancellationToken)
    {
        var actorPartition = AccessPartition.From(actor.Scope);
        if (targetPartition != actorPartition)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The target partition is outside the actor's scope."], Code: "out-of-scope"), context);

        if (AccessEngine.WorkspaceOf(targetGrantScope) is { } targetWorkspace && targetWorkspace != actor.Scope.WorkspaceId)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The target workspace is outside the actor's scope."], Code: "out-of-scope"), context);

        if (engine.Precheck(actor, requiredPermission, actor.Scope) is { } precheck)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: precheck.ToString()), context);

        var targetResource = AccessEngine.Target(actor.Scope, targetGrantScope);
        var (saDenial, _) = await engine.CheckServiceAccountAsync(actor, actor.Scope, targetResource, cancellationToken).ConfigureAwait(false);
        if (saDenial is { } saReason)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["The actor is not authorized."], Code: saReason.ToString()), context);

        context ??= await engine.LoadAsync(actor, actor.Scope, cancellationToken, forAdministration: true).ConfigureAwait(false);
        var tenantOnly = targetGrantScope is TenantGrantScope;
        var standard = AccessEngine.EvaluateSimple(context, requiredPermission, targetResource, includeBreakGlass: false, tenantAllowsOnly: tenantOnly);
        if (standard.Allowed)
            return (null, context);

        if (!tenantOnly)
        {
            var tenantAdmin = AccessEngine.EvaluateSimple(context, AccessPermissions.AccessManageTenant, new AccessResource(actor.Scope), includeBreakGlass: false, tenantAllowsOnly: true);
            if (tenantAdmin.Allowed)
                return (null, context);
        }

        var withBreakGlass = AccessEngine.EvaluateSimple(context, requiredPermission, targetResource, includeBreakGlass: true, tenantAllowsOnly: tenantOnly);
        if (withBreakGlass.Allowed)
            return (new TicketFailure(TicketFailureKind.Forbidden, ["Break-glass elevation cannot be used for service account administration."], Code: "break-glass-not-allowed"), context);

        return (new TicketFailure(TicketFailureKind.Forbidden, [$"The actor lacks standard permission '{requiredPermission}' on the target scope."], Code: "forbidden"), context);
    }

    private async Task<TicketResult<AccessChangeApplied<T>>> RefuseAsync<T>(
        PrincipalRef actor,
        AccessAuditKind kind,
        Permission permission,
        AccessResource resource,
        TicketFailure failure,
        string? justification = null,
        CancellationToken cancellationToken = default)
    {
        var denialReason = failure.Code switch
        {
            "out-of-scope" => AccessDenialReason.ScopeMismatch,
            _ when failure.Kind == TicketFailureKind.Forbidden => AccessDenialReason.NoMatchingGrant,
            _ => (AccessDenialReason?)null,
        };
        await RecordAuditAsync(actor, kind, AccessAuditOutcome.Denied, permission, resource, denialReason, justification, cancellationToken).ConfigureAwait(false);
        return failure;
    }

    private async Task<AccessAuditEvent> RecordAuditAsync(
        PrincipalRef actor,
        AccessAuditKind kind,
        AccessAuditOutcome outcome,
        Permission permission,
        AccessResource resource,
        AccessDenialReason? denialReason = null,
        string? justification = null,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = AccessEngine.AuditEvent(
            engine.Now,
            actor,
            kind,
            outcome,
            permission,
            resource,
            denialReason: denialReason,
            justification: justification);
        await auditSink.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return auditEvent;
    }

    private static bool IsVisibleInWorkspace(ServiceAccount account, TicketScope actorScope) =>
        account.Partition == AccessPartition.From(actorScope)
        && (account.Workspace is not { } workspace || workspace == actorScope.WorkspaceId);

    private static Permission GetRequiredAdminPermission(GrantScope scope) =>
        scope is TenantGrantScope ? AccessPermissions.AccessManageTenant : AccessPermissions.AccessManage;

    private static AccessResource TargetForAudit(TicketScope actorScope, GrantScope grantScope, string resourceId) => grantScope switch
    {
        TeamGrantScope team when team.Workspace == actorScope.WorkspaceId => new(actorScope, resourceId: resourceId, team: team.Team),
        InboxGrantScope inbox when inbox.Workspace == actorScope.WorkspaceId => new(actorScope, resourceId: resourceId, inbox: inbox.Inbox),
        _ => new(actorScope, resourceId: resourceId),
    };

    private static string FormatActorId(PrincipalRef actor)
    {
        var accessActor = AccessActor.From(actor);
        var kind = accessActor.Kind switch
        {
            AccessActorKind.User => "user",
            AccessActorKind.ApiClient => "client",
            AccessActorKind.Automation => "automation",
            AccessActorKind.System => "system",
            _ => "actor",
        };
        var formatted = $"{kind}:{accessActor.Id}";
        return formatted.Length <= TicketScope.MaximumIdentifierLength ? formatted : accessActor.Id;
    }

    private static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    private static (string KeyPrefix, string PlaintextKey, string KeyHashHex) GenerateApiKey()
    {
        var prefixBytes = RandomNumberGenerator.GetBytes(4);
        var keyPrefix = $"{KeyPrefixMarker}{Convert.ToHexStringLower(prefixBytes)}";
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64Url.EncodeToString(secretBytes);
        var plaintextKey = $"{keyPrefix}.{secret}";
        var keyHashHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey)));
        return (keyPrefix, plaintextKey, keyHashHex);
    }

    private static bool TryExtractPrefix(string? plaintextKey, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrEmpty(plaintextKey) || plaintextKey.Length > IssuedApiKey.MaximumPlaintextLength)
            return false;
        if (!plaintextKey.StartsWith(KeyPrefixMarker, StringComparison.Ordinal))
            return false;

        var dotIndex = plaintextKey.IndexOf('.');
        if (dotIndex <= KeyPrefixMarker.Length
            || dotIndex > StoredApiKey.MaximumKeyPrefixLength
            || dotIndex >= plaintextKey.Length - 1
            || plaintextKey.IndexOf('.', dotIndex + 1) >= 0)
            return false;

        for (var i = 0; i < plaintextKey.Length; i++)
            if (char.IsWhiteSpace(plaintextKey[i]) || char.IsControl(plaintextKey[i]))
                return false;

        prefix = plaintextKey[..dotIndex];
        return true;
    }
}
