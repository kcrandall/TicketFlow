using System.Runtime.CompilerServices;

namespace TicketFlow.Core;

/// <summary>Zero-allocation applied case for <see cref="StoreWriteResult{T, TRejection}"/>.</summary>
public readonly record struct StoreApplied<T>(T Value);

/// <summary>Zero-allocation not-found case for <see cref="StoreWriteResult{T, TRejection}"/>.</summary>
public readonly record struct StoreNotFound
{
    public static StoreNotFound Instance => default;
}

/// <summary>Zero-allocation optimistic-concurrency conflict case for <see cref="StoreWriteResult{T, TRejection}"/>.</summary>
public readonly record struct StoreConflict(long? CurrentRevision);

/// <summary>Zero-allocation contract-rejection case for <see cref="StoreWriteResult{T, TRejection}"/>.</summary>
public readonly record struct StoreRejected<TRejection>(TRejection Reason)
    where TRejection : struct, Enum;

/// <summary>
/// Non-boxing C# 15 union for a revision-fenced store write: <see cref="StoreApplied{T}"/>, <see cref="StoreNotFound"/>,
/// <see cref="StoreConflict"/> or <see cref="StoreRejected{TRejection}"/>. Every case is stored inline with zero heap
/// allocation and switches are compiler-checked for exhaustiveness without a discard arm.
/// </summary>
[Union]
public readonly struct StoreWriteResult<T, TRejection> : IUnion, IEquatable<StoreWriteResult<T, TRejection>>
    where TRejection : struct, Enum
{
    private readonly byte tag;
    private readonly T? value;
    private readonly long currentRevision;
    private readonly TRejection rejection;

    public StoreWriteResult(StoreApplied<T> applied)
    {
        tag = 1;
        value = applied.Value;
    }

    public StoreWriteResult(StoreNotFound _) => tag = 2;

    public StoreWriteResult(StoreConflict conflict)
    {
        if (conflict.CurrentRevision is { } revision)
        {
            tag = 3;
            currentRevision = revision;
        }
        else
        {
            tag = 5;
        }
    }

    public StoreWriteResult(StoreRejected<TRejection> rejected)
    {
        tag = 4;
        rejection = rejected.Reason;
    }

    /// <summary>True when the union was initialized.</summary>
    public bool HasValue => tag != 0;

    /// <summary>True when the write was applied.</summary>
    public bool IsApplied => tag == 1;

    /// <summary>Boxed union payload for <see cref="IUnion"/> interoperability; pattern matching uses <c>TryGetValue</c> without boxing.</summary>
    public object? Value => tag switch
    {
        1 => new StoreApplied<T>(value!),
        2 => default(StoreNotFound),
        3 => new StoreConflict(currentRevision),
        4 => new StoreRejected<TRejection>(rejection),
        5 => new StoreConflict(null),
        _ => null,
    };

    public bool TryGetValue(out StoreApplied<T> applied)
    {
        if (tag == 1)
        {
            applied = new StoreApplied<T>(value!);
            return true;
        }
        applied = default;
        return false;
    }

    public bool TryGetValue(out StoreNotFound notFound)
    {
        notFound = default;
        return tag == 2;
    }

    public bool TryGetValue(out StoreConflict conflict)
    {
        if (tag == 3)
        {
            conflict = new StoreConflict(currentRevision);
            return true;
        }
        if (tag == 5)
        {
            conflict = new StoreConflict(null);
            return true;
        }
        conflict = default;
        return false;
    }

    public bool TryGetValue(out StoreRejected<TRejection> rejected)
    {
        if (tag == 4)
        {
            rejected = new StoreRejected<TRejection>(rejection);
            return true;
        }
        rejected = default;
        return false;
    }

    /// <summary>Maps this store write outcome to a <see cref="TicketResult{T}"/>.</summary>
    public TicketResult<T> ToTicketResult(Func<TRejection, TicketFailure> onRejected) => this switch
    {
        StoreApplied<T>(var applied) => new TicketValue<T>(applied),
        StoreNotFound => TicketFailure.NotFound,
        StoreConflict(var current) => TicketFailure.Conflict(current),
        StoreRejected<TRejection>(var reason) => onRejected(reason),
    };

    /// <summary>Maps this store write outcome to a <see cref="TicketResult{T}"/> using a message formatter for rejections.</summary>
    public TicketResult<T> ToTicketResult(Func<TRejection, string> rejectionMessage) => this switch
    {
        StoreApplied<T>(var applied) => new TicketValue<T>(applied),
        StoreNotFound => TicketFailure.NotFound,
        StoreConflict(var current) => TicketFailure.Conflict(current),
        StoreRejected<TRejection>(var reason) => TicketFailure.Rejected(rejectionMessage(reason)),
    };

    public bool Equals(StoreWriteResult<T, TRejection> other) => tag == other.tag && tag switch
    {
        1 => EqualityComparer<T>.Default.Equals(value, other.value),
        2 => true,
        3 => currentRevision == other.currentRevision,
        4 => EqualityComparer<TRejection>.Default.Equals(rejection, other.rejection),
        5 => true,
        _ => true,
    };

    public override bool Equals(object? obj) => obj is StoreWriteResult<T, TRejection> other && Equals(other);

    public override int GetHashCode() => tag switch
    {
        1 => HashCode.Combine(tag, value),
        2 => tag.GetHashCode(),
        3 => HashCode.Combine(tag, currentRevision),
        4 => HashCode.Combine(tag, rejection),
        5 => tag.GetHashCode(),
        _ => 0,
    };

    public static bool operator ==(StoreWriteResult<T, TRejection> left, StoreWriteResult<T, TRejection> right) => left.Equals(right);

    public static bool operator !=(StoreWriteResult<T, TRejection> left, StoreWriteResult<T, TRejection> right) => !left.Equals(right);

    public static implicit operator StoreWriteResult<T, TRejection>(StoreApplied<T> applied) => new(applied);

    public static implicit operator StoreWriteResult<T, TRejection>(StoreNotFound notFound) => new(notFound);

    public static implicit operator StoreWriteResult<T, TRejection>(StoreConflict conflict) => new(conflict);

    public static implicit operator StoreWriteResult<T, TRejection>(StoreRejected<TRejection> rejected) => new(rejected);
}

/// <summary>Zero-allocation factories for <see cref="StoreWriteResult{T, TRejection}"/>.</summary>
public static class StoreWriteResult
{
    public static StoreApplied<T> Applied<T>(T value) => new(value);

    public static StoreNotFound NotFound => default;

    public static StoreConflict Conflict(long? currentRevision) => new(currentRevision);

    public static StoreRejected<TRejection> Rejected<TRejection>(TRejection reason)
        where TRejection : struct, Enum => new(reason);
}
