using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>The write was applied; <see cref="Value"/> is the stored state (for deletes, the removed state).</summary>
public sealed record AccessWriteApplied<T>(T Value);

/// <summary>No record exists for the key in the requested scope or partition.</summary>
public sealed record AccessWriteNotFound
{
    private AccessWriteNotFound()
    {
    }

    public static AccessWriteNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match, or a create found an existing record. Nothing was written.</summary>
public sealed record AccessWriteConflict(long CurrentRevision);

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record AccessWriteRejected(AccessWriteRejection Reason);

public enum AccessWriteRejection
{
    /// <summary>Built-in roles cannot be created, replaced or deleted.</summary>
    BuiltInRoleImmutable = 1,

    /// <summary>A per-scope or per-subject capacity limit in <see cref="AccessLimits"/> would be exceeded.</summary>
    CapacityExceeded = 2,

    /// <summary>An update tried to change a record's identity, scope or partition.</summary>
    IdentityChanged = 3,
}

/// <summary>Outcome of a revision-fenced store write. Switch over every case without a discard arm.</summary>
public union AccessWriteResult<T>(AccessWriteApplied<T>, AccessWriteNotFound, AccessWriteConflict, AccessWriteRejected);

/// <summary>Capacity limits every access store enforces atomically with the write.</summary>
public static class AccessLimits
{
    public const int MaximumCustomRolesPerWorkspace = 128;
    public const int MaximumFieldPoliciesPerWorkspace = 256;
    public const int MaximumGrantsPerSubject = 256;

    /// <summary>Maximum grants returned by one <see cref="IAccessGrantStore.ListForTargetAsync"/> call.</summary>
    public const int MaximumTargetPage = 1000;

    /// <summary>A user plus <see cref="UserPrincipal.MaximumTeamMemberships"/> teams.</summary>
    public const int MaximumSubjectsPerQuery = UserPrincipal.MaximumTeamMemberships + 1;
}

/// <summary>
/// Workspace-scoped custom roles. Contract: every operation is confined to exactly one <see cref="TicketScope"/>; built-in role
/// identifiers are rejected with <see cref="AccessWriteRejection.BuiltInRoleImmutable"/>; creates start at revision 1; updates and
/// deletes apply atomically only when the current revision equals the expected revision; at most
/// <see cref="AccessLimits.MaximumCustomRolesPerWorkspace"/> roles per scope, checked atomically with the insert.
/// </summary>
public interface IAccessRoleStore
{
    Task<StoredAccessRole?> GetAsync(TicketScope scope, RoleId id, CancellationToken cancellationToken = default);

    /// <summary>Roles in <paramref name="scope"/> ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredAccessRole>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessRole>> CreateAsync(TicketScope scope, AccessRole role, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessRole>> UpdateAsync(TicketScope scope, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessRole>> DeleteAsync(TicketScope scope, RoleId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Partition-scoped grants. Contract: records never cross an <see cref="AccessPartition"/>; listing returns tenant grants plus grants
/// naming the requested workspace, for the requested subjects only; creates start at revision 1; updates (which cannot change the
/// grant's partition or identifier) and deletes are atomic compare-and-set on revision; at most
/// <see cref="AccessLimits.MaximumGrantsPerSubject"/> grants per subject per partition, checked atomically with the write.
/// Stores persist explicit versioned discriminators, never union JSON.
/// </summary>
public interface IAccessGrantStore
{
    Task<StoredAccessGrant?> GetAsync(AccessPartition partition, GrantId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants for any of <paramref name="subjects"/> (at most <see cref="AccessLimits.MaximumSubjectsPerQuery"/>) that are tenant-wide or
    /// name <paramref name="workspace"/>, ordered by grant identifier (ordinal). Validity windows are not filtered by the store.
    /// </summary>
    Task<IReadOnlyList<StoredAccessGrant>> ListForSubjectsAsync(AccessPartition partition, WorkspaceId workspace, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants (any subject) whose scope equals <paramref name="target"/> — a workspace, team or inbox scope, never tenant — ordered by
    /// grant identifier (ordinal), after <paramref name="after"/> when supplied, at most <paramref name="limit"/>
    /// (1-<see cref="AccessLimits.MaximumTargetPage"/>). Used to clean up grants when a team or inbox is deleted. Validity windows
    /// are not filtered by the store.
    /// </summary>
    Task<IReadOnlyList<StoredAccessGrant>> ListForTargetAsync(AccessPartition partition, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessGrant>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessGrant>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessGrant>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Workspace-scoped field policies keyed by field path. Same scope, revision and atomic-capacity contract as
/// <see cref="IAccessRoleStore"/>, with at most <see cref="AccessLimits.MaximumFieldPoliciesPerWorkspace"/> policies per scope.
/// </summary>
public interface IAccessFieldPolicyStore
{
    Task<StoredAccessFieldPolicy?> GetAsync(TicketScope scope, FieldPath field, CancellationToken cancellationToken = default);

    /// <summary>Policies in <paramref name="scope"/> ordered by field path (ordinal).</summary>
    Task<IReadOnlyList<StoredAccessFieldPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessFieldPolicy>> CreateAsync(TicketScope scope, AccessFieldPolicy policy, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessFieldPolicy>> UpdateAsync(TicketScope scope, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AccessWriteResult<StoredAccessFieldPolicy>> DeleteAsync(TicketScope scope, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default);
}
