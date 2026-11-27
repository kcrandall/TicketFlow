using TicketFlow.Core;

namespace TicketFlow.Access.Tests;

/// <summary>An audit sink that fails on demand.</summary>
internal sealed class SwitchableAuditSink : IAccessAuditSink
{
    public bool Fail { get; set; }

    public List<AccessAuditEvent> Events { get; } = [];

    public Task RecordAsync(AccessAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        if (Fail)
            throw new InvalidOperationException("Audit storage is unavailable.");
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>A grant store that returns a fixed list regardless of the query, to prove the engine rejects contract violations.</summary>
internal sealed class FixedGrantStore(params AccessGrant[] grants) : IAccessGrantStore
{
    public Task<StoredAccessGrant?> GetAsync(AccessPartition partition, GrantId id, CancellationToken cancellationToken = default) =>
        Task.FromResult<StoredAccessGrant?>(null);

    public Task<IReadOnlyList<StoredAccessGrant>> ListForSubjectsAsync(AccessPartition partition, WorkspaceId workspace, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredAccessGrant>>([.. grants.Select(grant => new StoredAccessGrant(grant, 1))]);

    public Task<IReadOnlyList<StoredAccessGrant>> ListAllForSubjectsAsync(AccessPartition partition, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredAccessGrant>>([.. grants.Select(grant => new StoredAccessGrant(grant, 1))]);

    public Task<IReadOnlyList<StoredAccessGrant>> ListForTargetAsync(AccessPartition partition, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoredAccessGrant>>([.. grants.Where(grant => grant.Scope == target).Select(grant => new StoredAccessGrant(grant, 1))]);

    public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> HasLiveDenyForRoleAsync(AccessPartition partition, WorkspaceId workspace, RoleId role, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult(grants.Any(grant => grant.Partition == partition && grant.Workspace == workspace && grant.Effect == GrantEffect.Deny && grant.Role == role && (grant.ValidUntil is not { } until || until > now)));
}
