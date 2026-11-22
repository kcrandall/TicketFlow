using TicketFlow.Core;

namespace TicketFlow.Stores.InMemory;

/// <summary>
/// Bounded, process-local building block for development stores: a dictionary keyed by full <see cref="TicketScope"/> plus key,
/// with revision-fenced writes under a single lock. State is lost on restart and never shared between processes or nodes;
/// it is an example, not a durable store.
/// </summary>
/// <remarks>Stored values should be immutable; the store returns the same instances it holds.</remarks>
public sealed class ScopedRevisionStore<TKey, TValue>
    where TKey : notnull
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, TKey Key), RevisionedEntry<TKey, TValue>> entries;

    public ScopedRevisionStore(int maximumEntries = 10_000, IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        MaximumEntries = maximumEntries;
        entries = new(new ScopedKeyComparer(keyComparer ?? EqualityComparer<TKey>.Default));
    }

    public int MaximumEntries { get; }

    /// <summary>Number of entries across all scopes.</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>Returns the entry for <paramref name="key"/> in exactly <paramref name="scope"/>, or <see langword="null"/>.</summary>
    public RevisionedEntry<TKey, TValue>? Find(TicketScope scope, TKey key)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);
        lock (gate)
            return entries.GetValueOrDefault((scope, key));
    }

    /// <summary>Adds a new entry at revision 1, or reports a conflict with the existing revision.</summary>
    /// <exception cref="InvalidOperationException">The store is at <see cref="MaximumEntries"/>.</exception>
    public RevisionWriteResult<TKey, TValue> Add(TicketScope scope, TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);
        lock (gate)
        {
            if (entries.TryGetValue((scope, key), out var existing))
                return new RevisionConflict(existing.Revision);
            if (entries.Count >= MaximumEntries)
                throw new InvalidOperationException("The in-memory store has reached its configured capacity.");

            var added = new RevisionedEntry<TKey, TValue>(scope, key, value, 1);
            entries.Add((scope, key), added);
            return new RevisionApplied<TKey, TValue>(added);
        }
    }

    /// <summary>Replaces the value when the current revision equals <paramref name="expectedRevision"/>, incrementing the revision.</summary>
    public RevisionWriteResult<TKey, TValue> Replace(TicketScope scope, TKey key, long expectedRevision, TValue value)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!entries.TryGetValue((scope, key), out var existing))
                return RevisionNotFound.Instance;
            if (existing.Revision != expectedRevision)
                return new RevisionConflict(existing.Revision);

            var replaced = existing with { Value = value, Revision = checked(existing.Revision + 1) };
            entries[(scope, key)] = replaced;
            return new RevisionApplied<TKey, TValue>(replaced);
        }
    }

    /// <summary>Removes the entry when the current revision equals <paramref name="expectedRevision"/>.</summary>
    public RevisionWriteResult<TKey, TValue> Remove(TicketScope scope, TKey key, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (!entries.TryGetValue((scope, key), out var existing))
                return RevisionNotFound.Instance;
            if (existing.Revision != expectedRevision)
                return new RevisionConflict(existing.Revision);

            entries.Remove((scope, key));
            return new RevisionApplied<TKey, TValue>(existing);
        }
    }

    private sealed class ScopedKeyComparer(IEqualityComparer<TKey> keyComparer) : IEqualityComparer<(TicketScope Scope, TKey Key)>
    {
        public bool Equals((TicketScope Scope, TKey Key) x, (TicketScope Scope, TKey Key) y) =>
            x.Scope == y.Scope && keyComparer.Equals(x.Key, y.Key);

        public int GetHashCode((TicketScope Scope, TKey Key) obj) =>
            HashCode.Combine(obj.Scope, keyComparer.GetHashCode(obj.Key));
    }
}
