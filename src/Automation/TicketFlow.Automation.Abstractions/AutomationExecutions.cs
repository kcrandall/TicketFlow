using System.Globalization;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>The action changed the conversation (or was a no-op: <see cref="Sequences"/> is empty) and produced these timeline sequences.</summary>
public sealed record AutomationActionApplied
{
    public const int MaximumSequences = 32;

    public AutomationActionApplied(IEnumerable<long> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        Sequences = [.. sequences];
        if (Sequences.Count > MaximumSequences || Sequences.Any(sequence => sequence < 1))
            throw new ArgumentException($"At most {MaximumSequences} positive sequences.", nameof(sequences));
    }

    public IReadOnlyList<long> Sequences { get; }

    public bool Equals(AutomationActionApplied? other) => other is not null && Sequences.SequenceEqual(other.Sequences);

    public override int GetHashCode() => Sequences.Count;
}

/// <summary>The action was not attempted (for example a notify-only SLA action, or no survey integration). <see cref="Reason"/> is a token.</summary>
public sealed record AutomationActionSkipped
{
    public AutomationActionSkipped(string reason) => Reason = AutomationText.RequireToken(reason, 64, nameof(reason), "Reasons");

    public string Reason { get; }
}

/// <summary>Why the conversation service (or a guard) refused an action. Never renumber.</summary>
public enum AutomationActionRefusal
{
    /// <summary>The acting principal lacks the permission (automation never exceeds the automation client's grants).</summary>
    Forbidden = 1,
    Invalid = 2,
    Closed = 3,
    Rejected = 4,
    NotFound = 5,
    Conflict = 6,
}

public sealed record AutomationActionRefused
{
    public AutomationActionRefused(AutomationActionRefusal reason) => Reason = AutomationText.Defined(reason, nameof(reason));

    public AutomationActionRefusal Reason { get; }
}

/// <summary>The action failed outside the conversation service (webhook transport, survey integration). <see cref="Code"/> is a token.</summary>
public sealed record AutomationActionFailed
{
    public AutomationActionFailed(string code) => Code = AutomationText.RequireToken(code, 64, nameof(code), "Failure codes");

    public string Code { get; }
}

/// <summary>Outcome of one action. Switch over every case without a discard arm; persisted only through <see cref="AutomationCodec"/>.</summary>
public union AutomationActionOutcome(AutomationActionApplied, AutomationActionSkipped, AutomationActionRefused, AutomationActionFailed);

/// <summary>Execution lifecycle. Persisted numerically; never renumber.</summary>
public enum AutomationExecutionStatus
{
    /// <summary>Started; <see cref="AutomationExecution.Outcomes"/> lists the actions finished so far (resumed after a crash).</summary>
    Running = 1,
    Completed = 2,

    /// <summary>Not executed because a loop guard tripped; see <see cref="AutomationExecution.Suppression"/>.</summary>
    Suppressed = 3,
}

/// <summary>Loop guards. Persisted numerically; never renumber.</summary>
public enum AutomationSuppression
{
    /// <summary>The triggering event was caused by an automation chain at the maximum depth (or of unknown cause).</summary>
    DepthExceeded = 1,

    /// <summary>The conversation reached the per-conversation execution rate limit.</summary>
    RateLimited = 2,
}

/// <summary>The automation execution that produced a timeline event, and its chain depth (first execution in a chain is depth 1).</summary>
public sealed record AutomationCause(Guid Execution, AutomationRuleId Rule, int Depth);

/// <summary>
/// The durable record of one rule execution for one triggering event, unique per (rule, conversation, <see cref="TriggerKey"/>).
/// <see cref="Outcomes"/> has one entry per finished action, in order; <see cref="Marker"/> is the sequence of the
/// <c>automation.applied</c> timeline marker. Identifiers and outcome tokens only — never bodies or attribute values.
/// </summary>
public sealed record AutomationExecution
{
    public const int MaximumTriggerKeyLength = 200;

    public AutomationExecution(Guid id, AutomationRuleId rule, ConversationId? conversation, string triggerKey, AutomationEventKind eventKind, int depth,
        AutomationExecutionStatus status, AutomationSuppression? suppression, DateTimeOffset startedAt, DateTimeOffset? completedAt, int actionCount,
        IEnumerable<AutomationActionOutcome> outcomes, long? marker = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An execution identifier is required.", nameof(id));
        if (!rule.IsInitialized)
            throw new ArgumentException("A rule identifier is required.", nameof(rule));
        if (conversation == default(ConversationId))
            throw new ArgumentException("The conversation must be initialized when supplied.", nameof(conversation));
        Id = id;
        Rule = rule;
        Conversation = conversation;
        TriggerKey = RequireTriggerKey(triggerKey, nameof(triggerKey));
        EventKind = AutomationText.Defined(eventKind, nameof(eventKind));
        if (depth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(depth));
        Depth = depth;
        Status = AutomationText.Defined(status, nameof(status));
        if ((status == AutomationExecutionStatus.Suppressed) != (suppression is not null) || suppression is { } value && !Enum.IsDefined(value))
            throw new ArgumentException("Suppressed executions, and only they, carry a suppression reason.", nameof(suppression));
        Suppression = suppression;
        StartedAt = AutomationText.Instant(startedAt);
        CompletedAt = AutomationText.Instant(completedAt);
        if ((status == AutomationExecutionStatus.Running) == (completedAt is not null))
            throw new ArgumentException("Finished executions, and only they, have a completion instant.", nameof(completedAt));
        if (actionCount is < 0 or > AutomationRule.MaximumActions)
            throw new ArgumentOutOfRangeException(nameof(actionCount));
        ActionCount = actionCount;
        ArgumentNullException.ThrowIfNull(outcomes);
        Outcomes = [.. outcomes];
        if (Outcomes.Count > actionCount || Outcomes.Any(outcome => outcome.Value is null))
            throw new ArgumentException("Outcomes cannot exceed the action count.", nameof(outcomes));
        if (marker is < 1)
            throw new ArgumentOutOfRangeException(nameof(marker));
        Marker = marker;
    }

    public Guid Id { get; }
    public AutomationRuleId Rule { get; }
    public ConversationId? Conversation { get; }
    public string TriggerKey { get; }
    public AutomationEventKind EventKind { get; }
    public int Depth { get; }
    public AutomationExecutionStatus Status { get; }
    public AutomationSuppression? Suppression { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public int ActionCount { get; }
    public IReadOnlyList<AutomationActionOutcome> Outcomes { get; }
    public long? Marker { get; }

    /// <summary>Every timeline sequence this execution produced (actions and marker), ascending.</summary>
    public IReadOnlyList<long> ProducedSequences
    {
        get
        {
            var count = Marker is not null ? 1 : 0;
            for (var i = 0; i < Outcomes.Count; i++)
                if (Outcomes[i].Value is AutomationActionApplied applied)
                    count += applied.Sequences.Count;
            if (count == 0)
                return [];
            var result = new long[count];
            var pos = 0;
            for (var i = 0; i < Outcomes.Count; i++)
                if (Outcomes[i].Value is AutomationActionApplied applied)
                    for (var j = 0; j < applied.Sequences.Count; j++)
                        result[pos++] = applied.Sequences[j];
            if (Marker is { } marker)
                result[pos] = marker;
            if (result.Length > 1)
                Array.Sort(result);
            return result;
        }
    }

    public AutomationExecution WithProgress(IEnumerable<AutomationActionOutcome> outcomes) =>
        new(Id, Rule, Conversation, TriggerKey, EventKind, Depth, Status, Suppression, StartedAt, CompletedAt, ActionCount, outcomes, Marker);

    public AutomationExecution Complete(DateTimeOffset completedAt, long? marker) =>
        new(Id, Rule, Conversation, TriggerKey, EventKind, Depth, AutomationExecutionStatus.Completed, null, StartedAt, completedAt, ActionCount, Outcomes, marker);

    /// <summary>Validates a trigger key: 1-200 printable ASCII characters.</summary>
    public static string RequireTriggerKey(string triggerKey, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(triggerKey, parameterName);
        if (triggerKey.Length is 0 or > MaximumTriggerKeyLength || triggerKey.Any(character => character is < '!' or > '~'))
            throw new ArgumentException($"Trigger keys are 1-{MaximumTriggerKeyLength} printable ASCII characters.", parameterName);
        return triggerKey;
    }

    public bool Equals(AutomationExecution? other) =>
        other is not null && Id == other.Id && Rule == other.Rule && Conversation == other.Conversation && TriggerKey == other.TriggerKey && EventKind == other.EventKind
        && Depth == other.Depth && Status == other.Status && Suppression == other.Suppression && StartedAt == other.StartedAt && CompletedAt == other.CompletedAt
        && ActionCount == other.ActionCount && Outcomes.SequenceEqual(other.Outcomes) && Marker == other.Marker;

    public override int GetHashCode() => HashCode.Combine(Id, Status, Outcomes.Count);

    public override string ToString() => $"AutomationExecution {{ Id = {Id}, Rule = {Rule}, Status = {Status} }}";
}

/// <summary>An execution with its scope and revision (first revision is 1).</summary>
public sealed record StoredAutomationExecution(TicketScope Scope, AutomationExecution Execution, long Revision)
{
    public Guid Id => Execution.Id;
}

/// <summary>Trigger key builders shared by the engine, scheduler and replay.</summary>
public static class AutomationTriggerKeys
{
    /// <summary>A timeline event of the conversation (<c>evt:&lt;sequence&gt;</c>).</summary>
    public static string TimelineEvent(long sequence) => string.Create(CultureInfo.InvariantCulture, $"evt:{sequence}");

    /// <summary>A started conversation without a triggering timeline event.</summary>
    public const string Started = "start";

    /// <summary>A scheduled fire slot (<c>sched:&lt;unix seconds&gt;</c>).</summary>
    public static string Scheduled(DateTimeOffset slot) => string.Create(CultureInfo.InvariantCulture, $"sched:{slot.ToUnixTimeSeconds()}");

    /// <summary>A contact change log entry (<c>contact:&lt;sequence&gt;</c>).</summary>
    public static string ContactChange(long sequence) => string.Create(CultureInfo.InvariantCulture, $"contact:{sequence}");
}
