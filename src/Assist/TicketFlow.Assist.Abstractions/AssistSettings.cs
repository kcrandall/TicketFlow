using System.Runtime.CompilerServices;
using TicketFlow.Core;

namespace TicketFlow.Assist;

/// <summary>
/// Workspace assistant settings: enabled flag, daily token quota (UTC day, 1 to 10^10 tokens), per-minute request limit (1-10000)
/// and optional model identifiers per feature (each must be registered in <see cref="IAssistModelRegistry"/> when used).
/// </summary>
public sealed record AssistSettings
{
    public const long MaximumDailyTokenLimit = 10_000_000_000;
    public const int MaximumRequestsPerMinute = 10_000;

    public AssistSettings(bool enabled, long dailyTokenLimit, int requestsPerMinute, IReadOnlyDictionary<AssistFeature, string>? models = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dailyTokenLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dailyTokenLimit, MaximumDailyTokenLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requestsPerMinute, MaximumRequestsPerMinute);
        var snapshot = new SortedDictionary<AssistFeature, string>();
        foreach (var (feature, model) in models ?? new Dictionary<AssistFeature, string>())
        {
            if (!Enum.IsDefined(feature))
                throw new ArgumentOutOfRangeException(nameof(models));
            snapshot[feature] = AssistText.RequireToken(model, AssistModelDefinition.MaximumIdLength, nameof(models), "Model identifiers");
        }
        Enabled = enabled;
        DailyTokenLimit = dailyTokenLimit;
        RequestsPerMinute = requestsPerMinute;
        Models = snapshot;
    }

    public bool Enabled { get; }
    public long DailyTokenLimit { get; }
    public int RequestsPerMinute { get; }

    /// <summary>Per-feature model overrides, ordered by feature.</summary>
    public IReadOnlyDictionary<AssistFeature, string> Models { get; }

    public bool Equals(AssistSettings? other) =>
        other is not null && Enabled == other.Enabled && DailyTokenLimit == other.DailyTokenLimit && RequestsPerMinute == other.RequestsPerMinute
        && Models.SequenceEqual(other.Models);

    public override int GetHashCode() => HashCode.Combine(Enabled, DailyTokenLimit, RequestsPerMinute, Models.Count);
}

/// <summary>Stored settings. Revision 0 means no stored record: the application defaults apply.</summary>
public sealed record StoredAssistSettings(TicketScope Scope, AssistSettings Settings, long Revision);

/// <summary>Durable usage record states. Persisted numerically; never renumber.</summary>
public enum AssistUsageState
{
    /// <summary>Tokens are reserved for an in-flight call and count against the quota at the reserved amount.</summary>
    Reserved = 1,

    /// <summary>The call completed; actual usage counts.</summary>
    Completed = 2,

    /// <summary>The call failed, was refused or cancelled; actual usage (often zero) counts.</summary>
    Failed = 3,
}

/// <summary>
/// One assistant call: feature, model, actor and conversation identifiers and token counts only — never prompts or outputs.
/// <see cref="CountedTokens"/> is what the quota charges.
/// </summary>
public sealed record AssistUsageRecord
{
    public AssistUsageRecord(Guid id, AssistFeature feature, string model, Access.AccessActor actor, ConversationId? conversation, AssistUsageState state,
        long reservedTokens, AssistUsage usage, DateTimeOffset createdAt, DateTimeOffset? completedAt, bool estimated = false)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A usage identifier is required.", nameof(id));
        if (!Enum.IsDefined(feature) || !Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentOutOfRangeException.ThrowIfNegative(reservedTokens);
        if (conversation == default(ConversationId))
            throw new ArgumentException("Conversation identifiers must be initialized when supplied.", nameof(conversation));
        if ((state == AssistUsageState.Reserved) != (completedAt is null))
            throw new ArgumentException("Only settled records carry a completion time.", nameof(completedAt));
        if (estimated && state == AssistUsageState.Reserved)
            throw new ArgumentException("Only settled records can be charged at their estimate.", nameof(estimated));
        Id = id;
        Feature = feature;
        Model = AssistText.RequireToken(model, AssistModelDefinition.MaximumIdLength, nameof(model), "Model identifiers");
        Actor = actor;
        Conversation = conversation;
        State = state;
        ReservedTokens = reservedTokens;
        Usage = usage;
        CreatedAt = AssistLimits.Truncate(createdAt);
        CompletedAt = completedAt is { } completed ? AssistLimits.Truncate(completed) : null;
        Estimated = estimated;
    }

    public Guid Id { get; }
    public AssistFeature Feature { get; }
    public string Model { get; }
    public Access.AccessActor Actor { get; }
    public ConversationId? Conversation { get; }
    public AssistUsageState State { get; }
    public long ReservedTokens { get; }
    public AssistUsage Usage { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// The settled record is charged at <see cref="ReservedTokens"/> because actual usage is unknown (an ambiguous failure, a
    /// response without usage, or an abandoned reservation swept by maintenance).
    /// </summary>
    public bool Estimated { get; }

    public long CountedTokens => State == AssistUsageState.Reserved || Estimated ? ReservedTokens : Usage.TotalTokens;

    public AssistUsageRecord Settle(AssistUsageState state, AssistUsage usage, DateTimeOffset at, bool estimated = false) =>
        new(Id, Feature, Model, Actor, Conversation, state, ReservedTokens, usage, CreatedAt, at, estimated);
}

public enum AssistQuotaKind
{
    DailyTokens = 1,
    RequestsPerMinute = 2,
}

/// <summary>The reservation was stored.</summary>
public sealed record AssistUsageReserved(AssistUsageRecord Record);

/// <summary>The quota would be exceeded; nothing was written. <see cref="RetryAt"/> is when capacity frees up (at the latest).</summary>
public sealed record AssistQuotaExceeded(AssistQuotaKind Kind, DateTimeOffset RetryAt);

/// <summary>Outcome of an atomic quota reservation. Switch over every case without a discard arm.</summary>
public union AssistReservationResult(AssistUsageReserved, AssistQuotaExceeded);

/// <summary>Non-boxing outcome of a revision-fenced settings write. Switch over every case without a discard arm.</summary>
[Union]
public readonly struct AssistWriteResult<T> : IUnion, IEquatable<AssistWriteResult<T>>
{
    private readonly byte tag;
    private readonly T? value;
    private readonly long currentRevision;

    public AssistWriteResult(StoreApplied<T> applied)
    {
        tag = 1;
        value = applied.Value;
    }

    public AssistWriteResult(StoreConflict conflict)
    {
        if (conflict.CurrentRevision is { } revision)
        {
            tag = 2;
            currentRevision = revision;
        }
        else
        {
            tag = 3;
        }
    }

    public bool HasValue => tag != 0;

    public object? Value => tag switch
    {
        1 => new StoreApplied<T>(value!),
        2 => new StoreConflict(currentRevision),
        3 => new StoreConflict(null),
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

    public bool TryGetValue(out StoreConflict conflict)
    {
        if (tag == 2)
        {
            conflict = new StoreConflict(currentRevision);
            return true;
        }
        if (tag == 3)
        {
            conflict = new StoreConflict(null);
            return true;
        }
        conflict = default;
        return false;
    }

    public bool Equals(AssistWriteResult<T> other) => tag == other.tag && tag switch
    {
        1 => EqualityComparer<T>.Default.Equals(value, other.value),
        2 => currentRevision == other.currentRevision,
        3 => true,
        _ => true,
    };

    public override bool Equals(object? obj) => obj is AssistWriteResult<T> other && Equals(other);

    public override int GetHashCode() => tag switch
    {
        1 => HashCode.Combine(tag, value),
        2 => HashCode.Combine(tag, currentRevision),
        3 => tag.GetHashCode(),
        _ => 0,
    };

    public static bool operator ==(AssistWriteResult<T> left, AssistWriteResult<T> right) => left.Equals(right);

    public static bool operator !=(AssistWriteResult<T> left, AssistWriteResult<T> right) => !left.Equals(right);

    public static implicit operator AssistWriteResult<T>(StoreApplied<T> applied) => new(applied);

    public static implicit operator AssistWriteResult<T>(StoreConflict conflict) => new(conflict);
}

/// <summary>
/// Aggregated usage of one feature and model in a time range. <see cref="ReservedTokens"/> are held by unsettled reservations;
/// <see cref="EstimatedTokens"/> were charged at the estimate because actual usage is unknown.
/// </summary>
public sealed record AssistUsageTotal(AssistFeature Feature, string Model, long Requests, long Failed, long InputTokens, long OutputTokens, long CacheReadInputTokens,
    long CacheCreationInputTokens, long ReservedTokens, long EstimatedTokens);

/// <summary>Usage of a workspace in <c>[From, To)</c>, ordered by feature then model (ordinal).</summary>
public sealed record AssistUsageSummary(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<AssistUsageTotal> Totals);

/// <summary>Bounds assist stores and services enforce.</summary>
public static class AssistLimits
{
    /// <summary>Longest usage summary range.</summary>
    public static TimeSpan MaximumSummaryRange { get; } = TimeSpan.FromDays(92);

    /// <summary>Most records one purge or sweep call processes.</summary>
    public const int MaximumPurgeBatch = 10_000;

    /// <summary>UTC with whole-microsecond precision, the resolution durable stores (PostgreSQL) keep.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    /// <summary>Throws unless <c>[from, to)</c> is non-empty and at most <see cref="MaximumSummaryRange"/> long.</summary>
    public static void RequireRange(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || to - from > MaximumSummaryRange)
            throw new ArgumentException($"Usage ranges must be non-empty and at most {MaximumSummaryRange.TotalDays} days.", nameof(to));
    }

    /// <summary>Start of the UTC day containing <paramref name="instant"/> (the daily quota window).</summary>
    public static DateTimeOffset DayStart(DateTimeOffset instant) => new(instant.UtcDateTime.Date, TimeSpan.Zero);
}
