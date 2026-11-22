using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// Development-only contact store and audit sink. State is process-local, lost on restart and never shared between processes; it is
/// an example of the <see cref="IContactStore"/> contract, not durable storage. Every operation is serialized under one lock and
/// search scans the scope, so it is intended for small development data sets.
/// </summary>
public sealed class InMemoryContactStore : IContactStore, IContactAuditSink
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, ContactId Id), StoredContact> contacts = [];
    private readonly Dictionary<(TicketScope Scope, Guid Id), ContactMergeRecord> merges = [];
    private readonly Dictionary<TicketScope, List<ContactChange>> changes = [];
    private readonly List<ContactAuditEvent> audit = [];
    private int changeCount;

    public InMemoryContactStore(int maximumContacts = 10_000, int maximumChanges = 100_000, int maximumAuditEvents = 10_000,
        int maximumRowsExamined = ContactSearch.DefaultMaximumRowsExamined)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRowsExamined, 1);
        MaximumRowsExamined = maximumRowsExamined;
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumContacts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChanges, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAuditEvents, 1);
        MaximumContacts = maximumContacts;
        MaximumChanges = maximumChanges;
        MaximumAuditEvents = maximumAuditEvents;
    }

    public int MaximumContacts { get; }
    public int MaximumChanges { get; }
    public int MaximumAuditEvents { get; }

    /// <summary>Rows examined per search page before the access filter is applied.</summary>
    public int MaximumRowsExamined { get; }

    /// <summary>A snapshot of recorded audit events in recording order.</summary>
    public IReadOnlyList<ContactAuditEvent> AuditEvents
    {
        get
        {
            lock (gate)
                return [.. audit];
        }
    }

    public Task<StoredContact?> GetAsync(TicketScope scope, ContactId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        lock (gate)
            return Task.FromResult(contacts.GetValueOrDefault((scope, id)));
    }

    public Task<IReadOnlyList<ContactIdentityOwner>> FindByIdentityAsync(TicketScope scope, ContactIdentityAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(address);
        lock (gate)
        {
            IReadOnlyList<ContactIdentityOwner> owners = [.. contacts.Values
                .Where(stored => stored.Scope == scope)
                .SelectMany(stored => stored.Contact.Identities.Where(identity => identity.Address == address)
                    .Select(identity => new ContactIdentityOwner(stored.Id, identity.Verification)))
                .OrderBy(owner => owner.Verification.IsVerified ? 0 : 1)
                .ThenBy(owner => owner.Contact.Value, StringComparer.Ordinal)
                .Take(IContactStore.MaximumIdentityOwners)];
            return Task.FromResult(owners);
        }
    }

    public Task<ContactPage> SearchAsync(TicketScope scope, ContactSearch search, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            var live = contacts.Values.Where(stored => stored.Scope == scope && stored.Contact.MergedInto is null);
            var rows = search.Mode switch
            {
                ContactSearchMode.Name => live.Select(stored => (Key: ContactSearch.FoldName(stored.Contact.Profile.DisplayName), Stored: stored))
                    .Where(row => row.Key.StartsWith(search.Prefix, StringComparison.Ordinal)),
                ContactSearchMode.Identity => live.SelectMany(stored => stored.Contact.Identities
                    .Where(identity => identity.Address.Kind == search.Kind && (search.Namespace is null || identity.Address.Namespace == search.Namespace)
                        && identity.Address.Value.StartsWith(search.Prefix, StringComparison.Ordinal))
                    .Select(identity => (Key: identity.Address.Value, Stored: stored))),
                _ => throw new ArgumentOutOfRangeException(nameof(search)),
            };
            if (search.After is { } after)
                rows = rows.Where(row => Compare(row.Key, row.Stored.Id, after.SortKey, after.Contact) > 0);
            var examined = rows
                .OrderBy(row => row.Key, StringComparer.Ordinal)
                .ThenBy(row => row.Stored.Id.Value, StringComparer.Ordinal)
                .Take(MaximumRowsExamined)
                .ToList();
            var page = new List<(string Key, StoredContact Stored)>();
            foreach (var row in examined)
            {
                if (!AccessFilterEvaluator.Matches(filter, ContactAccess.Resource(row.Stored)))
                    continue;
                page.Add(row);
                if (page.Count == search.Limit)
                    break;
            }
            var seen = new HashSet<ContactId>();
            var result = page.Where(row => seen.Add(row.Stored.Id)).Select(row => row.Stored).ToList();
            ContactSearchCursor? next = page.Count == search.Limit
                ? new ContactSearchCursor(page[^1].Key, page[^1].Stored.Id)
                : examined.Count == MaximumRowsExamined ? new ContactSearchCursor(examined[^1].Key, examined[^1].Stored.Id) : null;
            return Task.FromResult(new ContactPage(result, next));
        }
    }

    public Task<IReadOnlyList<ContactId>> ListMergedIntoAsync(TicketScope scope, ContactId target, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ContactDelete.MaximumCascade);
        lock (gate)
            return Task.FromResult<IReadOnlyList<ContactId>>([.. contacts.Values.Where(stored => stored.Scope == scope && stored.Contact.MergedInto == target)
                .Select(stored => stored.Id).OrderBy(id => id.Value, StringComparer.Ordinal).Take(limit)]);
    }

    public Task<ContactWriteResult> WriteAsync(TicketScope scope, ContactWriteBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(batch);
        lock (gate)
            return Task.FromResult(Write(scope, batch));
    }

    public Task<ContactMergeRecord?> GetMergeAsync(TicketScope scope, Guid mergeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(merges.GetValueOrDefault((scope, mergeId)));
    }

    public Task<IReadOnlyList<ContactChange>> ReadChangesAsync(TicketScope scope, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IContactStore.MaximumChangesPerRead);
        lock (gate)
        {
            IReadOnlyList<ContactChange> result = changes.TryGetValue(scope, out var log)
                ? [.. log.Where(change => change.Sequence > afterSequence).Take(limit)]
                : [];
            return Task.FromResult(result);
        }
    }

    public Task RecordAsync(ContactAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        lock (gate)
        {
            if (audit.Count >= MaximumAuditEvents)
                throw new InvalidOperationException("The in-memory contact audit sink is full.");
            audit.Add(auditEvent);
        }
        return Task.CompletedTask;
    }

    private ContactWriteResult Write(TicketScope scope, ContactWriteBatch batch)
    {
        foreach (var put in batch.Puts)
        {
            var existing = contacts.GetValueOrDefault((scope, put.Contact.Id));
            if (put.ExpectedRevision is not { } expected)
            {
                if (existing is not null)
                    return new ContactWriteConflict(put.Contact.Id, existing.Revision);
            }
            else if (existing is null)
                return new ContactWriteNotFound(put.Contact.Id);
            else if (existing.Revision != expected)
                return new ContactWriteConflict(put.Contact.Id, existing.Revision);
        }
        foreach (var delete in batch.Deletes)
        {
            var existing = contacts.GetValueOrDefault((scope, delete.Id));
            if (existing is null)
                return new ContactWriteNotFound(delete.Id);
            if (existing.Revision != delete.ExpectedRevision)
                return new ContactWriteConflict(delete.Id, existing.Revision);
        }
        if (batch.DeleteMerge is { } deleteMerge && !merges.ContainsKey((scope, deleteMerge)))
            return new ContactWriteNotFound(Merge: deleteMerge);
        if (batch.CreateMerge is { } createMerge && merges.ContainsKey((scope, createMerge.Id)))
            return new ContactWriteRejected(ContactWriteRejection.DuplicateMerge);

        var removedOrder = new List<ContactId>();
        foreach (var delete in batch.Deletes)
        {
            var cascade = Cascade(scope, [delete.Id]);
            cascade.Remove(delete.Id);
            if (delete.ExpectedCascade is { } expected && !expected.SetEquals(cascade))
                return new ContactWriteConflict(delete.Id, contacts[(scope, delete.Id)].Revision);
            removedOrder.Add(delete.Id);
            removedOrder.AddRange(cascade.OrderBy(id => id.Value, StringComparer.Ordinal));
        }
        var removed = removedOrder.ToHashSet();
        var written = batch.Puts.Select(put => put.Contact.Id).Concat(removed).ToHashSet();
        var claimedInBatch = new Dictionary<ContactIdentityAddress, ContactId>();
        foreach (var put in batch.Puts)
        {
            foreach (var identity in put.Contact.Identities.Where(identity => identity.Verification.IsVerified))
            {
                if (!claimedInBatch.TryAdd(identity.Address, put.Contact.Id))
                    return new ContactIdentityTaken(identity.Address, claimedInBatch[identity.Address]);
                var owner = VerifiedOwner(scope, identity.Address, written);
                if (owner is { } other)
                    return new ContactIdentityTaken(identity.Address, other);
            }
        }
        var creates = batch.Puts.Count(put => put.ExpectedRevision is null);
        if (contacts.Count - removed.Count + creates > MaximumContacts || changeCount + batch.Changes.Count > MaximumChanges
            || (batch.Audit is not null && audit.Count >= MaximumAuditEvents))
            return new ContactWriteRejected(ContactWriteRejection.CapacityExceeded);

        var stored = new List<StoredContact>();
        foreach (var put in batch.Puts)
        {
            var entry = new StoredContact(scope, put.Contact, put.ExpectedRevision is { } expected ? checked(expected + 1) : 1);
            contacts[(scope, put.Contact.Id)] = entry;
            stored.Add(entry);
        }
        foreach (var id in removed)
            contacts.Remove((scope, id));
        foreach (var key in merges.Where(pair => pair.Key.Scope == scope && (removed.Contains(pair.Value.Target) || removed.Contains(pair.Value.Source)))
            .Select(pair => pair.Key).ToList())
            merges.Remove(key);
        if (batch.DeleteMerge is { } deleted)
            merges.Remove((scope, deleted));
        if (batch.CreateMerge is { } created)
            merges[(scope, created.Id)] = created;

        if (!changes.TryGetValue(scope, out var log))
            changes[scope] = log = [];
        var appended = new List<ContactChange>();
        foreach (var draft in batch.Changes)
        {
            var change = new ContactChange(scope, log.Count == 0 ? 1 : log[^1].Sequence + 1, draft);
            log.Add(change);
            appended.Add(change);
        }
        changeCount += appended.Count;
        if (batch.Audit is { } auditEvent)
            audit.Add(auditEvent);
        return new ContactWriteApplied(stored, appended) { Deleted = [.. removedOrder.Distinct()] };
    }

    private HashSet<ContactId> Cascade(TicketScope scope, IEnumerable<ContactId> roots)
    {
        var removed = new HashSet<ContactId>();
        var pending = new Queue<ContactId>(roots);
        while (pending.TryDequeue(out var id))
        {
            if (!removed.Add(id))
                continue;
            foreach (var tombstone in contacts.Values.Where(stored => stored.Scope == scope && stored.Contact.MergedInto == id))
                pending.Enqueue(tombstone.Id);
        }
        return removed;
    }

    private ContactId? VerifiedOwner(TicketScope scope, ContactIdentityAddress address, HashSet<ContactId> excluded)
    {
        foreach (var stored in contacts.Values)
            if (stored.Scope == scope && !excluded.Contains(stored.Id)
                && stored.Contact.Identities.Any(identity => identity.Address == address && identity.Verification.IsVerified))
                return stored.Id;
        return null;
    }

    private static int Compare(string key, ContactId id, string afterKey, ContactId afterId)
    {
        var order = string.CompareOrdinal(key, afterKey);
        return order != 0 ? order : string.CompareOrdinal(id.Value, afterId.Value);
    }

    private static void RequireId(ContactId id)
    {
        if (id == default)
            throw new ArgumentException("A contact identifier is required.", nameof(id));
    }
}

/// <summary>Development-only attribute definition store: process-local, single-lock, not durable.</summary>
public sealed class InMemoryContactAttributeDefinitionStore : IContactAttributeDefinitionStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, string Key), StoredContactAttributeDefinition> definitions = [];

    public InMemoryContactAttributeDefinitionStore(int maximumEntries = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        MaximumEntries = maximumEntries;
    }

    public int MaximumEntries { get; }

    public Task<StoredContactAttributeDefinition?> GetAsync(TicketScope scope, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ContactAttributeDefinition.RequireKey(key, nameof(key));
        lock (gate)
            return Task.FromResult(definitions.GetValueOrDefault((scope, key)));
    }

    public Task<IReadOnlyList<StoredContactAttributeDefinition>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredContactAttributeDefinition>>([.. definitions.Values.Where(entry => entry.Scope == scope)
                .OrderBy(entry => entry.Definition.Key, StringComparer.Ordinal)]);
    }

    public Task<AttributeDefinitionWriteResult> CreateAsync(TicketScope scope, ContactAttributeDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        lock (gate)
        {
            if (definitions.TryGetValue((scope, definition.Key), out var existing))
                return Result(new AttributeDefinitionConflict(existing.Revision));
            if (definitions.Count >= MaximumEntries
                || definitions.Keys.Count(key => key.Scope == scope) >= IContactAttributeDefinitionStore.MaximumDefinitionsPerWorkspace)
                return Result(new ContactWriteRejected(ContactWriteRejection.CapacityExceeded));
            var stored = new StoredContactAttributeDefinition(scope, definition, 1);
            definitions.Add((scope, definition.Key), stored);
            return Result(new AttributeDefinitionApplied(stored));
        }
    }

    public Task<AttributeDefinitionWriteResult> UpdateAsync(TicketScope scope, ContactAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!definitions.TryGetValue((scope, definition.Key), out var existing))
                return Result(AttributeDefinitionNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new AttributeDefinitionConflict(existing.Revision));
            if (existing.Definition.Type != definition.Type)
                return Result(new ContactWriteRejected(ContactWriteRejection.TypeChanged));
            var stored = new StoredContactAttributeDefinition(scope, definition, checked(existing.Revision + 1));
            definitions[(scope, definition.Key)] = stored;
            return Result(new AttributeDefinitionApplied(stored));
        }
    }

    public Task<AttributeDefinitionWriteResult> DeleteAsync(TicketScope scope, string key, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ContactAttributeDefinition.RequireKey(key, nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!definitions.TryGetValue((scope, key), out var existing))
                return Result(AttributeDefinitionNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new AttributeDefinitionConflict(existing.Revision));
            definitions.Remove((scope, key));
            return Result(new AttributeDefinitionApplied(existing));
        }
    }

    private static Task<AttributeDefinitionWriteResult> Result(AttributeDefinitionWriteResult result) => Task.FromResult(result);
}
