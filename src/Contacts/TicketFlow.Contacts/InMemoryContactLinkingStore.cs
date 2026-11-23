using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// Development-only <see cref="IContactLinkingStore"/>: process-local, single-lock, not durable. Suggestion listing evaluates the
/// access filter against the contacts of the supplied <see cref="IContactStore"/> (expected to complete synchronously, like
/// <see cref="InMemoryContactStore"/>).
/// </summary>
public sealed class InMemoryContactLinkingStore : IContactLinkingStore
{
    private readonly Lock gate = new();
    private readonly IContactStore contacts;
    private readonly Dictionary<TicketScope, StoredContactLinkingPolicy> policies = [];
    private readonly Dictionary<TicketScope, ScopeBucket> scopes = [];
    private int totalSuggestions;

    private sealed class ScopeBucket
    {
        public Dictionary<Guid, StoredContactLinkSuggestion> ById { get; } = [];
        public Dictionary<(ContactId Candidate, ContactId Account), Guid> PendingByPair { get; } = [];
    }

    public InMemoryContactLinkingStore(IContactStore contacts, int maximumSuggestions = 10_000, int maximumRowsExamined = ContactLinkSuggestionQuery.DefaultMaximumRowsExamined)
    {
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSuggestions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRowsExamined, 1);
        MaximumSuggestions = maximumSuggestions;
        MaximumRowsExamined = maximumRowsExamined;
    }

    public int MaximumSuggestions { get; }
    public int MaximumRowsExamined { get; }

    public Task<StoredContactLinkingPolicy?> GetPolicyAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(policies.GetValueOrDefault(scope));
    }

    public Task<StoreWriteResult<StoredContactLinkingPolicy, ContactWriteRejection>> PutPolicyAsync(TicketScope scope, ContactLinkingPolicy policy, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (policy.Value is null)
            throw new ArgumentException("A policy is required.", nameof(policy));
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        lock (gate)
        {
            var existing = policies.GetValueOrDefault(scope);
            StoreWriteResult<StoredContactLinkingPolicy, ContactWriteRejection> result;
            if (expectedRevision is null && existing is not null)
                result = new StoreConflict(existing.Revision);
            else if (expectedRevision is { } expected && existing is null)
                result = StoreNotFound.Instance;
            else if (expectedRevision is { } fenced && existing!.Revision != fenced)
                result = new StoreConflict(existing.Revision);
            else
            {
                var stored = new StoredContactLinkingPolicy(scope, policy, existing is null ? 1 : checked(existing.Revision + 1));
                policies[scope] = stored;
                result = new StoreApplied<StoredContactLinkingPolicy>(stored);
            }
            return Task.FromResult(result);
        }
    }

    public Task<StoredContactLinkSuggestion?> GetSuggestionAsync(TicketScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(scopes.TryGetValue(scope, out var bucket) ? bucket.ById.GetValueOrDefault(id) : null);
    }

    public Task<StoreWriteResult<StoredLinkSuggestionWrite, ContactWriteRejection>> CreateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.Status != ContactLinkSuggestionStatus.Pending)
            throw new ArgumentException("New suggestions are pending.", nameof(suggestion));
        lock (gate)
        {
            scopes.TryGetValue(scope, out var bucket);
            var pair = (suggestion.Candidate, suggestion.Account);
            StoreWriteResult<StoredLinkSuggestionWrite, ContactWriteRejection> result;
            if (bucket is not null && bucket.PendingByPair.TryGetValue(pair, out var pendingId))
                result = new StoreApplied<StoredLinkSuggestionWrite>(new(bucket.ById[pendingId], false));
            else if (bucket is not null && bucket.ById.TryGetValue(suggestion.Id, out var duplicate))
                result = new StoreConflict(duplicate.Revision);
            else if (totalSuggestions >= MaximumSuggestions || (bucket?.PendingByPair.Count ?? 0) >= IContactLinkingStore.MaximumPendingSuggestions)
                result = new StoreRejected<ContactWriteRejection>(ContactWriteRejection.CapacityExceeded);
            else
            {
                bucket ??= scopes[scope] = new ScopeBucket();
                var stored = new StoredContactLinkSuggestion(scope, suggestion, 1);
                bucket.ById[suggestion.Id] = stored;
                bucket.PendingByPair[pair] = suggestion.Id;
                totalSuggestions++;
                result = new StoreApplied<StoredLinkSuggestionWrite>(new(stored, true));
            }
            return Task.FromResult(result);
        }
    }

    public Task<StoreWriteResult<StoredContactLinkSuggestion, ContactWriteRejection>> UpdateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            StoreWriteResult<StoredContactLinkSuggestion, ContactWriteRejection> result;
            if (!scopes.TryGetValue(scope, out var bucket) || !bucket.ById.TryGetValue(suggestion.Id, out var existing))
                result = StoreNotFound.Instance;
            else if (existing.Revision != expectedRevision)
                result = new StoreConflict(existing.Revision);
            else if (!SameIdentity(existing.Suggestion, suggestion))
                throw new ArgumentException("A suggestion's pair, evidence and creation fields are immutable.", nameof(suggestion));
            else if (suggestion.Status == ContactLinkSuggestionStatus.Pending && existing.Suggestion.Status != ContactLinkSuggestionStatus.Pending)
                throw new ArgumentException("A decided suggestion cannot become pending again.", nameof(suggestion));
            else
            {
                var stored = new StoredContactLinkSuggestion(scope, suggestion, checked(existing.Revision + 1));
                bucket.ById[suggestion.Id] = stored;
                if (existing.Suggestion.Status == ContactLinkSuggestionStatus.Pending && suggestion.Status != ContactLinkSuggestionStatus.Pending)
                    bucket.PendingByPair.Remove((suggestion.Candidate, suggestion.Account));
                result = new StoreApplied<StoredContactLinkSuggestion>(stored);
            }
            return Task.FromResult(result);
        }
    }

    public async Task<ContactLinkSuggestionStorePage> ListSuggestionsAsync(TicketScope scope, ContactLinkSuggestionQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        List<StoredContactLinkSuggestion> examined;
        lock (gate)
        {
            if (!scopes.TryGetValue(scope, out var bucket) || bucket.ById.Count == 0)
                return new ContactLinkSuggestionStorePage([], null);
            var afterText = query.After?.ToString("D");
            examined = [];
            foreach (var stored in bucket.ById.Values)
            {
                if (stored.Suggestion.Status != query.Status)
                    continue;
                if (query.Contact is { } contact && stored.Suggestion.Candidate != contact && stored.Suggestion.Account != contact)
                    continue;
                if (afterText is not null && string.CompareOrdinal(stored.Suggestion.Id.ToString("D"), afterText) <= 0)
                    continue;
                examined.Add(stored);
            }
            examined.Sort(static (a, b) => string.CompareOrdinal(a.Suggestion.Id.ToString("D"), b.Suggestion.Id.ToString("D")));
            if (examined.Count > MaximumRowsExamined)
                examined.RemoveRange(MaximumRowsExamined, examined.Count - MaximumRowsExamined);
        }
        if (examined.Count == 0)
            return new ContactLinkSuggestionStorePage([], null);
        var ids = new HashSet<ContactId>(examined.Count * 2);
        foreach (var stored in examined)
        {
            ids.Add(stored.Suggestion.Candidate);
            ids.Add(stored.Suggestion.Account);
        }
        var loaded = await contacts.GetManyAsync(scope, ids, cancellationToken).ConfigureAwait(false);
        var page = new List<StoredContactLinkSuggestion>(Math.Min(query.Limit, examined.Count));
        foreach (var stored in examined)
        {
            if (!loaded.TryGetValue(stored.Suggestion.Candidate, out var candidate)
                || !loaded.TryGetValue(stored.Suggestion.Account, out var account)
                || !AccessFilterEvaluator.Matches(filter, ContactAccess.Resource(candidate))
                || !AccessFilterEvaluator.Matches(filter, ContactAccess.Resource(account)))
                continue;
            page.Add(stored);
            if (page.Count == query.Limit)
                break;
        }
        Guid? next = page.Count == query.Limit ? page[^1].Suggestion.Id : examined.Count == MaximumRowsExamined ? examined[^1].Suggestion.Id : null;
        return new ContactLinkSuggestionStorePage(page, next);
    }

    public Task<int> PruneDecidedSuggestionsAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IContactLinkingStore.MaximumPruneBatch);
        lock (gate)
        {
            var expired = new List<(ScopeBucket Bucket, Guid Id, DateTimeOffset UpdatedAt)>();
            foreach (var bucket in scopes.Values)
            {
                foreach (var pair in bucket.ById)
                    if (pair.Value.Suggestion.Status != ContactLinkSuggestionStatus.Pending && pair.Value.Suggestion.UpdatedAt < decidedBefore)
                        expired.Add((bucket, pair.Key, pair.Value.Suggestion.UpdatedAt));
            }
            if (expired.Count == 0)
                return Task.FromResult(0);
            expired.Sort(static (a, b) => a.UpdatedAt.CompareTo(b.UpdatedAt));
            var count = Math.Min(limit, expired.Count);
            for (var i = 0; i < count; i++)
            {
                if (expired[i].Bucket.ById.Remove(expired[i].Id))
                    totalSuggestions--;
            }
            return Task.FromResult(count);
        }
    }

    private static bool SameIdentity(ContactLinkSuggestion left, ContactLinkSuggestion right) =>
        left.Candidate == right.Candidate && left.Account == right.Account && left.Evidence == right.Evidence && left.EvidenceVerified == right.EvidenceVerified
        && left.CreatedAt == right.CreatedAt && left.CreatedBy == right.CreatedBy;
}
