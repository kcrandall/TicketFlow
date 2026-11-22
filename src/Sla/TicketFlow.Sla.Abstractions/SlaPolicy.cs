using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>Measured service-level metrics. Persisted numerically; never renumber.</summary>
public enum SlaMetric
{
    /// <summary>From the first incoming message until the first outgoing reply.</summary>
    FirstResponse = 1,

    /// <summary>From each later unanswered incoming message (after the first reply) until the next outgoing reply.</summary>
    NextResponse = 2,

    /// <summary>From application until the conversation is resolved or closed; resumes when it is reopened.</summary>
    Resolution = 3,
}

/// <summary>How a rule measures durations. Persisted numerically; never renumber.</summary>
public enum SlaClockBasis
{
    /// <summary>Open time of the inbox's business calendar (calendar time when the inbox has none).</summary>
    BusinessHours = 1,

    /// <summary>Elapsed wall-clock time.</summary>
    CalendarTime = 2,
}

/// <summary>Notify a user (an E7 notification consumer delivers it).</summary>
public sealed record NotifyUserAction
{
    public NotifyUserAction(UserId user) => User = user == default ? throw new ArgumentException("A user is required.", nameof(user)) : user;

    public UserId User { get; }
}

/// <summary>Notify every member of a team.</summary>
public sealed record NotifyTeamAction
{
    public NotifyTeamAction(TeamId team) => Team = team == default ? throw new ArgumentException("A team is required.", nameof(team)) : team;

    public TeamId Team { get; }
}

/// <summary>Raise the conversation priority to at least <see cref="Priority"/> (E10 automation applies it).</summary>
public sealed record RaisePriorityAction
{
    public RaisePriorityAction(ConversationPriority priority) => Priority = SlaText.Defined(priority, nameof(priority));

    public ConversationPriority Priority { get; }
}

/// <summary>Move the conversation to <see cref="Team"/> (E10 automation applies it).</summary>
public sealed record ReassignTeamAction
{
    public ReassignTeamAction(TeamId team) => Team = team == default ? throw new ArgumentException("A team is required.", nameof(team)) : team;

    public TeamId Team { get; }
}

/// <summary>
/// An escalation action. SLA packages never execute actions: they are recorded in <c>sla.warning</c>/<c>sla.breached</c> timeline
/// events for automation (E10) and notifications (E7). Switch over every case without a discard arm; persisted through
/// <see cref="SlaPolicyCodec"/> with explicit discriminators.
/// </summary>
public union SlaAction(NotifyUserAction, NotifyTeamAction, RaisePriorityAction, ReassignTeamAction);

/// <summary>Actions to record when a metric's elapsed time reaches <see cref="ThresholdPercent"/> of its target (100 = breach).</summary>
public sealed record SlaEscalation
{
    public const int MaximumActions = 8;

    public SlaEscalation(SlaMetric metric, int thresholdPercent, IEnumerable<SlaAction> actions)
    {
        Metric = SlaText.Defined(metric, nameof(metric));
        ArgumentOutOfRangeException.ThrowIfLessThan(thresholdPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(thresholdPercent, 100);
        ThresholdPercent = thresholdPercent;
        ArgumentNullException.ThrowIfNull(actions);
        Actions = [.. actions];
        if (Actions.Count is < 1 or > MaximumActions || Actions.Any(action => action.Value is null))
            throw new ArgumentException($"An escalation has 1-{MaximumActions} actions.", nameof(actions));
    }

    public SlaMetric Metric { get; }
    public int ThresholdPercent { get; }
    public IReadOnlyList<SlaAction> Actions { get; }

    public bool Equals(SlaEscalation? other) =>
        other is not null && Metric == other.Metric && ThresholdPercent == other.ThresholdPercent && Actions.SequenceEqual(other.Actions);

    public override int GetHashCode() => HashCode.Combine(Metric, ThresholdPercent, Actions.Count);
}

/// <summary>Targets per metric; <see langword="null"/> means the metric is not measured. At least one target is required.</summary>
public sealed record SlaTargets
{
    public static TimeSpan MinimumTarget { get; } = TimeSpan.FromMinutes(1);
    public static TimeSpan MaximumTarget { get; } = TimeSpan.FromDays(365);

    public SlaTargets(TimeSpan? firstResponse = null, TimeSpan? nextResponse = null, TimeSpan? resolution = null)
    {
        FirstResponse = Require(firstResponse, nameof(firstResponse));
        NextResponse = Require(nextResponse, nameof(nextResponse));
        Resolution = Require(resolution, nameof(resolution));
        if (FirstResponse is null && NextResponse is null && Resolution is null)
            throw new ArgumentException("At least one target is required.");
    }

    public TimeSpan? FirstResponse { get; }
    public TimeSpan? NextResponse { get; }
    public TimeSpan? Resolution { get; }

    public TimeSpan? For(SlaMetric metric) => metric switch
    {
        SlaMetric.FirstResponse => FirstResponse,
        SlaMetric.NextResponse => NextResponse,
        SlaMetric.Resolution => Resolution,
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    private static TimeSpan? Require(TimeSpan? target, string parameterName)
    {
        if (target is not { } value)
            return null;
        if (value < MinimumTarget || value > MaximumTarget || value.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(parameterName, "Targets are whole seconds between one minute and 365 days.");
        return value;
    }
}

/// <summary>
/// When a rule applies: the conversation priority is one of <see cref="Priorities"/> (empty = any) and it carries every label in
/// <see cref="Labels"/> (empty = any).
/// </summary>
public sealed record SlaCondition
{
    public const int MaximumLabels = 16;

    public SlaCondition(IEnumerable<ConversationPriority>? priorities = null, IEnumerable<LabelId>? labels = null)
    {
        Priorities = [.. (priorities ?? []).Select(priority => SlaText.Defined(priority, nameof(priorities))).Distinct().Order()];
        Labels = [.. (labels ?? []).Distinct().OrderBy(label => label.Value, StringComparer.Ordinal)];
        if (Labels.Any(label => label == default) || Labels.Count > MaximumLabels)
            throw new ArgumentException($"Conditions name at most {MaximumLabels} initialized labels.", nameof(labels));
    }

    /// <summary>Matches every conversation.</summary>
    public static SlaCondition Always { get; } = new();

    public IReadOnlyList<ConversationPriority> Priorities { get; }
    public IReadOnlyList<LabelId> Labels { get; }

    public bool Matches(ConversationPriority priority, IEnumerable<LabelId> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (Priorities.Count > 0 && !Priorities.Contains(priority))
            return false;
        if (Labels.Count == 0)
            return true;
        var present = labels as IReadOnlyCollection<LabelId> ?? [.. labels];
        return Labels.All(present.Contains);
    }

    public bool Equals(SlaCondition? other) => other is not null && Priorities.SequenceEqual(other.Priorities) && Labels.SequenceEqual(other.Labels);

    public override int GetHashCode() => HashCode.Combine(Priorities.Count, Labels.Count);
}

/// <summary>One ordered policy rule: condition, targets, clock basis and escalations (at most one per metric and threshold).</summary>
public sealed record SlaRule
{
    public const int MaximumNameLength = 128;
    public const int MaximumEscalations = 16;

    public SlaRule(string name, SlaCondition condition, SlaTargets targets, SlaClockBasis basis = SlaClockBasis.BusinessHours, IEnumerable<SlaEscalation>? escalations = null)
    {
        Name = SlaText.RequireText(name, MaximumNameLength, nameof(name));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        Basis = SlaText.Defined(basis, nameof(basis));
        Escalations = [.. (escalations ?? []).OrderBy(escalation => escalation?.Metric).ThenBy(escalation => escalation?.ThresholdPercent)];
        if (Escalations.Count > MaximumEscalations || Escalations.Any(escalation => escalation is null))
            throw new ArgumentException($"A rule has at most {MaximumEscalations} escalations.", nameof(escalations));
        if (Escalations.Select(escalation => (escalation.Metric, escalation.ThresholdPercent)).Distinct().Count() != Escalations.Count)
            throw new ArgumentException("Escalations must be unique per metric and threshold.", nameof(escalations));
        if (Escalations.Any(escalation => Targets.For(escalation.Metric) is null))
            throw new ArgumentException("Escalations require a target for their metric.", nameof(escalations));
    }

    public string Name { get; }
    public SlaCondition Condition { get; }
    public SlaTargets Targets { get; }
    public SlaClockBasis Basis { get; }

    /// <summary>Escalations ordered by metric, then threshold.</summary>
    public IReadOnlyList<SlaEscalation> Escalations { get; }

    /// <summary>Thresholds tracked for <paramref name="metric"/>: every escalation threshold plus 100 (breach), ascending.</summary>
    public IReadOnlyList<int> Thresholds(SlaMetric metric) =>
        [.. Escalations.Where(escalation => escalation.Metric == metric).Select(escalation => escalation.ThresholdPercent).Append(100).Distinct().Order()];

    /// <summary>The actions of the escalation at exactly <paramref name="thresholdPercent"/>, or none.</summary>
    public IReadOnlyList<SlaAction> ActionsAt(SlaMetric metric, int thresholdPercent) =>
        Escalations.FirstOrDefault(escalation => escalation.Metric == metric && escalation.ThresholdPercent == thresholdPercent)?.Actions ?? [];

    public bool Equals(SlaRule? other) =>
        other is not null && Name == other.Name && Condition.Equals(other.Condition) && Targets.Equals(other.Targets) && Basis == other.Basis
        && Escalations.SequenceEqual(other.Escalations);

    public override int GetHashCode() => HashCode.Combine(Name, Basis, Escalations.Count);
}

/// <summary>Which conversation statuses pause running clocks. A snooze pauses only until its <c>Until</c> instant.</summary>
public sealed record SlaPausePolicy(bool PauseWhenPending = true, bool PauseWhenSnoozed = true)
{
    public static SlaPausePolicy Default { get; } = new();

    /// <summary>True when <paramref name="status"/> pauses clocks at <paramref name="at"/>.</summary>
    public bool Pauses(ConversationStatusKind status, DateTimeOffset? snoozedUntil, DateTimeOffset at) => status switch
    {
        ConversationStatusKind.Pending => PauseWhenPending,
        ConversationStatusKind.Snoozed => PauseWhenSnoozed && (snoozedUntil is not { } until || at < until),
        ConversationStatusKind.Open or ConversationStatusKind.Resolved or ConversationStatusKind.Closed => false,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

/// <summary>
/// A workspace SLA policy, applied to conversations of every inbox whose <see cref="Inbox.SlaPolicy"/> references it. Rules are
/// evaluated in order and the first whose condition matches applies; a conversation matching no rule has no SLA. A clock is at risk
/// once its elapsed time reaches <see cref="AtRiskPercent"/> of the target.
/// </summary>
public sealed record SlaPolicy
{
    public const int MaximumNameLength = 128;
    public const int MaximumRules = 32;
    public const int DefaultAtRiskPercent = 75;

    public SlaPolicy(SlaPolicyId id, string name, IEnumerable<SlaRule> rules, SlaPausePolicy? pause = null, int atRiskPercent = DefaultAtRiskPercent)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A policy identifier is required.", nameof(id));
        Id = id;
        Name = SlaText.RequireText(name, MaximumNameLength, nameof(name));
        ArgumentNullException.ThrowIfNull(rules);
        Rules = [.. rules];
        if (Rules.Count is < 1 or > MaximumRules || Rules.Any(rule => rule is null))
            throw new ArgumentException($"A policy has 1-{MaximumRules} rules.", nameof(rules));
        Pause = pause ?? SlaPausePolicy.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(atRiskPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(atRiskPercent, 99);
        AtRiskPercent = atRiskPercent;
    }

    public SlaPolicyId Id { get; }
    public string Name { get; }
    public IReadOnlyList<SlaRule> Rules { get; }
    public SlaPausePolicy Pause { get; }
    public int AtRiskPercent { get; }

    /// <summary>The index of the first rule matching <paramref name="priority"/> and <paramref name="labels"/>, or <see langword="null"/>.</summary>
    public int? Match(ConversationPriority priority, IEnumerable<LabelId> labels)
    {
        IReadOnlyCollection<LabelId> present = [.. labels ?? throw new ArgumentNullException(nameof(labels))];
        for (var index = 0; index < Rules.Count; index++)
            if (Rules[index].Condition.Matches(priority, present))
                return index;
        return null;
    }

    public bool Equals(SlaPolicy? other) =>
        other is not null && Id == other.Id && Name == other.Name && Rules.SequenceEqual(other.Rules) && Pause == other.Pause && AtRiskPercent == other.AtRiskPercent;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Rules.Count, AtRiskPercent);

    public override string ToString() => $"SlaPolicy {{ Id = {Id}, Rules = {Rules.Count} }}";
}

/// <summary>A policy with its scope and revision (first revision is 1).</summary>
public sealed record StoredSlaPolicy(TicketScope Scope, SlaPolicy Policy, long Revision);
