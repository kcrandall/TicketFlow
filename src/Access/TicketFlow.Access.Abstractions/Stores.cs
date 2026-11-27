using TicketFlow.Core;

namespace TicketFlow.Access;

public enum AccessWriteRejection
{
    /// <summary>Built-in roles cannot be created, replaced or deleted.</summary>
    BuiltInRoleImmutable = 1,

    /// <summary>A per-scope or per-subject capacity limit in <see cref="AccessLimits"/> would be exceeded.</summary>
    CapacityExceeded = 2,

    /// <summary>An update tried to change a record's identity, scope or partition.</summary>
    IdentityChanged = 3,
}

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

    Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> CreateAsync(TicketScope scope, AccessRole role, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> UpdateAsync(TicketScope scope, AccessRole role, long expectedRevision, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredAccessRole, AccessWriteRejection>> DeleteAsync(TicketScope scope, RoleId id, long expectedRevision, CancellationToken cancellationToken = default);
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
    /// Grants for any of <paramref name="subjects"/> (at most <see cref="AccessLimits.MaximumSubjectsPerQuery"/>) across every scope in
    /// <paramref name="partition"/> (tenant-wide and every workspace, team and inbox), ordered by grant identifier (ordinal).
    /// Validity windows are not filtered by the store.
    /// </summary>
    Task<IReadOnlyList<StoredAccessGrant>> ListAllForSubjectsAsync(AccessPartition partition, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants (any subject) whose scope equals <paramref name="target"/> — a workspace, team or inbox scope, never tenant — ordered by
    /// grant identifier (ordinal), after <paramref name="after"/> when supplied, at most <paramref name="limit"/>
    /// (1-<see cref="AccessLimits.MaximumTargetPage"/>). Used to clean up grants when a team or inbox is deleted. Validity windows
    /// are not filtered by the store.
    /// </summary>
    Task<IReadOnlyList<StoredAccessGrant>> ListForTargetAsync(AccessPartition partition, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants for <paramref name="subject"/> across every scope in <paramref name="partition"/>, ordered by grant identifier (ordinal).
    /// Default implementation falls back to <see cref="ListForSubjectsAsync"/> for <paramref name="fallbackWorkspace"/>.
    /// </summary>
    Task<IReadOnlyList<StoredAccessGrant>> ListAllForSubjectAsync(AccessPartition partition, GrantSubject subject, WorkspaceId fallbackWorkspace, CancellationToken cancellationToken = default) =>
        ListForSubjectsAsync(partition, fallbackWorkspace, [subject], cancellationToken);

    /// <summary>Deletes multiple revision-fenced grants in one batch, returning those that were removed.</summary>
    async Task<IReadOnlyList<StoredAccessGrant>> DeleteManyAsync(AccessPartition partition, IReadOnlyList<(GrantId Id, long ExpectedRevision)> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(targets);
        var removed = new List<StoredAccessGrant>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
            if (await DeleteAsync(partition, targets[i].Id, targets[i].ExpectedRevision, cancellationToken).ConfigureAwait(false) is StoreApplied<StoredAccessGrant> applied)
                removed.Add(applied.Value);
        return removed;
    }

    /// <summary>
    /// Returns <see langword="true"/> when at least one live <see cref="GrantEffect.Deny"/> grant
    /// (<c>ValidUntil is null || ValidUntil &gt; now</c>) in <paramref name="partition"/> and <paramref name="workspace"/>
    /// (across workspace, team and inbox scopes) references <paramref name="role"/>.
    /// </summary>
    Task<bool> HasLiveDenyForRoleAsync(AccessPartition partition, WorkspaceId workspace, RoleId role, DateTimeOffset now, CancellationToken cancellationToken = default);
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

    Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> CreateAsync(TicketScope scope, AccessFieldPolicy policy, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> UpdateAsync(TicketScope scope, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> DeleteAsync(TicketScope scope, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default);
}
