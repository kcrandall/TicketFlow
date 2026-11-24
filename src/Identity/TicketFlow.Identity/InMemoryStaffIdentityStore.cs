using TicketFlow.Core;

namespace TicketFlow.Identity;

/// <summary>Development-only <see cref="IStaffIdentityStore"/>: process-local, bounded, single-lock, not durable.</summary>
public sealed class InMemoryStaffIdentityStore : IStaffIdentityStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, string Provider, string Subject), StoredStaffIdentityLink> links = [];
    private readonly Dictionary<(TicketScope Scope, UserId User), StoredStaffProfile> profiles = [];
    private readonly List<StaffIdentityAuditEvent> audit = [];

    public InMemoryStaffIdentityStore(int maximumEntries = 10_000, int maximumAuditEvents = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAuditEvents, 1);
        MaximumEntries = maximumEntries;
        MaximumAuditEvents = maximumAuditEvents;
    }

    public int MaximumEntries { get; }
    public int MaximumAuditEvents { get; }

    /// <summary>A snapshot of recorded audit events in recording order.</summary>
    public IReadOnlyList<StaffIdentityAuditEvent> AuditEvents
    {
        get
        {
            lock (gate)
                return [.. audit];
        }
    }

    public Task<StoredStaffIdentityLink?> FindAsync(TicketScope scope, StaffIdentityRef identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(identity);
        lock (gate)
            return Task.FromResult(links.GetValueOrDefault(scope, identity.Provider, identity.Subject));
    }

    public Task<IReadOnlyList<StoredStaffIdentityLink>> ListForUserAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredStaffIdentityLink>>([.. links.Values.Where(stored => stored.Scope == scope && stored.Link.User == user)
                .OrderBy(stored => stored.Link.Identity.Provider, StringComparer.Ordinal).ThenBy(stored => stored.Link.Identity.Subject, StringComparer.Ordinal)]);
    }

    public Task<StaffIdentityWriteResult> LinkAsync(TicketScope scope, StaffIdentityLink link, StaffIdentityAuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(audit);
        lock (gate)
        {
            StaffIdentityWriteResult result;
            var key = (scope, link.Identity.Provider, link.Identity.Subject);
            if (links.TryGetValue(key, out var existing))
                result = existing.Link.User == link.User ? new StaffIdentityWriteApplied(existing, false) : new StaffIdentityWriteTaken(existing.Link.User);
            else if (links.Count >= MaximumEntries || this.audit.Count >= MaximumAuditEvents
                || links.Values.Count(stored => stored.Scope == scope && stored.Link.User == link.User) >= IStaffIdentityStore.MaximumIdentitiesPerUser)
                result = StaffIdentityWriteRejected.CapacityExceeded;
            else
            {
                var stored = new StoredStaffIdentityLink(scope, link);
                links[key] = stored;
                this.audit.Add(audit);
                result = new StaffIdentityWriteApplied(stored);
            }
            return Task.FromResult(result);
        }
    }

    public Task<StaffIdentityWriteResult> UnlinkAsync(TicketScope scope, StaffIdentityRef identity, UserId expectedUser, StaffIdentityAuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(audit);
        lock (gate)
        {
            StaffIdentityWriteResult result;
            var key = (scope, identity.Provider, identity.Subject);
            if (!links.TryGetValue(key, out var existing))
                result = StaffIdentityWriteNotFound.Instance;
            else if (existing.Link.User != expectedUser)
                result = new StaffIdentityWriteTaken(existing.Link.User);
            else if (this.audit.Count >= MaximumAuditEvents)
                result = StaffIdentityWriteRejected.CapacityExceeded;
            else
            {
                links.Remove(key);
                this.audit.Add(audit);
                result = new StaffIdentityWriteApplied(existing);
            }
            return Task.FromResult(result);
        }
    }

    public Task<StoredStaffProfile?> GetProfileAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(profiles.GetValueOrDefault(scope, user));
    }

    public Task<StaffIdentityProfileWriteResult> PutProfileAsync(TicketScope scope, StaffProfile profile, DateTimeOffset updatedAt, long? expectedRevision, StaffIdentityAuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(audit);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        lock (gate)
        {
            StaffIdentityProfileWriteResult result;
            var existing = profiles.GetValueOrDefault(scope, profile.User);
            if (expectedRevision is null && existing is not null)
                result = new StaffIdentityWriteConflict(existing.Revision);
            else if (expectedRevision is not null && existing is null)
                result = StaffIdentityWriteNotFound.Instance;
            else if (expectedRevision is { } expected && existing!.Revision != expected)
                result = new StaffIdentityWriteConflict(existing.Revision);
            else
            {
                var at = new DateTimeOffset(updatedAt.UtcTicks - updatedAt.UtcTicks % 10, TimeSpan.Zero);
                var stored = new StoredStaffProfile(scope, profile, at, existing is null ? 1 : checked(existing.Revision + 1));
                profiles[scope, profile.User] = stored;
                this.audit.Add(audit);
                result = new StaffIdentityProfileWriteApplied(stored);
            }
            return Task.FromResult(result);
        }
    }
}
