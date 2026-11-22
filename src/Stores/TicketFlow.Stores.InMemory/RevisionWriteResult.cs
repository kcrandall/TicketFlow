using TicketFlow.Core;

namespace TicketFlow.Stores.InMemory;

/// <summary>A stored value with its scope, key and monotonically increasing revision (first revision is 1).</summary>
public sealed record RevisionedEntry<TKey, TValue>(TicketScope Scope, TKey Key, TValue Value, long Revision)
    where TKey : notnull;

/// <summary>The write was applied. For removals, <see cref="Entry"/> is the entry that was removed.</summary>
public sealed record RevisionApplied<TKey, TValue>(RevisionedEntry<TKey, TValue> Entry)
    where TKey : notnull;

/// <summary>No entry exists for the key within the requested scope.</summary>
public sealed record RevisionNotFound
{
    public static RevisionNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match, or an add found an existing entry. Nothing was written.</summary>
public sealed record RevisionConflict(long CurrentRevision);

/// <summary>Outcome of a revision-fenced write. Switch over every case without a discard arm.</summary>
public union RevisionWriteResult<TKey, TValue>(RevisionApplied<TKey, TValue>, RevisionNotFound, RevisionConflict)
    where TKey : notnull;
