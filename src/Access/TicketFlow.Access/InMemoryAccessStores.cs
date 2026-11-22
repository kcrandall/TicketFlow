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

    public Task<AccessWriteResult<StoredAccessRole>> CreateAsync(TicketScope scope, AccessRole role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(role);
        if (role.IsBuiltIn || BuiltInRoles.IsBuiltIn(role.Id))
            return Task.FromResult<AccessWriteResult<StoredAccessRole>>(new AccessWriteRejected(AccessWriteRejection.BuiltInRoleImmutable));
        return Task.FromResult(table.Create(scope, role.Id, revision => new StoredAccessRole(scope, role, revision), static entry => entry.Revision));
    }

    public Task<AccessWriteResult<StoredAccessRole>> UpdateAsync(TicketScope scope, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (role.IsBuiltIn || BuiltInRoles.IsBuiltIn(role.Id))
            return Task.FromResult<AccessWriteResult<StoredAccessRole>>(new AccessWriteRejected(AccessWriteRejection.BuiltInRoleImmutable));
        return Task.FromResult(table.Update(scope, role.Id, expectedRevision, revision => new StoredAccessRole(scope, role, revision), static entry => entry.Revision));
    }

    public Task<AccessWriteResult<StoredAccessRole>> DeleteAsync(TicketScope scope, RoleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (BuiltInRoles.IsBuiltIn(id))
            return Task.FromResult<AccessWriteResult<StoredAccessRole>>(new AccessWriteRejected(AccessWriteRejection.BuiltInRoleImmutable));
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

    public Task<AccessWriteResult<StoredAccessFieldPolicy>> CreateAsync(TicketScope scope, AccessFieldPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        return Task.FromResult(table.Create(scope, policy.Field, revision => new StoredAccessFieldPolicy(scope, policy, revision), static entry => entry.Revision));
    }

    public Task<AccessWriteResult<StoredAccessFieldPolicy>> UpdateAsync(TicketScope scope, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Update(scope, policy.Field, expectedRevision, revision => new StoredAccessFieldPolicy(scope, policy, revision), static entry => entry.Revision));
    }

    public Task<AccessWriteResult<StoredAccessFieldPolicy>> DeleteAsync(TicketScope scope, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default)
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
        var wanted = subjects.ToHashSet();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<StoredAccessGrant>>([.. grants.Values
                .Where(entry => entry.Grant.Partition == partition && wanted.Contains(entry.Grant.Subject)
                    && (entry.Grant.Workspace is not { } named || named == workspace))
                .OrderBy(entry => entry.Grant.Id.Value, StringComparer.Ordinal)]);
        }
    }

    public Task<AccessWriteResult<StoredAccessGrant>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (gate)
        {
            if (grants.TryGetValue((grant.Partition, grant.Id), out var existing))
                return Result(new AccessWriteConflict(existing.Revision));
            if (grants.Count >= MaximumEntries || CountForSubject(grant.Partition, grant.Subject, except: null) >= AccessLimits.MaximumGrantsPerSubject)
                return Result(new AccessWriteRejected(AccessWriteRejection.CapacityExceeded));
            var stored = new StoredAccessGrant(grant, 1);
            grants.Add((grant.Partition, grant.Id), stored);
            return Result(new AccessWriteApplied<StoredAccessGrant>(stored));
        }
    }

    public Task<AccessWriteResult<StoredAccessGrant>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!grants.TryGetValue((grant.Partition, grant.Id), out var existing))
                return Result(AccessWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new AccessWriteConflict(existing.Revision));
            if (!existing.Grant.Subject.Equals(grant.Subject) && CountForSubject(grant.Partition, grant.Subject, grant.Id) >= AccessLimits.MaximumGrantsPerSubject)
                return Result(new AccessWriteRejected(AccessWriteRejection.CapacityExceeded));
            var stored = new StoredAccessGrant(grant, checked(existing.Revision + 1));
            grants[(grant.Partition, grant.Id)] = stored;
            return Result(new AccessWriteApplied<StoredAccessGrant>(stored));
        }
    }

    public Task<AccessWriteResult<StoredAccessGrant>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!grants.TryGetValue((partition, id), out var existing))
                return Result(AccessWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new AccessWriteConflict(existing.Revision));
            grants.Remove((partition, id));
            return Result(new AccessWriteApplied<StoredAccessGrant>(existing));
        }
    }

    private int CountForSubject(AccessPartition partition, GrantSubject subject, GrantId? except) =>
        grants.Values.Count(entry => entry.Grant.Partition == partition && entry.Grant.Subject.Equals(subject) && entry.Grant.Id != except);

    private static Task<AccessWriteResult<StoredAccessGrant>> Result(AccessWriteResult<StoredAccessGrant> result) => Task.FromResult(result);
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
    private readonly Dictionary<(TicketScope Scope, TKey Key), TValue> entries = [];
    private readonly int maximumEntries;
    private readonly int maximumPerScope;

    public InMemoryScopedTable(int maximumEntries, int maximumPerScope)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        this.maximumEntries = maximumEntries;
        this.maximumPerScope = maximumPerScope;
    }

    public TValue? Get(TicketScope scope, TKey key)
    {
        lock (gate)
            return entries.GetValueOrDefault((scope, key));
    }

    public List<TValue> List(TicketScope scope)
    {
        lock (gate)
            return [.. entries.Where(pair => pair.Key.Scope == scope).Select(pair => pair.Value)];
    }

    public AccessWriteResult<TValue> Create(TicketScope scope, TKey key, Func<long, TValue> create, Func<TValue, long> revision)
    {
        lock (gate)
        {
            if (entries.TryGetValue((scope, key), out var existing))
                return new AccessWriteConflict(revision(existing));
            if (entries.Count >= maximumEntries || entries.Keys.Count(entry => entry.Scope == scope) >= maximumPerScope)
                return new AccessWriteRejected(AccessWriteRejection.CapacityExceeded);
            var created = create(1);
            entries.Add((scope, key), created);
            return new AccessWriteApplied<TValue>(created);
        }
    }

    public AccessWriteResult<TValue> Update(TicketScope scope, TKey key, long expectedRevision, Func<long, TValue> create, Func<TValue, long> revision)
    {
        lock (gate)
        {
            if (!entries.TryGetValue((scope, key), out var existing))
                return AccessWriteNotFound.Instance;
            if (revision(existing) != expectedRevision)
                return new AccessWriteConflict(revision(existing));
            var updated = create(checked(expectedRevision + 1));
            entries[(scope, key)] = updated;
            return new AccessWriteApplied<TValue>(updated);
        }
    }

    public AccessWriteResult<TValue> Delete(TicketScope scope, TKey key, long expectedRevision, Func<TValue, long> revision)
    {
        lock (gate)
        {
            if (!entries.TryGetValue((scope, key), out var existing))
                return AccessWriteNotFound.Instance;
            if (revision(existing) != expectedRevision)
                return new AccessWriteConflict(revision(existing));
            entries.Remove((scope, key));
            return new AccessWriteApplied<TValue>(existing);
        }
    }
}
