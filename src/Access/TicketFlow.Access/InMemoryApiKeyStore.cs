using System.Collections.Concurrent;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Development-only, per-partition <see cref="Lock"/>-synchronized in-memory <see cref="IApiKeyStore"/>.</summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore, IDisposable
{
    private readonly ConcurrentDictionary<AccessPartition, PartitionState> partitions = new();
    private readonly Lock prefixGate = new();
    private readonly Dictionary<string, Dictionary<(AccessPartition Partition, ApiKeyId KeyId), StoredApiKeySecret>> byPrefix = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private int disposed;

    public InMemoryApiKeyStore(TimeProvider? timeProvider = null) =>
        this.timeProvider = timeProvider ?? TimeProvider.System;

    public ValueTask<StoredApiKey?> FindByIdAsync(AccessPartition partition, ApiKeyId keyId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!keyId.IsInitialized || !partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<StoredApiKey?>(null);

        lock (state.Gate)
            return ValueTask.FromResult<StoredApiKey?>(state.Keys.TryGetValue(keyId, out var stored) ? stored.Metadata : null);
    }

    public ValueTask<IReadOnlyList<StoredApiKeySecret>> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(keyPrefix))
            return ValueTask.FromResult<IReadOnlyList<StoredApiKeySecret>>([]);

        var normalizedPrefix = keyPrefix.Trim();
        List<StoredApiKeySecret>? results = null;
        lock (prefixGate)
            if (byPrefix.TryGetValue(normalizedPrefix, out var matches) && matches.Count > 0)
                results = new List<StoredApiKeySecret>(matches.Values);

        if (results is null || results.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<StoredApiKeySecret>>([]);

        results.Sort(static (left, right) => CompareKeys(left.Metadata, right.Metadata));
        return ValueTask.FromResult<IReadOnlyList<StoredApiKeySecret>>(results.AsReadOnly());
    }

    public ValueTask<IReadOnlyList<StoredApiKey>> ListForClientAsync(AccessPartition partition, ApiClientId clientId, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!clientId.IsInitialized)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));

        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<IReadOnlyList<StoredApiKey>>([]);

        List<StoredApiKey>? results = null;
        lock (state.Gate)
            if (state.KeysByClient.TryGetValue(clientId, out var clientKeys) && clientKeys.Count > 0)
            {
                var now = timeProvider.GetUtcNow();
                results = new List<StoredApiKey>(clientKeys.Count);
                foreach (var stored in clientKeys.Values)
                    if (includeInactive || stored.Metadata.IsActiveAt(now))
                        results.Add(stored.Metadata);
            }

        if (results is null || results.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<StoredApiKey>>([]);

        results.Sort(CompareKeys);
        return ValueTask.FromResult<IReadOnlyList<StoredApiKey>>(results.AsReadOnly());
    }

    public ValueTask<IReadOnlyList<StoredApiKey>> ListForPartitionAsync(AccessPartition partition, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<IReadOnlyList<StoredApiKey>>([]);

        List<StoredApiKey>? results = null;
        lock (state.Gate)
            if (state.Keys.Count > 0)
            {
                var now = timeProvider.GetUtcNow();
                results = new List<StoredApiKey>(state.Keys.Count);
                foreach (var stored in state.Keys.Values)
                    if (includeInactive || stored.Metadata.IsActiveAt(now))
                        results.Add(stored.Metadata);
            }

        if (results is null || results.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<StoredApiKey>>([]);

        results.Sort(CompareKeys);
        return ValueTask.FromResult<IReadOnlyList<StoredApiKey>>(results.AsReadOnly());
    }

    public ValueTask<TicketResult<StoredApiKey>> CreateAsync(
        ApiKeyId keyId,
        AccessPartition partition,
        ApiClientId clientId,
        string name,
        string keyPrefix,
        string keyHashHex,
        string actorPrincipalId,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!keyId.IsInitialized)
            return ValueTask.FromResult<TicketResult<StoredApiKey>>(TicketFailure.Invalid("An API key identifier is required."));
        if (!clientId.IsInitialized)
            return ValueTask.FromResult<TicketResult<StoredApiKey>>(TicketFailure.Invalid("An API client identifier is required."));

        var state = partitions.GetOrAdd(partition, static _ => new PartitionState());
        lock (state.Gate)
        {
            if (state.Keys.TryGetValue(keyId, out var existing))
                return ValueTask.FromResult<TicketResult<StoredApiKey>>(
                    TicketFailure.Conflict(existing.Metadata.Version, [$"API key '{keyId.Value}' already exists."], code: "already-exists"));

            var now = timeProvider.GetUtcNow();
            var metadata = new StoredApiKey(
                keyId,
                partition,
                clientId,
                name,
                keyPrefix,
                version: 1,
                createdAtUtc: now,
                createdByPrincipalId: actorPrincipalId,
                expiresAtUtc: expiresAtUtc);
            var secret = new StoredApiKeySecret(metadata, keyHashHex);

            state.Keys[keyId] = secret;
            if (!state.KeysByClient.TryGetValue(clientId, out var clientKeys))
                state.KeysByClient[clientId] = clientKeys = [];
            clientKeys[keyId] = secret;
            IndexPrefixEntry(secret);

            return ValueTask.FromResult(TicketResult.Ok(metadata));
        }
    }

    public ValueTask<TicketResult<StoredApiKey>> RevokeAsync(
        AccessPartition partition,
        ApiKeyId keyId,
        string actorPrincipalId,
        string? reason = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!keyId.IsInitialized)
            return ValueTask.FromResult<TicketResult<StoredApiKey>>(TicketFailure.Invalid("An API key identifier is required."));

        var normalizedActor = TicketScope.RequireIdentifier(actorPrincipalId, nameof(actorPrincipalId));
        if (expectedVersion is < 0)
            return ValueTask.FromResult<TicketResult<StoredApiKey>>(TicketFailure.Invalid("Expected version cannot be negative."));

        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<TicketResult<StoredApiKey>>(TicketFailure.NotFound);

        lock (state.Gate)
        {
            if (!state.Keys.TryGetValue(keyId, out var existing))
                return ValueTask.FromResult<TicketResult<StoredApiKey>>(TicketFailure.NotFound);

            if (expectedVersion is { } expected && existing.Metadata.Version != expected)
                return ValueTask.FromResult<TicketResult<StoredApiKey>>(
                    TicketFailure.Conflict(existing.Metadata.Version, [$"API key '{keyId.Value}' is at version {existing.Metadata.Version}, expected {expected}."], code: "concurrency-conflict"));

            if (existing.Metadata.IsRevoked)
                return ValueTask.FromResult<TicketResult<StoredApiKey>>(
                    TicketFailure.Conflict(existing.Metadata.Version, [$"API key '{keyId.Value}' is already revoked."], code: "already-revoked"));

            var now = timeProvider.GetUtcNow();
            var revoked = CreateRevokedSecret(existing, now, normalizedActor, reason);
            StoreUpdatedUnlocked(state, revoked);
            return ValueTask.FromResult(TicketResult.Ok(revoked.Metadata));
        }
    }

    public ValueTask<int> RevokeAllForClientAsync(
        AccessPartition partition,
        ApiClientId clientId,
        string actorPrincipalId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!clientId.IsInitialized)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));

        var normalizedActor = TicketScope.RequireIdentifier(actorPrincipalId, nameof(actorPrincipalId));
        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult(0);

        lock (state.Gate)
        {
            if (!state.KeysByClient.TryGetValue(clientId, out var clientKeys) || clientKeys.Count == 0)
                return ValueTask.FromResult(0);

            var now = timeProvider.GetUtcNow();
            List<StoredApiKeySecret>? toRevoke = null;
            foreach (var existing in clientKeys.Values)
                if (existing.Metadata.IsActiveAt(now))
                    (toRevoke ??= []).Add(CreateRevokedSecret(existing, now, normalizedActor, reason));

            if (toRevoke is null)
                return ValueTask.FromResult(0);

            foreach (var revoked in toRevoke)
                StoreUpdatedUnlocked(state, revoked);

            return ValueTask.FromResult(toRevoke.Count);
        }
    }

    public ValueTask RecordUsageAsync(AccessPartition partition, ApiKeyId keyId, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!keyId.IsInitialized || !partitions.TryGetValue(partition, out var state))
            return ValueTask.CompletedTask;

        lock (state.Gate)
        {
            if (!state.Keys.TryGetValue(keyId, out var existing))
                return ValueTask.CompletedTask;

            if (existing.Metadata.LastUsedAtUtc is { } lastUsed && usedAtUtc <= lastUsed)
                return ValueTask.CompletedTask;

            var metadata = existing.Metadata;
            var updatedMetadata = new StoredApiKey(
                metadata.Id,
                metadata.Partition,
                metadata.ClientId,
                metadata.Name,
                metadata.KeyPrefix,
                metadata.Version,
                metadata.CreatedAtUtc,
                metadata.CreatedByPrincipalId,
                metadata.ExpiresAtUtc,
                lastUsedAtUtc: usedAtUtc,
                metadata.RevokedAtUtc,
                metadata.RevokedByPrincipalId,
                metadata.RevocationReason);
            StoreUpdatedUnlocked(state, new StoredApiKeySecret(updatedMetadata, existing.KeyHashHex));
            return ValueTask.CompletedTask;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        partitions.Clear();
        lock (prefixGate)
            byPrefix.Clear();
    }

    private void StoreUpdatedUnlocked(PartitionState state, StoredApiKeySecret updated)
    {
        var metadata = updated.Metadata;
        state.Keys[metadata.Id] = updated;
        if (state.KeysByClient.TryGetValue(metadata.ClientId, out var clientKeys))
            clientKeys[metadata.Id] = updated;
        IndexPrefixEntry(updated);
    }

    private void IndexPrefixEntry(StoredApiKeySecret secret)
    {
        var metadata = secret.Metadata;
        lock (prefixGate)
        {
            if (!byPrefix.TryGetValue(metadata.KeyPrefix, out var bucket))
                byPrefix[metadata.KeyPrefix] = bucket = [];
            bucket[(metadata.Partition, metadata.Id)] = secret;
        }
    }

    private static StoredApiKeySecret CreateRevokedSecret(StoredApiKeySecret existing, DateTimeOffset revokedAtUtc, string actorPrincipalId, string? reason)
    {
        var metadata = existing.Metadata;
        var revokedMetadata = new StoredApiKey(
            metadata.Id,
            metadata.Partition,
            metadata.ClientId,
            metadata.Name,
            metadata.KeyPrefix,
            checked(metadata.Version + 1),
            metadata.CreatedAtUtc,
            metadata.CreatedByPrincipalId,
            metadata.ExpiresAtUtc,
            metadata.LastUsedAtUtc,
            revokedAtUtc: revokedAtUtc,
            revokedByPrincipalId: actorPrincipalId,
            revocationReason: reason);
        return new StoredApiKeySecret(revokedMetadata, existing.KeyHashHex);
    }

    private static int CompareKeys(StoredApiKey left, StoredApiKey right)
    {
        var createdComparison = left.CreatedAtUtc.CompareTo(right.CreatedAtUtc);
        return createdComparison != 0 ? createdComparison : left.Id.CompareTo(right.Id);
    }

    private sealed class PartitionState
    {
        public Lock Gate { get; } = new();
        public Dictionary<ApiKeyId, StoredApiKeySecret> Keys { get; } = [];
        public Dictionary<ApiClientId, Dictionary<ApiKeyId, StoredApiKeySecret>> KeysByClient { get; } = [];
    }
}
