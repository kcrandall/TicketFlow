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
    private readonly Dictionary<(TicketScope Scope, Guid Id), StoredContactLinkSuggestion> suggestions = [];

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

    public Task<ContactLinkingWriteResult<StoredContactLinkingPolicy>> PutPolicyAsync(TicketScope scope, ContactLinkingPolicy policy, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (policy.Value is null)
            throw new ArgumentException("A policy is required.", nameof(policy));
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        lock (gate)
        {
            var existing = policies.GetValueOrDefault(scope);
            ContactLinkingWriteResult<StoredContactLinkingPolicy> result;
            if (expectedRevision is null && existing is not null)
                result = new LinkingWriteConflict(existing.Revision);
            else if (expectedRevision is { } expected && existing is null)
                result = LinkingWriteNotFound.Instance;
            else if (expectedRevision is { } fenced && existing!.Revision != fenced)
                result = new LinkingWriteConflict(existing.Revision);
            else
            {
                var stored = new StoredContactLinkingPolicy(scope, policy, existing is null ? 1 : checked(existing.Revision + 1));
                policies[scope] = stored;
                result = new LinkingWriteApplied<StoredContactLinkingPolicy>(stored, existing is null);
            }
            return Task.FromResult(result);
        }
    }

    public Task<StoredContactLinkSuggestion?> GetSuggestionAsync(TicketScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(suggestions.GetValueOrDefault((scope, id)));
    }

    public Task<ContactLinkingWriteResult<StoredContactLinkSuggestion>> CreateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.Status != ContactLinkSuggestionStatus.Pending)
            throw new ArgumentException("New suggestions are pending.", nameof(suggestion));
        lock (gate)
        {
            ContactLinkingWriteResult<StoredContactLinkSuggestion> result;
            if (suggestions.Values.FirstOrDefault(stored => stored.Scope == scope && stored.Suggestion.Status == ContactLinkSuggestionStatus.Pending
                && stored.Suggestion.Candidate == suggestion.Candidate && stored.Suggestion.Account == suggestion.Account) is { } pending)
                result = new LinkingWriteApplied<StoredContactLinkSuggestion>(pending, false);
            else if (suggestions.ContainsKey((scope, suggestion.Id)))
                result = new LinkingWriteConflict(suggestions[(scope, suggestion.Id)].Revision);
            else if (suggestions.Count >= MaximumSuggestions || suggestions.Values.Count(stored => stored.Scope == scope
                && stored.Suggestion.Status == ContactLinkSuggestionStatus.Pending) >= IContactLinkingStore.MaximumPendingSuggestions)
                result = new ContactWriteRejected(ContactWriteRejection.CapacityExceeded);
            else
            {
                var stored = new StoredContactLinkSuggestion(scope, suggestion, 1);
                suggestions[(scope, suggestion.Id)] = stored;
                result = new LinkingWriteApplied<StoredContactLinkSuggestion>(stored);
            }
            return Task.FromResult(result);
        }
    }

    public Task<ContactLinkingWriteResult<StoredContactLinkSuggestion>> UpdateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            ContactLinkingWriteResult<StoredContactLinkSuggestion> result;
            if (!suggestions.TryGetValue((scope, suggestion.Id), out var existing))
                result = LinkingWriteNotFound.Instance;
            else if (existing.Revision != expectedRevision)
                result = new LinkingWriteConflict(existing.Revision);
            else if (!SameIdentity(existing.Suggestion, suggestion))
                throw new ArgumentException("A suggestion's pair, evidence and creation fields are immutable.", nameof(suggestion));
            else if (suggestion.Status == ContactLinkSuggestionStatus.Pending && existing.Suggestion.Status != ContactLinkSuggestionStatus.Pending)
                throw new ArgumentException("A decided suggestion cannot become pending again.", nameof(suggestion));
            else
            {
                var stored = new StoredContactLinkSuggestion(scope, suggestion, checked(existing.Revision + 1));
                suggestions[(scope, suggestion.Id)] = stored;
                result = new LinkingWriteApplied<StoredContactLinkSuggestion>(stored, false);
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
            examined = [.. suggestions.Values
                .Where(stored => stored.Scope == scope && stored.Suggestion.Status == query.Status
                    && (query.Contact is not { } contact || stored.Suggestion.Candidate == contact || stored.Suggestion.Account == contact)
                    && (query.After is not { } after || string.CompareOrdinal(stored.Suggestion.Id.ToString("D"), after.ToString("D")) > 0))
                .OrderBy(stored => stored.Suggestion.Id.ToString("D"), StringComparer.Ordinal)
                .Take(MaximumRowsExamined)];
        }
        var page = new List<StoredContactLinkSuggestion>();
        foreach (var stored in examined)
        {
            var candidate = await contacts.GetAsync(scope, stored.Suggestion.Candidate, cancellationToken).ConfigureAwait(false);
            var account = await contacts.GetAsync(scope, stored.Suggestion.Account, cancellationToken).ConfigureAwait(false);
            if (candidate is null || account is null || !AccessFilterEvaluator.Matches(filter, ContactAccess.Resource(candidate))
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
            var expired = suggestions.Where(pair => pair.Value.Suggestion.Status != ContactLinkSuggestionStatus.Pending && pair.Value.Suggestion.UpdatedAt < decidedBefore)
                .OrderBy(pair => pair.Value.Suggestion.UpdatedAt).Take(limit).Select(pair => pair.Key).ToList();
            foreach (var key in expired)
                suggestions.Remove(key);
            return Task.FromResult(expired.Count);
        }
    }

    private static bool SameIdentity(ContactLinkSuggestion left, ContactLinkSuggestion right) =>
        left.Candidate == right.Candidate && left.Account == right.Account && left.Evidence == right.Evidence && left.EvidenceVerified == right.EvidenceVerified
        && left.CreatedAt == right.CreatedAt && left.CreatedBy == right.CreatedBy;
}
