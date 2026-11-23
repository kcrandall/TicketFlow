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
    private readonly Dictionary<TicketScope, ScopeBucket> scopes = [];
    private readonly Dictionary<(TicketScope Scope, Guid Id), ContactMergeRecord> merges = [];
    private readonly Dictionary<TicketScope, List<ContactChange>> changes = [];
    private readonly List<ContactAuditEvent> audit = [];
    private int contactCount;
    private int changeCount;

    private sealed class ScopeBucket
    {
        public Dictionary<ContactId, StoredContact> Contacts { get; } = [];
        public Dictionary<ContactIdentityAddress, HashSet<ContactId>> ByIdentity { get; } = [];
        public Dictionary<ContactIdentityAddress, ContactId> VerifiedByIdentity { get; } = [];
        public Dictionary<ContactId, HashSet<ContactId>> MergedInto { get; } = [];
    }

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
            return Task.FromResult(scopes.TryGetValue(scope, out var bucket) ? bucket.Contacts.GetValueOrDefault(id) : null);
    }

    public Task<IReadOnlyDictionary<ContactId, StoredContact>> GetManyAsync(TicketScope scope, IReadOnlyCollection<ContactId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
            RequireId(id);
        if (ids.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<ContactId, StoredContact>>(new Dictionary<ContactId, StoredContact>());
        lock (gate)
        {
            var result = new Dictionary<ContactId, StoredContact>(ids.Count);
            if (scopes.TryGetValue(scope, out var bucket))
            {
                foreach (var id in ids)
                    if (bucket.Contacts.TryGetValue(id, out var stored))
                        result[id] = stored;
            }
            return Task.FromResult<IReadOnlyDictionary<ContactId, StoredContact>>(result);
        }
    }

    public Task<IReadOnlyList<ContactIdentityOwner>> FindByIdentityAsync(TicketScope scope, ContactIdentityAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(address);
        lock (gate)
        {
            if (!scopes.TryGetValue(scope, out var bucket) || !bucket.ByIdentity.TryGetValue(address, out var ids) || ids.Count == 0)
                return Task.FromResult<IReadOnlyList<ContactIdentityOwner>>([]);
            var owners = new List<ContactIdentityOwner>(ids.Count);
            foreach (var id in ids)
            {
                if (!bucket.Contacts.TryGetValue(id, out var stored))
                    continue;
                foreach (var identity in stored.Contact.Identities)
                    if (identity.Address == address)
                    {
                        owners.Add(new ContactIdentityOwner(stored.Id, identity.Verification));
                        break;
                    }
            }
            owners.Sort(static (left, right) =>
            {
                var v = (left.Verification.IsVerified ? 0 : 1).CompareTo(right.Verification.IsVerified ? 0 : 1);
                return v != 0 ? v : string.CompareOrdinal(left.Contact.Value, right.Contact.Value);
            });
            if (owners.Count > IContactStore.MaximumIdentityOwners)
                owners.RemoveRange(IContactStore.MaximumIdentityOwners, owners.Count - IContactStore.MaximumIdentityOwners);
            return Task.FromResult<IReadOnlyList<ContactIdentityOwner>>(owners);
        }
    }

    public Task<ContactPage> SearchAsync(TicketScope scope, ContactSearch search, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            if (!scopes.TryGetValue(scope, out var bucket) || bucket.Contacts.Count == 0)
                return Task.FromResult(new ContactPage([], null));
            var rows = new List<(string Key, StoredContact Stored)>();
            foreach (var stored in bucket.Contacts.Values)
            {
                if (stored.Contact.MergedInto is not null)
                    continue;
                if (search.Mode == ContactSearchMode.Name)
                {
                    var key = ContactSearch.FoldName(stored.Contact.Profile.DisplayName);
                    if (!key.StartsWith(search.Prefix, StringComparison.Ordinal))
                        continue;
                    if (search.After is { } after && Compare(key, stored.Id, after.SortKey, after.Contact) <= 0)
                        continue;
                    rows.Add((key, stored));
                }
                else
                {
                    foreach (var identity in stored.Contact.Identities)
                    {
                        if (identity.Address.Kind != search.Kind)
                            continue;
                        if (search.Namespace is not null && !string.Equals(identity.Address.Namespace, search.Namespace, StringComparison.Ordinal))
                            continue;
                        if (!identity.Address.Value.StartsWith(search.Prefix, StringComparison.Ordinal))
                            continue;
                        if (search.After is { } after && Compare(identity.Address.Value, stored.Id, after.SortKey, after.Contact) <= 0)
                            continue;
                        rows.Add((identity.Address.Value, stored));
                    }
                }
            }
            rows.Sort(static (left, right) => Compare(left.Key, left.Stored.Id, right.Key, right.Stored.Id));
            var examinedCount = Math.Min(rows.Count, MaximumRowsExamined);
            var page = new List<(string Key, StoredContact Stored)>(Math.Min(search.Limit, examinedCount));
            for (var i = 0; i < examinedCount; i++)
            {
                var row = rows[i];
                if (!AccessFilterEvaluator.Matches(filter, ContactAccess.Resource(row.Stored)))
                    continue;
                page.Add(row);
                if (page.Count == search.Limit)
                    break;
            }
            var seen = new HashSet<ContactId>(page.Count);
            var result = new List<StoredContact>(page.Count);
            foreach (var row in page)
                if (seen.Add(row.Stored.Id))
                    result.Add(row.Stored);
            ContactSearchCursor? next = page.Count == search.Limit
                ? new ContactSearchCursor(page[^1].Key, page[^1].Stored.Id)
                : examinedCount == MaximumRowsExamined ? new ContactSearchCursor(rows[examinedCount - 1].Key, rows[examinedCount - 1].Stored.Id) : null;
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
        {
            if (!scopes.TryGetValue(scope, out var bucket) || !bucket.MergedInto.TryGetValue(target, out var tombstones) || tombstones.Count == 0)
                return Task.FromResult<IReadOnlyList<ContactId>>([]);
            var list = new List<ContactId>(tombstones);
            list.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            if (list.Count > limit)
                list.RemoveRange(limit, list.Count - limit);
            return Task.FromResult<IReadOnlyList<ContactId>>(list);
        }
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
            if (!changes.TryGetValue(scope, out var log) || log.Count == 0)
                return Task.FromResult<IReadOnlyList<ContactChange>>([]);
            var start = afterSequence >= log.Count ? log.Count : (int)afterSequence;
            if (start < log.Count && log[start].Sequence <= afterSequence)
            {
                while (start < log.Count && log[start].Sequence <= afterSequence)
                    start++;
            }
            var count = Math.Min(limit, log.Count - start);
            if (count <= 0)
                return Task.FromResult<IReadOnlyList<ContactChange>>([]);
            var result = new ContactChange[count];
            log.CopyTo(start, result, 0, count);
            return Task.FromResult<IReadOnlyList<ContactChange>>(result);
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
        scopes.TryGetValue(scope, out var bucket);
        foreach (var put in batch.Puts)
        {
            var existing = bucket is not null ? bucket.Contacts.GetValueOrDefault(put.Contact.Id) : null;
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
            var existing = bucket is not null ? bucket.Contacts.GetValueOrDefault(delete.Id) : null;
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
            var cascade = Cascade(bucket!, delete.Id);
            cascade.Remove(delete.Id);
            if (delete.ExpectedCascade is { } expected && !expected.SetEquals(cascade))
                return new ContactWriteConflict(delete.Id, bucket!.Contacts[delete.Id].Revision);
            removedOrder.Add(delete.Id);
            if (cascade.Count > 0)
            {
                var sorted = new List<ContactId>(cascade);
                sorted.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
                removedOrder.AddRange(sorted);
            }
        }
        var removed = removedOrder.Count > 0 ? new HashSet<ContactId>(removedOrder) : [];
        var written = new HashSet<ContactId>(batch.Puts.Count + removed.Count);
        foreach (var put in batch.Puts)
            written.Add(put.Contact.Id);
        foreach (var id in removed)
            written.Add(id);

        var claimedInBatch = new Dictionary<ContactIdentityAddress, ContactId>();
        foreach (var put in batch.Puts)
        {
            foreach (var identity in put.Contact.Identities)
            {
                if (!identity.Verification.IsVerified)
                    continue;
                if (!claimedInBatch.TryAdd(identity.Address, put.Contact.Id))
                    return new ContactIdentityTaken(identity.Address, claimedInBatch[identity.Address]);
                if (bucket is not null && bucket.VerifiedByIdentity.TryGetValue(identity.Address, out var owner) && !written.Contains(owner))
                    return new ContactIdentityTaken(identity.Address, owner);
            }
        }
        var creates = 0;
        foreach (var put in batch.Puts)
            if (put.ExpectedRevision is null)
                creates++;
        if (contactCount - removed.Count + creates > MaximumContacts || changeCount + batch.Changes.Count > MaximumChanges
            || (batch.Audit is not null && audit.Count >= MaximumAuditEvents))
            return new ContactWriteRejected(ContactWriteRejection.CapacityExceeded);

        bucket ??= scopes[scope] = new ScopeBucket();
        var stored = new List<StoredContact>(batch.Puts.Count);
        foreach (var put in batch.Puts)
        {
            if (bucket.Contacts.TryGetValue(put.Contact.Id, out var prior))
                Deindex(bucket, prior);
            else
                contactCount++;
            var entry = new StoredContact(scope, put.Contact, put.ExpectedRevision is { } expected ? checked(expected + 1) : 1);
            bucket.Contacts[put.Contact.Id] = entry;
            Index(bucket, entry);
            stored.Add(entry);
        }
        foreach (var id in removed)
        {
            if (bucket.Contacts.Remove(id, out var prior))
            {
                Deindex(bucket, prior);
                contactCount--;
            }
        }
        if (removed.Count > 0)
        {
            var deadMerges = new List<(TicketScope, Guid)>();
            foreach (var pair in merges)
                if (pair.Key.Scope == scope && (removed.Contains(pair.Value.Target) || removed.Contains(pair.Value.Source)))
                    deadMerges.Add(pair.Key);
            foreach (var key in deadMerges)
                merges.Remove(key);
        }
        if (batch.DeleteMerge is { } deleted)
            merges.Remove((scope, deleted));
        if (batch.CreateMerge is { } created)
            merges[(scope, created.Id)] = created;

        if (!changes.TryGetValue(scope, out var log))
            changes[scope] = log = [];
        var appended = new List<ContactChange>(batch.Changes.Count);
        foreach (var draft in batch.Changes)
        {
            var change = new ContactChange(scope, log.Count == 0 ? 1 : log[^1].Sequence + 1, draft);
            log.Add(change);
            appended.Add(change);
        }
        changeCount += appended.Count;
        if (batch.Audit is { } auditEvent)
            audit.Add(auditEvent);
        return new ContactWriteApplied(stored, appended) { Deleted = removedOrder.Count == 0 ? [] : [.. removedOrder.Distinct()] };
    }

    private static void Index(ScopeBucket bucket, StoredContact stored)
    {
        var contact = stored.Contact;
        foreach (var identity in contact.Identities)
        {
            if (!bucket.ByIdentity.TryGetValue(identity.Address, out var set))
                bucket.ByIdentity[identity.Address] = set = [];
            set.Add(contact.Id);
            if (identity.Verification.IsVerified)
                bucket.VerifiedByIdentity[identity.Address] = contact.Id;
        }
        if (contact.MergedInto is { } target)
        {
            if (!bucket.MergedInto.TryGetValue(target, out var tombstones))
                bucket.MergedInto[target] = tombstones = [];
            tombstones.Add(contact.Id);
        }
    }

    private static void Deindex(ScopeBucket bucket, StoredContact stored)
    {
        var contact = stored.Contact;
        foreach (var identity in contact.Identities)
        {
            if (bucket.ByIdentity.TryGetValue(identity.Address, out var set))
            {
                set.Remove(contact.Id);
                if (set.Count == 0)
                    bucket.ByIdentity.Remove(identity.Address);
            }
            if (identity.Verification.IsVerified && bucket.VerifiedByIdentity.TryGetValue(identity.Address, out var owner) && owner == contact.Id)
                bucket.VerifiedByIdentity.Remove(identity.Address);
        }
        if (contact.MergedInto is { } target && bucket.MergedInto.TryGetValue(target, out var tombstones))
        {
            tombstones.Remove(contact.Id);
            if (tombstones.Count == 0)
                bucket.MergedInto.Remove(target);
        }
    }

    private static HashSet<ContactId> Cascade(ScopeBucket bucket, ContactId root)
    {
        var removed = new HashSet<ContactId>();
        var pending = new Queue<ContactId>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var id))
        {
            if (!removed.Add(id))
                continue;
            if (bucket.MergedInto.TryGetValue(id, out var tombstones))
                foreach (var tombstone in tombstones)
                    pending.Enqueue(tombstone);
        }
        return removed;
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
    private readonly Dictionary<TicketScope, Dictionary<string, StoredContactAttributeDefinition>> definitions = [];
    private int totalCount;

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
            return Task.FromResult(definitions.TryGetValue(scope, out var map) ? map.GetValueOrDefault(key) : null);
    }

    public Task<IReadOnlyList<StoredContactAttributeDefinition>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            if (!definitions.TryGetValue(scope, out var map) || map.Count == 0)
                return Task.FromResult<IReadOnlyList<StoredContactAttributeDefinition>>([]);
            var list = new List<StoredContactAttributeDefinition>(map.Values);
            list.Sort(static (a, b) => string.CompareOrdinal(a.Definition.Key, b.Definition.Key));
            return Task.FromResult<IReadOnlyList<StoredContactAttributeDefinition>>(list);
        }
    }

    public Task<AttributeDefinitionWriteResult> CreateAsync(TicketScope scope, ContactAttributeDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(definition);
        lock (gate)
        {
            if (!definitions.TryGetValue(scope, out var map))
                map = null;
            if (map is not null && map.TryGetValue(definition.Key, out var existing))
                return Result(new AttributeDefinitionConflict(existing.Revision));
            if (totalCount >= MaximumEntries || (map?.Count ?? 0) >= IContactAttributeDefinitionStore.MaximumDefinitionsPerWorkspace)
                return Result(new ContactWriteRejected(ContactWriteRejection.CapacityExceeded));
            map ??= definitions[scope] = new Dictionary<string, StoredContactAttributeDefinition>(StringComparer.Ordinal);
            var stored = new StoredContactAttributeDefinition(scope, definition, 1);
            map.Add(definition.Key, stored);
            totalCount++;
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
            if (!definitions.TryGetValue(scope, out var map) || !map.TryGetValue(definition.Key, out var existing))
                return Result(AttributeDefinitionNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new AttributeDefinitionConflict(existing.Revision));
            if (existing.Definition.Type != definition.Type)
                return Result(new ContactWriteRejected(ContactWriteRejection.TypeChanged));
            var stored = new StoredContactAttributeDefinition(scope, definition, checked(existing.Revision + 1));
            map[definition.Key] = stored;
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
            if (!definitions.TryGetValue(scope, out var map) || !map.TryGetValue(key, out var existing))
                return Result(AttributeDefinitionNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result(new AttributeDefinitionConflict(existing.Revision));
            map.Remove(key);
            if (map.Count == 0)
                definitions.Remove(scope);
            totalCount--;
            return Result(new AttributeDefinitionApplied(existing));
        }
    }

    private static Task<AttributeDefinitionWriteResult> Result(AttributeDefinitionWriteResult result) => Task.FromResult(result);
}
