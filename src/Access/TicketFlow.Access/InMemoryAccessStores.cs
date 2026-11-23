using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Development-only custom role store. State is process-local, lost on restart and never shared between processes; it is an
/// example of the <see cref="IAccessRoleStore"/> contract, not durable storage. Writes are serialized under one lock.
/// </summary>
public sealed class InMemoryAccessRoleStore(int maximumEntries = 10_000) : IAccessRoleStore
{
    private readonly InMemoryScopedTable<RoleId, StoredAccessRole> table = new(maximumEntries, AccessLimits.MaximumCustomRolesPerWorkspace);

    public Task<StoredAccessRole?> GetAsync(TicketScope scope, RoleId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult(table.Get(scope, id));
    }

    public Task<IReadOnlyList<StoredAccessRole>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult<IReadOnlyList<StoredAccessRole>>([.. table.List(scope).OrderBy(role => role.Role.Id)]);
    }

    public Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> CreateAsync(TicketScope scope, AccessRole role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(role);
        if (role.IsBuiltIn || BuiltInRoles.IsBuiltIn(role.Id))
            return Task.FromResult<StoreWriteResult<StoredAccessRole, AccessWriteRejection>>(new StoreRejected<AccessWriteRejection>(AccessWriteRejection.BuiltInRoleImmutable));
        return Task.FromResult(table.Create(scope, role.Id, revision => new StoredAccessRole(scope, role, revision), static entry => entry.Revision));
    }

    public Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> UpdateAsync(TicketScope scope, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (role.IsBuiltIn || BuiltInRoles.IsBuiltIn(role.Id))
            return Task.FromResult<StoreWriteResult<StoredAccessRole, AccessWriteRejection>>(new StoreRejected<AccessWriteRejection>(AccessWriteRejection.BuiltInRoleImmutable));
        return Task.FromResult(table.Update(scope, role.Id, expectedRevision, revision => new StoredAccessRole(scope, role, revision), static entry => entry.Revision));
    }

    public Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> DeleteAsync(TicketScope scope, RoleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (BuiltInRoles.IsBuiltIn(id))
            return Task.FromResult<StoreWriteResult<StoredAccessRole, AccessWriteRejection>>(new StoreRejected<AccessWriteRejection>(AccessWriteRejection.BuiltInRoleImmutable));
        return Task.FromResult(table.Delete(scope, id, expectedRevision, static entry => entry.Revision));
    }
}

/// <summary>Development-only field policy store with the same non-durable, single-lock semantics as <see cref="InMemoryAccessRoleStore"/>.</summary>
public sealed class InMemoryAccessFieldPolicyStore(int maximumEntries = 10_000) : IAccessFieldPolicyStore
{
    private readonly InMemoryScopedTable<FieldPath, StoredAccessFieldPolicy> table = new(maximumEntries, AccessLimits.MaximumFieldPoliciesPerWorkspace);

    public Task<StoredAccessFieldPolicy?> GetAsync(TicketScope scope, FieldPath field, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult(table.Get(scope, field));
    }

    public Task<IReadOnlyList<StoredAccessFieldPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult<IReadOnlyList<StoredAccessFieldPolicy>>([.. table.List(scope).OrderBy(policy => policy.Policy.Field)]);
    }

    public Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> CreateAsync(TicketScope scope, AccessFieldPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        return Task.FromResult(table.Create(scope, policy.Field, revision => new StoredAccessFieldPolicy(scope, policy, revision), static entry => entry.Revision));
    }

    public Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> UpdateAsync(TicketScope scope, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Update(scope, policy.Field, expectedRevision, revision => new StoredAccessFieldPolicy(scope, policy, revision), static entry => entry.Revision));
    }

    public Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> DeleteAsync(TicketScope scope, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Delete(scope, field, expectedRevision, static entry => entry.Revision));
    }
}

/// <summary>
/// Development-only grant store: process-local, lost on restart, never shared between processes. Writes and the per-subject
/// capacity check are serialized under one lock; listing scans the table and is intended for small development data sets.
/// </summary>
public sealed class InMemoryAccessGrantStore : IAccessGrantStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(AccessPartition Partition, GrantId Id), StoredAccessGrant> grants = [];
    private readonly Dictionary<(AccessPartition Partition, GrantSubject Subject), HashSet<GrantId>> bySubject = [];
    private readonly Dictionary<(AccessPartition Partition, GrantScope Scope), HashSet<GrantId>> byTarget = [];

    public InMemoryAccessGrantStore(int maximumEntries = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        MaximumEntries = maximumEntries;
    }

    public int MaximumEntries { get; }

    public Task<StoredAccessGrant?> GetAsync(AccessPartition partition, GrantId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        lock (gate)
            return Task.FromResult(grants.GetValueOrDefault((partition, id)));
    }

    public Task<IReadOnlyList<StoredAccessGrant>> ListForSubjectsAsync(AccessPartition partition, WorkspaceId workspace, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count > AccessLimits.MaximumSubjectsPerQuery)
            throw new ArgumentException($"At most {AccessLimits.MaximumSubjectsPerQuery} subjects can be queried.", nameof(subjects));
        lock (gate)
        {
            var result = new List<StoredAccessGrant>();
            HashSet<GrantSubject>? seenSubjects = subjects.Count > 1 ? [] : null;
            foreach (var subject in subjects)
            {
                if (seenSubjects is not null && !seenSubjects.Add(subject))
                    continue;
                if (!bySubject.TryGetValue((partition, subject), out var ids))
                    continue;
                foreach (var id in ids)
                {
                    var entry = grants[(partition, id)];
                    if (entry.Grant.Workspace is not { } named || named == workspace)
                        result.Add(entry);
                }
            }
            if (result.Count > 1)
                result.Sort(static (left, right) => string.CompareOrdinal(left.Grant.Id.Value, right.Grant.Id.Value));
            return Task.FromResult<IReadOnlyList<StoredAccessGrant>>(result);
        }
    }

    public Task<IReadOnlyList<StoredAccessGrant>> ListForTargetAsync(AccessPartition partition, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        AccessGrantTargets.Validate(target, limit, after);
        lock (gate)
        {
            if (!byTarget.TryGetValue((partition, target), out var ids) || ids.Count == 0)
                return Task.FromResult<IReadOnlyList<StoredAccessGrant>>([]);
            var matching = new List<StoredAccessGrant>(Math.Min(ids.Count, limit));
            foreach (var id in ids)
                if (after is not { } cursor || string.CompareOrdinal(id.Value, cursor.Value) > 0)
                    matching.Add(grants[(partition, id)]);
            if (matching.Count > 1)
                matching.Sort(static (left, right) => string.CompareOrdinal(left.Grant.Id.Value, right.Grant.Id.Value));
            if (matching.Count > limit)
                matching.RemoveRange(limit, matching.Count - limit);
            return Task.FromResult<IReadOnlyList<StoredAccessGrant>>(matching);
        }
    }

    public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (gate)
        {
            if (grants.TryGetValue((grant.Partition, grant.Id), out var existing))
                return Result(new StoreConflict(existing.Revision));
            if (grants.Count >= MaximumEntries || CountForSubject(grant.Partition, grant.Subject, except: null) >= AccessLimits.MaximumGrantsPerSubject)
                return Result(new StoreRejected<AccessWriteRejection>(AccessWriteRejection.CapacityExceeded));
            var stored = new StoredAccessGrant(grant, 1);
            grants.Add((grant.Partition, grant.Id), stored);
            IndexGrant(grant);
            return Result(new StoreApplied<StoredAccessGrant>(stored));
        }
    }

    public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!grants.TryGetValue((grant.Partition, grant.Id), out var existing))
                return Result(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new StoreConflict(existing.Revision));
            if (!existing.Grant.Subject.Equals(grant.Subject) && CountForSubject(grant.Partition, grant.Subject, grant.Id) >= AccessLimits.MaximumGrantsPerSubject)
                return Result(new StoreRejected<AccessWriteRejection>(AccessWriteRejection.CapacityExceeded));
            UnindexGrant(existing.Grant);
            var stored = new StoredAccessGrant(grant, checked(existing.Revision + 1));
            grants[(grant.Partition, grant.Id)] = stored;
            IndexGrant(grant);
            return Result(new StoreApplied<StoredAccessGrant>(stored));
        }
    }

    public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!grants.TryGetValue((partition, id), out var existing))
                return Result(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new StoreConflict(existing.Revision));
            grants.Remove((partition, id));
            UnindexGrant(existing.Grant);
            return Result(new StoreApplied<StoredAccessGrant>(existing));
        }
    }

    public Task<IReadOnlyList<StoredAccessGrant>> DeleteManyAsync(AccessPartition partition, IReadOnlyList<(GrantId Id, long ExpectedRevision)> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(targets);
        lock (gate)
        {
            var removed = new List<StoredAccessGrant>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var (id, expectedRevision) = targets[i];
                ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
                if (grants.TryGetValue((partition, id), out var existing) && existing.Revision == expectedRevision)
                {
                    grants.Remove((partition, id));
                    UnindexGrant(existing.Grant);
                    removed.Add(existing);
                }
            }
            return Task.FromResult<IReadOnlyList<StoredAccessGrant>>(removed);
        }
    }

    private void IndexGrant(AccessGrant grant)
    {
        var subjectKey = (grant.Partition, grant.Subject);
        if (!bySubject.TryGetValue(subjectKey, out var subjectIds))
            bySubject[subjectKey] = subjectIds = [];
        subjectIds.Add(grant.Id);

        if (grant.Scope is not TenantGrantScope)
        {
            var targetKey = (grant.Partition, grant.Scope);
            if (!byTarget.TryGetValue(targetKey, out var targetIds))
                byTarget[targetKey] = targetIds = [];
            targetIds.Add(grant.Id);
        }
    }

    private void UnindexGrant(AccessGrant grant)
    {
        var subjectKey = (grant.Partition, grant.Subject);
        if (bySubject.TryGetValue(subjectKey, out var subjectIds))
        {
            subjectIds.Remove(grant.Id);
            if (subjectIds.Count == 0)
                bySubject.Remove(subjectKey);
        }
        if (grant.Scope is not TenantGrantScope)
        {
            var targetKey = (grant.Partition, grant.Scope);
            if (byTarget.TryGetValue(targetKey, out var targetIds))
            {
                targetIds.Remove(grant.Id);
                if (targetIds.Count == 0)
                    byTarget.Remove(targetKey);
            }
        }
    }

    private int CountForSubject(AccessPartition partition, GrantSubject subject, GrantId? except)
    {
        if (!bySubject.TryGetValue((partition, subject), out var ids))
            return 0;
        return except is { } excluded && ids.Contains(excluded) ? ids.Count - 1 : ids.Count;
    }

    private static Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> Result(StoreWriteResult<StoredAccessGrant, AccessWriteRejection> result) => Task.FromResult(result);
}

/// <summary>
/// Development-only audit sink holding at most <see cref="MaximumEvents"/> events in process memory. When full it throws, so the
/// engine fails closed instead of dropping audit records. Not durable.
/// </summary>
public sealed class InMemoryAccessAuditSink : IAccessAuditSink
{
    private readonly Lock gate = new();
    private readonly List<AccessAuditEvent> events = [];

    public InMemoryAccessAuditSink(int maximumEvents = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEvents, 1);
        MaximumEvents = maximumEvents;
    }

    public int MaximumEvents { get; }

    public Task RecordAsync(AccessAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (events.Count >= MaximumEvents)
                throw new InvalidOperationException("The in-memory audit sink is full.");
            events.Add(auditEvent);
        }
        return Task.CompletedTask;
    }

    /// <summary>Recorded events in recording order.</summary>
    public IReadOnlyList<AccessAuditEvent> Snapshot()
    {
        lock (gate)
            return [.. events];
    }
}

internal sealed class InMemoryScopedTable<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly Lock gate = new();
    private readonly Dictionary<TicketScope, Dictionary<TKey, TValue>> scopes = [];
    private readonly int maximumEntries;
    private readonly int maximumPerScope;
    private int count;

    public InMemoryScopedTable(int maximumEntries, int maximumPerScope)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        this.maximumEntries = maximumEntries;
        this.maximumPerScope = maximumPerScope;
    }

    public TValue? Get(TicketScope scope, TKey key)
    {
        lock (gate)
            return scopes.TryGetValue(scope, out var entries) ? entries.GetValueOrDefault(key) : null;
    }

    public List<TValue> List(TicketScope scope)
    {
        lock (gate)
            return scopes.TryGetValue(scope, out var entries) ? [.. entries.Values] : [];
    }

    public StoreWriteResult<TValue, AccessWriteRejection> Create(TicketScope scope, TKey key, Func<long, TValue> create, Func<TValue, long> revision)
    {
        lock (gate)
        {
            if (scopes.TryGetValue(scope, out var entries))
            {
                if (entries.TryGetValue(key, out var existing))
                    return new StoreConflict(revision(existing));
                if (count >= maximumEntries || entries.Count >= maximumPerScope)
                    return new StoreRejected<AccessWriteRejection>(AccessWriteRejection.CapacityExceeded);
            }
            else
            {
                if (count >= maximumEntries)
                    return new StoreRejected<AccessWriteRejection>(AccessWriteRejection.CapacityExceeded);
                scopes[scope] = entries = [];
            }
            var created = create(1);
            entries.Add(key, created);
            count++;
            return new StoreApplied<TValue>(created);
        }
    }

    public StoreWriteResult<TValue, AccessWriteRejection> Update(TicketScope scope, TKey key, long expectedRevision, Func<long, TValue> create, Func<TValue, long> revision)
    {
        lock (gate)
        {
            if (!scopes.TryGetValue(scope, out var entries) || !entries.TryGetValue(key, out var existing))
                return StoreNotFound.Instance;
            if (revision(existing) != expectedRevision)
                return new StoreConflict(revision(existing));
            var updated = create(checked(expectedRevision + 1));
            entries[key] = updated;
            return new StoreApplied<TValue>(updated);
        }
    }

    public StoreWriteResult<TValue, AccessWriteRejection> Delete(TicketScope scope, TKey key, long expectedRevision, Func<TValue, long> revision)
    {
        lock (gate)
        {
            if (!scopes.TryGetValue(scope, out var entries) || !entries.TryGetValue(key, out var existing))
                return StoreNotFound.Instance;
            if (revision(existing) != expectedRevision)
                return new StoreConflict(revision(existing));
            entries.Remove(key);
            count--;
            if (entries.Count == 0)
                scopes.Remove(scope);
            return new StoreApplied<TValue>(existing);
        }
    }
}
