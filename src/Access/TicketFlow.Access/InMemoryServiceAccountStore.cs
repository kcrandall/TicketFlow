using System.Collections.Concurrent;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Development-only, per-partition <see cref="Lock"/>-synchronized in-memory <see cref="IServiceAccountStore"/>.</summary>
public sealed class InMemoryServiceAccountStore : IServiceAccountStore, IDisposable
{
    private readonly ConcurrentDictionary<AccessPartition, PartitionState> partitions = new();
    private readonly TimeProvider timeProvider;
    private int disposed;

    public InMemoryServiceAccountStore(TimeProvider? timeProvider = null) =>
        this.timeProvider = timeProvider ?? TimeProvider.System;

    public ValueTask<StoredServiceAccount?> FindAsync(AccessPartition partition, ApiClientId clientId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!clientId.IsInitialized)
            return ValueTask.FromResult<StoredServiceAccount?>(null);

        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<StoredServiceAccount?>(null);

        lock (state.Gate)
            return ValueTask.FromResult<StoredServiceAccount?>(state.Accounts.GetValueOrDefault(clientId));
    }

    public ValueTask<IReadOnlyList<StoredServiceAccount>> ListForPartitionAsync(AccessPartition partition, bool includeDisabled = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<IReadOnlyList<StoredServiceAccount>>([]);

        List<StoredServiceAccount>? results = null;
        lock (state.Gate)
            if (state.Accounts.Count > 0)
            {
                results = new List<StoredServiceAccount>(state.Accounts.Count);
                foreach (var stored in state.Accounts.Values)
                    if (includeDisabled || stored.IsEnabled)
                        results.Add(stored);
            }

        if (results is null || results.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<StoredServiceAccount>>([]);

        results.Sort(static (left, right) => left.Account.ClientId.CompareTo(right.Account.ClientId));
        return ValueTask.FromResult<IReadOnlyList<StoredServiceAccount>>(results.AsReadOnly());
    }

    public ValueTask<TicketResult<StoredServiceAccount>> CreateAsync(ServiceAccount account, string actorPrincipalId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(account);
        var normalizedActor = TicketScope.RequireIdentifier(actorPrincipalId, nameof(actorPrincipalId));

        var state = partitions.GetOrAdd(account.Partition, static _ => new PartitionState());
        lock (state.Gate)
        {
            if (state.Accounts.TryGetValue(account.ClientId, out var existing))
                return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(
                    TicketFailure.Conflict(existing.Version, [$"Service account '{account.ClientId.Value}' already exists."], code: "already-exists"));

            var now = timeProvider.GetUtcNow();
            var created = new StoredServiceAccount(
                account,
                isEnabled: true,
                version: 1,
                createdAtUtc: now,
                createdByPrincipalId: normalizedActor,
                updatedAtUtc: now,
                updatedByPrincipalId: normalizedActor);
            state.Accounts[account.ClientId] = created;
            return ValueTask.FromResult(TicketResult.Ok(created));
        }
    }

    public ValueTask<TicketResult<StoredServiceAccount>> UpdateAsync(ServiceAccount account, string actorPrincipalId, long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(account);
        var normalizedActor = TicketScope.RequireIdentifier(actorPrincipalId, nameof(actorPrincipalId));

        if (expectedVersion is < 0)
            return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(TicketFailure.Invalid("Expected version cannot be negative."));

        if (!partitions.TryGetValue(account.Partition, out var state))
            return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(TicketFailure.NotFound);

        lock (state.Gate)
        {
            if (!state.Accounts.TryGetValue(account.ClientId, out var existing))
                return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(TicketFailure.NotFound);

            if (expectedVersion is { } expected && existing.Version != expected)
                return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(
                    TicketFailure.Conflict(existing.Version, [$"Service account '{account.ClientId.Value}' is at version {existing.Version}, expected {expected}."], code: "concurrency-conflict"));

            var now = timeProvider.GetUtcNow();
            var updated = new StoredServiceAccount(
                account,
                existing.IsEnabled,
                checked(existing.Version + 1),
                existing.CreatedAtUtc,
                existing.CreatedByPrincipalId,
                now,
                normalizedActor,
                existing.DisabledAtUtc,
                existing.DisabledByPrincipalId,
                existing.DisabledReason);
            state.Accounts[account.ClientId] = updated;
            return ValueTask.FromResult(TicketResult.Ok(updated));
        }
    }

    public ValueTask<TicketResult<StoredServiceAccount>> SetEnabledAsync(
        AccessPartition partition,
        ApiClientId clientId,
        bool isEnabled,
        string actorPrincipalId,
        string? reason = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!clientId.IsInitialized)
            return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(TicketFailure.Invalid("An API client identifier is required."));

        var normalizedActor = TicketScope.RequireIdentifier(actorPrincipalId, nameof(actorPrincipalId));
        if (expectedVersion is < 0)
            return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(TicketFailure.Invalid("Expected version cannot be negative."));

        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(TicketFailure.NotFound);

        lock (state.Gate)
        {
            if (!state.Accounts.TryGetValue(clientId, out var existing))
                return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(TicketFailure.NotFound);

            if (expectedVersion is { } expected && existing.Version != expected)
                return ValueTask.FromResult<TicketResult<StoredServiceAccount>>(
                    TicketFailure.Conflict(existing.Version, [$"Service account '{clientId.Value}' is at version {existing.Version}, expected {expected}."], code: "concurrency-conflict"));

            var now = timeProvider.GetUtcNow();
            var updated = new StoredServiceAccount(
                existing.Account,
                isEnabled,
                checked(existing.Version + 1),
                existing.CreatedAtUtc,
                existing.CreatedByPrincipalId,
                now,
                normalizedActor,
                disabledAtUtc: isEnabled ? null : now,
                disabledByPrincipalId: isEnabled ? null : normalizedActor,
                disabledReason: isEnabled ? null : reason);
            state.Accounts[clientId] = updated;
            return ValueTask.FromResult(TicketResult.Ok(updated));
        }
    }

    public ValueTask<TicketResult<bool>> DeleteAsync(AccessPartition partition, ApiClientId clientId, long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!clientId.IsInitialized)
            return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.Invalid("An API client identifier is required."));

        if (expectedVersion is < 0)
            return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.Invalid("Expected version cannot be negative."));

        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.NotFound);

        lock (state.Gate)
        {
            if (!state.Accounts.TryGetValue(clientId, out var existing))
                return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.NotFound);

            if (expectedVersion is { } expected && existing.Version != expected)
                return ValueTask.FromResult<TicketResult<bool>>(
                    TicketFailure.Conflict(existing.Version, [$"Service account '{clientId.Value}' is at version {existing.Version}, expected {expected}."], code: "concurrency-conflict"));

            state.Accounts.Remove(clientId);
            return ValueTask.FromResult(TicketResult.Ok(true));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        partitions.Clear();
    }

    private sealed class PartitionState
    {
        public Lock Gate { get; } = new();
        public Dictionary<ApiClientId, StoredServiceAccount> Accounts { get; } = [];
    }
}
