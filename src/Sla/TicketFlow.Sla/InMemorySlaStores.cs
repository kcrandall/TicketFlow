using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>Development-only, process-local SLA policy store. Not durable; state is lost on restart.</summary>
public sealed class InMemorySlaPolicyStore : ISlaPolicyStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, string Id), StoredSlaPolicy> policies = [];

    public Task<StoredSlaPolicy?> GetAsync(TicketScope scope, SlaPolicyId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(policies.GetValueOrDefault((scope, id.Value)));
    }

    public Task<IReadOnlyList<StoredSlaPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredSlaPolicy>>([.. policies.Where(pair => pair.Key.Scope == scope).OrderBy(pair => pair.Key.Id, StringComparer.Ordinal).Select(pair => pair.Value)]);
    }

    public Task<SlaWriteResult<StoredSlaPolicy>> CreateAsync(TicketScope scope, SlaPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (policies.TryGetValue((scope, policy.Id.Value), out var existing))
                return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(new SlaWriteConflict(existing.Revision));
            if (policies.Keys.Count(key => key.Scope == scope) >= ISlaPolicyStore.MaximumPoliciesPerWorkspace)
                return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(new SlaWriteRejected(SlaWriteRejection.CapacityExceeded));
            var stored = new StoredSlaPolicy(scope, policy, 1);
            policies[(scope, policy.Id.Value)] = stored;
            return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(new SlaWriteApplied<StoredSlaPolicy>(stored));
        }
    }

    public Task<SlaWriteResult<StoredSlaPolicy>> UpdateAsync(TicketScope scope, SlaPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!policies.TryGetValue((scope, policy.Id.Value), out var existing))
                return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(SlaWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(new SlaWriteConflict(existing.Revision));
            var stored = new StoredSlaPolicy(scope, policy, existing.Revision + 1);
            policies[(scope, policy.Id.Value)] = stored;
            return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(new SlaWriteApplied<StoredSlaPolicy>(stored));
        }
    }

    public Task<SlaWriteResult<StoredSlaPolicy>> DeleteAsync(TicketScope scope, SlaPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!policies.TryGetValue((scope, id.Value), out var existing))
                return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(SlaWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(new SlaWriteConflict(existing.Revision));
            policies.Remove((scope, id.Value));
            return Task.FromResult<SlaWriteResult<StoredSlaPolicy>>(new SlaWriteApplied<StoredSlaPolicy>(existing));
        }
    }
}

/// <summary>
/// Development-only, process-local SLA tracker store with timers. Not durable; state is lost on restart. Lease times come from the
/// supplied <see cref="TimeProvider"/>. Lists scan the process-local set under a lock (bounded only by memory) and apply the Access
/// filter with <see cref="AccessFilterEvaluator"/>.
/// </summary>
public sealed class InMemorySlaTrackerStore(TimeProvider? time = null) : ISlaTrackerStore
{
    private readonly Lock gate = new();
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Dictionary<(TicketScope Scope, string Id), Entry> entries = [];

    public Task<StoredSlaTracker?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(entries.GetValueOrDefault((scope, conversation.Value))?.Stored);
    }

    public Task<SlaWriteResult<StoredSlaTracker>> WriteAsync(TicketScope scope, SlaTracker tracker, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(tracker);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = (scope, tracker.Conversation.Value);
            entries.TryGetValue(key, out var existing);
            if (expectedRevision is null && existing is not null)
                return Task.FromResult<SlaWriteResult<StoredSlaTracker>>(new SlaWriteConflict(existing.Stored.Revision));
            if (expectedRevision is { } expected)
            {
                if (existing is null)
                    return Task.FromResult<SlaWriteResult<StoredSlaTracker>>(SlaWriteNotFound.Instance);
                if (existing.Stored.Revision != expected)
                    return Task.FromResult<SlaWriteResult<StoredSlaTracker>>(new SlaWriteConflict(existing.Stored.Revision));
                if (existing.Stored.Tracker.Inbox != tracker.Inbox)
                    throw new ArgumentException("A tracker's inbox cannot change.", nameof(tracker));
            }
            var stored = new StoredSlaTracker(scope, tracker, (existing?.Stored.Revision ?? 0) + 1);
            entries[key] = new Entry(stored, tracker.Schedule.NextCheckAt, null, existing?.Fence ?? 0);
            return Task.FromResult<SlaWriteResult<StoredSlaTracker>>(new SlaWriteApplied<StoredSlaTracker>(stored));
        }
    }

    public Task<SlaWriteResult<StoredSlaTracker>> DeleteAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!entries.TryGetValue((scope, conversation.Value), out var existing))
                return Task.FromResult<SlaWriteResult<StoredSlaTracker>>(SlaWriteNotFound.Instance);
            if (existing.Stored.Revision != expectedRevision)
                return Task.FromResult<SlaWriteResult<StoredSlaTracker>>(new SlaWriteConflict(existing.Stored.Revision));
            entries.Remove((scope, conversation.Value));
            return Task.FromResult<SlaWriteResult<StoredSlaTracker>>(new SlaWriteApplied<StoredSlaTracker>(existing.Stored));
        }
    }

    public Task<IReadOnlyList<SlaTimerClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ISlaTrackerStore.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > ISlaTrackerStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most one hour.");
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        lock (gate)
        {
            var due = entries
                .Where(pair => pair.Value.DueAt <= now && (pair.Value.LeaseUntil is null || pair.Value.LeaseUntil <= now))
                .OrderBy(pair => pair.Value.DueAt).ThenBy(pair => pair.Key.Id, StringComparer.Ordinal)
                .Take(limit).ToList();
            var claims = new List<SlaTimerClaim>(due.Count);
            foreach (var (key, entry) in due)
            {
                var leased = entry with { LeaseUntil = now + lease, Fence = entry.Fence + 1 };
                entries[key] = leased;
                claims.Add(new SlaTimerClaim(key.Scope, entry.Stored.Conversation, entry.DueAt!.Value, leased.Fence, leased.LeaseUntil!.Value));
            }
            return Task.FromResult<IReadOnlyList<SlaTimerClaim>>(claims);
        }
    }

    public Task<bool> ReleaseAsync(SlaTimerClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        lock (gate)
        {
            var key = (claim.Scope, claim.Conversation.Value);
            if (!entries.TryGetValue(key, out var entry) || entry.Fence != claim.Fence || entry.LeaseUntil is not { } until || until <= now)
                return Task.FromResult(false);
            entries[key] = entry with { DueAt = SlaMath.Instant(retryAt), LeaseUntil = null };
            return Task.FromResult(true);
        }
    }

    public Task<SlaTrackerPage> ListAsync(TicketScope scope, SlaRiskQuery query, DateTimeOffset now, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var rows = entries
                .Where(pair => pair.Key.Scope == scope)
                .Select(pair => pair.Value.Stored)
                .Where(stored => query.Matches(stored.Tracker, now) && query.IsAfterCursor(stored.Tracker)
                    && AccessFilterEvaluator.Matches(filter, stored.Tracker.Resource(scope)))
                .OrderBy(stored => stored.Tracker.Schedule.UrgencyAt).ThenBy(stored => stored.Conversation.Value, StringComparer.Ordinal)
                .Take(query.Limit + 1).ToList();
            if (rows.Count <= query.Limit)
                return Task.FromResult(new SlaTrackerPage(rows, null));
            var page = rows[..query.Limit];
            return Task.FromResult(new SlaTrackerPage(page, new SlaRiskCursor(page[^1].Tracker.Schedule.UrgencyAt!.Value, page[^1].Conversation)));
        }
    }

    private sealed record Entry(StoredSlaTracker Stored, DateTimeOffset? DueAt, DateTimeOffset? LeaseUntil, long Fence);
}
