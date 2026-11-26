using System.Collections.Concurrent;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Development-only, per-partition <see cref="Lock"/>-synchronized in-memory <see cref="IRoleFederationMappingStore"/>.</summary>
public sealed class InMemoryRoleFederationMappingStore : IRoleFederationMappingStore, IDisposable
{
    private readonly ConcurrentDictionary<AccessPartition, PartitionState> partitions = new();
    private readonly TimeProvider timeProvider;
    private int disposed;

    public InMemoryRoleFederationMappingStore(TimeProvider? timeProvider = null) =>
        this.timeProvider = timeProvider ?? TimeProvider.System;

    public ValueTask<StoredRoleFederationMapping?> FindAsync(AccessPartition partition, RoleFederationMappingId mappingId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!mappingId.IsInitialized || !partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<StoredRoleFederationMapping?>(null);

        lock (state.Gate)
            return ValueTask.FromResult<StoredRoleFederationMapping?>(state.Mappings.GetValueOrDefault(mappingId));
    }

    public ValueTask<IReadOnlyList<StoredRoleFederationMapping>> ListForPartitionAsync(AccessPartition partition, bool includeDisabled = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<IReadOnlyList<StoredRoleFederationMapping>>([]);

        List<StoredRoleFederationMapping>? results = null;
        lock (state.Gate)
            if (state.Mappings.Count > 0)
            {
                results = new List<StoredRoleFederationMapping>(state.Mappings.Count);
                foreach (var stored in state.Mappings.Values)
                    if (includeDisabled || stored.IsEnabled)
                        results.Add(stored);
            }

        if (results is null || results.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<StoredRoleFederationMapping>>([]);

        results.Sort(CompareMappings);
        return ValueTask.FromResult<IReadOnlyList<StoredRoleFederationMapping>>(results.AsReadOnly());
    }

    public ValueTask<TicketResult<StoredRoleFederationMapping>> UpsertAsync(
        RoleFederationMappingId mappingId,
        RoleFederationMapping mapping,
        bool isEnabled,
        string actorPrincipalId,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(mapping);

        if (!mappingId.IsInitialized)
            return ValueTask.FromResult<TicketResult<StoredRoleFederationMapping>>(TicketFailure.Invalid("A mapping identifier is required."));

        var normalizedActor = TicketScope.RequireIdentifier(actorPrincipalId, nameof(actorPrincipalId));
        if (expectedVersion is < 0)
            return ValueTask.FromResult<TicketResult<StoredRoleFederationMapping>>(TicketFailure.Invalid("Expected version cannot be negative."));

        var state = partitions.GetOrAdd(mapping.Partition, static _ => new PartitionState());
        lock (state.Gate)
        {
            var exists = state.Mappings.TryGetValue(mappingId, out var existing);
            if (!exists)
            {
                if (expectedVersion is { } expected && expected != 0)
                    return ValueTask.FromResult<TicketResult<StoredRoleFederationMapping>>(
                        TicketFailure.Conflict(null, [$"Role federation mapping '{mappingId.Value}' does not exist, expected version {expected}."], code: "concurrency-conflict"));
            }
            else if (expectedVersion is { } expected && existing!.Version != expected)
                return ValueTask.FromResult<TicketResult<StoredRoleFederationMapping>>(
                    TicketFailure.Conflict(existing.Version, [$"Role federation mapping '{mappingId.Value}' is at version {existing.Version}, expected {expected}."], code: "concurrency-conflict"));

            foreach (var other in state.Mappings.Values)
            {
                if (other.Id == mappingId)
                    continue;

                if (string.Equals(other.Mapping.Provider, mapping.Provider, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(other.Mapping.ExternalRole, mapping.ExternalRole, StringComparison.OrdinalIgnoreCase)
                    && other.Mapping.RoleId == mapping.RoleId
                    && other.Mapping.Scope.Equals(mapping.Scope))
                    return ValueTask.FromResult<TicketResult<StoredRoleFederationMapping>>(
                        TicketFailure.Conflict(other.Version,
                            [$"A role federation mapping already exists for provider '{mapping.Provider}', external role '{mapping.ExternalRole}', and role '{mapping.RoleId.Value}'."],
                            code: "already-exists"));
            }

            var now = timeProvider.GetUtcNow();
            var stored = exists
                ? new StoredRoleFederationMapping(
                    mappingId,
                    mapping,
                    isEnabled,
                    checked(existing!.Version + 1),
                    existing.CreatedAtUtc,
                    existing.CreatedByPrincipalId,
                    now,
                    normalizedActor)
                : new StoredRoleFederationMapping(
                    mappingId,
                    mapping,
                    isEnabled,
                    version: 1,
                    createdAtUtc: now,
                    createdByPrincipalId: normalizedActor,
                    updatedAtUtc: now,
                    updatedByPrincipalId: normalizedActor);

            state.Mappings[mappingId] = stored;
            return ValueTask.FromResult(TicketResult.Ok(stored));
        }
    }

    public ValueTask<TicketResult<bool>> DeleteAsync(AccessPartition partition, RoleFederationMappingId mappingId, long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(partition);
        if (!mappingId.IsInitialized)
            return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.Invalid("A mapping identifier is required."));

        if (expectedVersion is < 0)
            return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.Invalid("Expected version cannot be negative."));

        if (!partitions.TryGetValue(partition, out var state))
            return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.NotFound);

        lock (state.Gate)
        {
            if (!state.Mappings.TryGetValue(mappingId, out var existing))
                return ValueTask.FromResult<TicketResult<bool>>(TicketFailure.NotFound);

            if (expectedVersion is { } expected && existing.Version != expected)
                return ValueTask.FromResult<TicketResult<bool>>(
                    TicketFailure.Conflict(existing.Version, [$"Role federation mapping '{mappingId.Value}' is at version {existing.Version}, expected {expected}."], code: "concurrency-conflict"));

            state.Mappings.Remove(mappingId);
            return ValueTask.FromResult(TicketResult.Ok(true));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        partitions.Clear();
    }

    private static int CompareMappings(StoredRoleFederationMapping left, StoredRoleFederationMapping right)
    {
        var providerComparison = string.Compare(left.Mapping.Provider, right.Mapping.Provider, StringComparison.OrdinalIgnoreCase);
        if (providerComparison != 0)
            return providerComparison;

        var externalRoleComparison = string.Compare(left.Mapping.ExternalRole, right.Mapping.ExternalRole, StringComparison.OrdinalIgnoreCase);
        if (externalRoleComparison != 0)
            return externalRoleComparison;

        var roleComparison = left.Mapping.RoleId.CompareTo(right.Mapping.RoleId);
        return roleComparison != 0 ? roleComparison : left.Id.CompareTo(right.Id);
    }

    private sealed class PartitionState
    {
        public Lock Gate { get; } = new();
        public Dictionary<RoleFederationMappingId, StoredRoleFederationMapping> Mappings { get; } = [];
    }
}
