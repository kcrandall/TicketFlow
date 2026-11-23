using System.Runtime.CompilerServices;

namespace TicketFlow.Core;

/// <summary>Zero-allocation success payload for <see cref="TicketResult{T}"/> and <see cref="TicketResult{T, TFailure}"/>.</summary>
public readonly record struct TicketValue<T>(T Value);

/// <summary>Common refusal categories shared by TicketFlow service operations.</summary>
public enum TicketFailureKind
{
    /// <summary>Access control denied the request.</summary>
    Forbidden = 1,

    /// <summary>The request failed contextual validation.</summary>
    Invalid = 2,

    /// <summary>The target record does not exist in the actor's scope.</summary>
    NotFound = 3,

    /// <summary>The expected revision did not match or a unique record already exists.</summary>
    Conflict = 4,

    /// <summary>A store limit, domain state or policy refused the operation.</summary>
    Rejected = 5,
}

/// <summary>
/// A non-sensitive service failure. <see cref="Errors"/> are operator/client messages (never PII or raw input);
/// <see cref="CurrentRevision"/> accompanies optimistic-concurrency conflicts; <see cref="Code"/> carries an optional
/// domain-specific machine code (for example <c>closed</c>, <c>taken</c> or <c>address-not-owned</c>).
/// </summary>
public sealed record TicketFailure(TicketFailureKind Kind, IReadOnlyList<string> Errors, long? CurrentRevision = null, string? Code = null)
{
    public static TicketFailure Forbidden { get; } = new(TicketFailureKind.Forbidden, []);

    public static TicketFailure NotFound { get; } = new(TicketFailureKind.NotFound, []);

    public static TicketFailure ForbiddenWith(IReadOnlyList<string> errors) =>
        errors.Count == 0 ? Forbidden : new(TicketFailureKind.Forbidden, errors);

    public static TicketFailure Invalid(string error) => new(TicketFailureKind.Invalid, [error]);

    public static TicketFailure Invalid(IReadOnlyList<string> errors) => new(TicketFailureKind.Invalid, errors);

    public static TicketFailure Conflict(long? currentRevision = null, IReadOnlyList<string>? errors = null, string? code = null) =>
        new(TicketFailureKind.Conflict, errors ?? [], currentRevision, code);

    public static TicketFailure Rejected(string error, string? code = null) =>
        new(TicketFailureKind.Rejected, [error], null, code);

    public static TicketFailure Rejected(IReadOnlyList<string> errors, string? code = null) =>
        new(TicketFailureKind.Rejected, errors, null, code);
}

/// <summary>
/// Non-boxing C# 15 union for a TicketFlow service operation returning either <see cref="TicketValue{T}"/> or
/// <see cref="TicketFailure"/>. Stores <typeparamref name="T"/> inline with zero heap allocation on success and enforces
/// exhaustive pattern matching without a discard arm.
/// </summary>
[Union]
public readonly struct TicketResult<T> : IUnion, IEquatable<TicketResult<T>>
{
    private readonly byte tag;
    private readonly T? value;
    private readonly TicketFailure? failure;

    public TicketResult(TicketValue<T> ok)
    {
        tag = 1;
        value = ok.Value;
        failure = null;
    }

    public TicketResult(TicketFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        tag = 2;
        value = default;
        this.failure = failure;
    }

    /// <summary>True when the union was initialized (either success or failure).</summary>
    public bool HasValue => tag != 0;

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess => tag == 1;

    /// <summary>Boxed union payload for <see cref="IUnion"/> interoperability; pattern matching uses <see cref="TryGetValue(out TicketValue{T})"/> without boxing.</summary>
    public object? Value => tag switch
    {
        1 => new TicketValue<T>(value!),
        2 => failure,
        _ => null,
    };

    public bool TryGetValue(out TicketValue<T> ok)
    {
        if (tag == 1)
        {
            ok = new TicketValue<T>(value!);
            return true;
        }
        ok = default;
        return false;
    }

    public bool TryGetValue(out TicketFailure failure)
    {
        if (tag == 2)
        {
            failure = this.failure!;
            return true;
        }
        failure = null!;
        return false;
    }

    /// <summary>Transforms the success value without changing a failure.</summary>
    public TicketResult<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return tag switch
        {
            1 => new TicketValue<TOut>(map(value!)),
            2 => failure!,
            _ => throw new InvalidOperationException("The result is not initialized."),
        };
    }

    /// <summary>Chains another fallible operation when this result succeeded, or propagates the failure.</summary>
    public TicketResult<TOut> Bind<TOut>(Func<T, TicketResult<TOut>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return tag switch
        {
            1 => bind(value!),
            2 => failure!,
            _ => throw new InvalidOperationException("The result is not initialized."),
        };
    }

    /// <summary>Projects either the success value or the failure into a single result.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TicketFailure, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return tag switch
        {
            1 => onSuccess(value!),
            2 => onFailure(failure!),
            _ => throw new InvalidOperationException("The result is not initialized."),
        };
    }

    public bool Equals(TicketResult<T> other) => tag == other.tag && tag switch
    {
        1 => EqualityComparer<T>.Default.Equals(value, other.value),
        2 => Equals(failure, other.failure),
        _ => true,
    };

    public override bool Equals(object? obj) => obj is TicketResult<T> other && Equals(other);

    public override int GetHashCode() => tag switch
    {
        1 => HashCode.Combine(tag, value),
        2 => HashCode.Combine(tag, failure),
        _ => 0,
    };

    public static bool operator ==(TicketResult<T> left, TicketResult<T> right) => left.Equals(right);

    public static bool operator !=(TicketResult<T> left, TicketResult<T> right) => !left.Equals(right);

    public static implicit operator TicketResult<T>(TicketValue<T> ok) => new(ok);

    public static implicit operator TicketResult<T>(TicketFailure failure) => new(failure);
}

/// <summary>
/// Non-boxing C# 15 union for a service operation returning either <see cref="TicketValue{T}"/> or a domain-specific
/// <typeparamref name="TFailure"/>. Stores <typeparamref name="T"/> inline with zero heap allocation on success and enforces
/// exhaustive pattern matching without a discard arm.
/// </summary>
[Union]
public readonly struct TicketResult<T, TFailure> : IUnion, IEquatable<TicketResult<T, TFailure>>
    where TFailure : class
{
    private readonly byte tag;
    private readonly T? value;
    private readonly TFailure? failure;

    public TicketResult(TicketValue<T> ok)
    {
        tag = 1;
        value = ok.Value;
        failure = null;
    }

    public TicketResult(TFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        tag = 2;
        value = default;
        this.failure = failure;
    }

    /// <summary>True when the union was initialized (either success or failure).</summary>
    public bool HasValue => tag != 0;

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess => tag == 1;

    /// <summary>Boxed union payload for <see cref="IUnion"/> interoperability; pattern matching uses <see cref="TryGetValue(out TicketValue{T})"/> without boxing.</summary>
    public object? Value => tag switch
    {
        1 => new TicketValue<T>(value!),
        2 => failure,
        _ => null,
    };

    public bool TryGetValue(out TicketValue<T> ok)
    {
        if (tag == 1)
        {
            ok = new TicketValue<T>(value!);
            return true;
        }
        ok = default;
        return false;
    }

    public bool TryGetValue(out TFailure failure)
    {
        if (tag == 2)
        {
            failure = this.failure!;
            return true;
        }
        failure = null!;
        return false;
    }

    /// <summary>Transforms the success value without changing a failure.</summary>
    public TicketResult<TOut, TFailure> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return tag switch
        {
            1 => new TicketValue<TOut>(map(value!)),
            2 => failure!,
            _ => throw new InvalidOperationException("The result is not initialized."),
        };
    }

    /// <summary>Chains another fallible operation when this result succeeded, or propagates the failure.</summary>
    public TicketResult<TOut, TFailure> Bind<TOut>(Func<T, TicketResult<TOut, TFailure>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return tag switch
        {
            1 => bind(value!),
            2 => failure!,
            _ => throw new InvalidOperationException("The result is not initialized."),
        };
    }

    /// <summary>Projects either the success value or the failure into a single result.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TFailure, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return tag switch
        {
            1 => onSuccess(value!),
            2 => onFailure(failure!),
            _ => throw new InvalidOperationException("The result is not initialized."),
        };
    }

    public bool Equals(TicketResult<T, TFailure> other) => tag == other.tag && tag switch
    {
        1 => EqualityComparer<T>.Default.Equals(value, other.value),
        2 => EqualityComparer<TFailure>.Default.Equals(failure, other.failure),
        _ => true,
    };

    public override bool Equals(object? obj) => obj is TicketResult<T, TFailure> other && Equals(other);

    public override int GetHashCode() => tag switch
    {
        1 => HashCode.Combine(tag, value),
        2 => HashCode.Combine(tag, failure),
        _ => 0,
    };

    public static bool operator ==(TicketResult<T, TFailure> left, TicketResult<T, TFailure> right) => left.Equals(right);

    public static bool operator !=(TicketResult<T, TFailure> left, TicketResult<T, TFailure> right) => !left.Equals(right);

    public static implicit operator TicketResult<T, TFailure>(TicketValue<T> ok) => new(ok);

    public static implicit operator TicketResult<T, TFailure>(TFailure failure) => new(failure);
}

/// <summary>Zero-allocation factories for <see cref="TicketResult{T}"/> and <see cref="TicketResult{T, TFailure}"/>.</summary>
public static class TicketResult
{
    public static TicketResult<T> Ok<T>(T value) => new(new TicketValue<T>(value));

    public static TicketResult<T, TFailure> Ok<T, TFailure>(T value)
        where TFailure : class => new(new TicketValue<T>(value));
}
