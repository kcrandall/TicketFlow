using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>Clock lifecycle. Persisted numerically; never renumber.</summary>
public enum SlaClockState
{
    Running = 1,
    Paused = 2,
    Completed = 3,
}

/// <summary>How a completed clock ended. Persisted numerically; never renumber.</summary>
public enum SlaClockOutcome
{
    /// <summary>Satisfied within the target.</summary>
    Met = 1,

    /// <summary>Satisfied after the target was breached.</summary>
    Missed = 2,

    /// <summary>Stopped without being satisfied (for example resolved without a reply, or the rule no longer measures the metric).</summary>
    Cancelled = 3,
}

/// <summary>
/// One metric clock. <see cref="Elapsed"/> is the measured time accumulated before <see cref="RunningSince"/> (set only while
/// running); <see cref="DueAt"/> is the predicted breach instant while running and not yet breached. <see cref="FiredThresholds"/>
/// lists the threshold percentages (escalations and 100 = breach) already fired for this cycle. <see cref="Completions"/> counts
/// completions (a resolution clock completes again after a reopen).
/// </summary>
public sealed record SlaClock
{
    public SlaClock(SlaMetric metric, int cycle, DateTimeOffset startedAt, TimeSpan target, TimeSpan elapsed, DateTimeOffset? runningSince, SlaClockState state,
        DateTimeOffset? dueAt, DateTimeOffset? breachedAt, DateTimeOffset? completedAt, SlaClockOutcome? outcome, int completions, IEnumerable<int> firedThresholds)
    {
        Metric = SlaText.Defined(metric, nameof(metric));
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, 1);
        Cycle = cycle;
        StartedAt = SlaText.Instant(startedAt);
        if (target < SlaTargets.MinimumTarget || target > SlaTargets.MaximumTarget)
            throw new ArgumentOutOfRangeException(nameof(target));
        Target = target;
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        Elapsed = SlaText.Micros(elapsed);
        State = SlaText.Defined(state, nameof(state));
        if ((state == SlaClockState.Running) != (runningSince is not null))
            throw new ArgumentException("Only running clocks have a running-since instant.", nameof(runningSince));
        if ((state == SlaClockState.Completed) != (completedAt is not null && outcome is not null) || (outcome is { } value && !Enum.IsDefined(value)))
            throw new ArgumentException("Completed clocks, and only they, have a completion instant and outcome.", nameof(outcome));
        if (dueAt is not null && state != SlaClockState.Running)
            throw new ArgumentException("Only running clocks have a due instant.", nameof(dueAt));
        ArgumentOutOfRangeException.ThrowIfNegative(completions);
        RunningSince = SlaText.Instant(runningSince);
        DueAt = SlaText.Instant(dueAt);
        BreachedAt = SlaText.Instant(breachedAt);
        CompletedAt = SlaText.Instant(completedAt);
        Outcome = outcome;
        Completions = completions;
        ArgumentNullException.ThrowIfNull(firedThresholds);
        FiredThresholds = [.. firedThresholds.Distinct().Order()];
        if (FiredThresholds.Any(threshold => threshold is < 1 or > 100) || FiredThresholds.Count > SlaRule.MaximumEscalations + 1)
            throw new ArgumentException("Fired thresholds are 1-100.", nameof(firedThresholds));
    }

    public SlaMetric Metric { get; }

    /// <summary>1 for first response and resolution; increments per next-response cycle.</summary>
    public int Cycle { get; }

    public DateTimeOffset StartedAt { get; }
    public TimeSpan Target { get; }
    public TimeSpan Elapsed { get; }
    public DateTimeOffset? RunningSince { get; }
    public SlaClockState State { get; }
    public DateTimeOffset? DueAt { get; }
    public DateTimeOffset? BreachedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public SlaClockOutcome? Outcome { get; }
    public int Completions { get; }
    public IReadOnlyList<int> FiredThresholds { get; }

    public bool IsActive => State != SlaClockState.Completed;

    public bool Equals(SlaClock? other) =>
        other is not null && Metric == other.Metric && Cycle == other.Cycle && StartedAt == other.StartedAt && Target == other.Target && Elapsed == other.Elapsed
        && RunningSince == other.RunningSince && State == other.State && DueAt == other.DueAt && BreachedAt == other.BreachedAt && CompletedAt == other.CompletedAt
        && Outcome == other.Outcome && Completions == other.Completions && FiredThresholds.SequenceEqual(other.FiredThresholds);

    public override int GetHashCode() => HashCode.Combine(Metric, Cycle, State, Elapsed);
}

/// <summary>A paused interval (open while <see cref="End"/> is <see langword="null"/>).</summary>
public sealed record SlaPauseInterval
{
    public SlaPauseInterval(DateTimeOffset start, DateTimeOffset? end)
    {
        Start = SlaText.Instant(start);
        End = SlaText.Instant(end);
        if (End < Start)
            throw new ArgumentException("A pause cannot end before it starts.", nameof(end));
    }

    public DateTimeOffset Start { get; }
    public DateTimeOffset? End { get; }
}

/// <summary>
/// A timeline event recorded in the tracker before it is appended to the conversation (so a crash between the two never loses it).
/// <see cref="Key"/> is unique per tracker and carried in the payload; <see cref="AfterSequence"/> is the conversation head when the
/// emission was recorded, so a retry can look for an already appended copy after it.
/// </summary>
public sealed record SlaPendingEmission
{
    public SlaPendingEmission(string key, SlaTimelineEvent timelineEvent, long afterSequence)
    {
        Key = SlaText.RequireText(key, SlaEventCodec.MaximumKeyLength, nameof(key));
        if (timelineEvent.Value is null)
            throw new ArgumentException("An event is required.", nameof(timelineEvent));
        Event = timelineEvent;
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        AfterSequence = afterSequence;
    }

    public string Key { get; }
    public SlaTimelineEvent Event { get; }
    public long AfterSequence { get; }
}

/// <summary>The rule a tracker applies: a snapshot of the matched policy rule, its pause policy, risk threshold and calendar.</summary>
public sealed record SlaAppliedRule
{
    public SlaAppliedRule(SlaPolicyId policy, long policyRevision, int ruleIndex, SlaRule rule, SlaPausePolicy pause, int atRiskPercent, BusinessCalendarId? calendar)
    {
        if (!policy.IsInitialized || calendar is { IsInitialized: false })
            throw new ArgumentException("Policy and calendar identifiers must be initialized.", !policy.IsInitialized ? nameof(policy) : nameof(calendar));
        ArgumentOutOfRangeException.ThrowIfLessThan(policyRevision, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(ruleIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ruleIndex, SlaPolicy.MaximumRules);
        ArgumentOutOfRangeException.ThrowIfLessThan(atRiskPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(atRiskPercent, 99);
        Policy = policy;
        PolicyRevision = policyRevision;
        RuleIndex = ruleIndex;
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        Pause = pause ?? throw new ArgumentNullException(nameof(pause));
        AtRiskPercent = atRiskPercent;
        Calendar = calendar;
    }

    public SlaPolicyId Policy { get; }
    public long PolicyRevision { get; }
    public int RuleIndex { get; }
    public SlaRule Rule { get; }
    public SlaPausePolicy Pause { get; }
    public int AtRiskPercent { get; }

    /// <summary>The inbox calendar at application; <see langword="null"/> measures calendar time.</summary>
    public BusinessCalendarId? Calendar { get; }
}

/// <summary>The conversation facts the clocks depend on, as of <see cref="SlaTracker.LastSequence"/>.</summary>
public sealed record SlaConversationFacts
{
    public SlaConversationFacts(ConversationStatusKind status, DateTimeOffset? snoozedUntil, ConversationPriority priority, DateTimeOffset? waitingSince, bool replied)
    {
        Status = SlaText.Defined(status, nameof(status));
        if ((status == ConversationStatusKind.Snoozed) != (snoozedUntil is not null))
            throw new ArgumentException("Only snoozed conversations have a snooze instant.", nameof(snoozedUntil));
        SnoozedUntil = SlaText.Instant(snoozedUntil);
        Priority = SlaText.Defined(priority, nameof(priority));
        WaitingSince = SlaText.Instant(waitingSince);
        Replied = replied;
    }

    public ConversationStatusKind Status { get; }
    public DateTimeOffset? SnoozedUntil { get; }
    public ConversationPriority Priority { get; }

    /// <summary>The oldest unanswered incoming message, if any.</summary>
    public DateTimeOffset? WaitingSince { get; }

    /// <summary>True once an outgoing reply was recorded.</summary>
    public bool Replied { get; }
}

/// <summary>
/// The Access-relevant fields of the conversation (team, assignee, labels and attribute condition values), mirrored so risk lists can
/// apply <see cref="AccessFilter"/>s inside the store query. <see cref="Resource"/> equals <c>ConversationAccess.Resource</c> of the
/// mirrored conversation.
/// </summary>
public sealed record SlaAccessSnapshot
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes = new SortedDictionary<string, string>(StringComparer.Ordinal).AsReadOnly();

    public SlaAccessSnapshot(TeamId? team, UserId? assignee, IEnumerable<LabelId> labels, IEnumerable<KeyValuePair<string, string>> attributes)
    {
        if (team == default(TeamId) || assignee == default(UserId))
            throw new ArgumentException("Team and assignee must be initialized when supplied.", team == default(TeamId) ? nameof(team) : nameof(assignee));
        Team = team;
        Assignee = assignee;
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(attributes);
        Labels = [.. labels.Distinct().OrderBy(label => label.Value, StringComparer.Ordinal)];
        if (Labels.Count > Conversation.MaximumLabels || Labels.Any(label => label == default))
            throw new ArgumentException("The access snapshot exceeds conversation limits.", nameof(labels));
        SortedDictionary<string, string>? sorted = null;
        foreach (var pair in attributes)
        {
            sorted ??= new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!sorted.TryAdd(pair.Key, pair.Value))
                throw new ArgumentException("Duplicate attribute key.", nameof(attributes));
            if (sorted.Count > Conversation.MaximumAttributes)
                throw new ArgumentException("The access snapshot exceeds conversation limits.", nameof(attributes));
            if (pair.Key.Length is 0 or > AccessResource.MaximumAttributeKeyLength || pair.Value is null || pair.Value.Length > AccessResource.MaximumAttributeValueLength)
                throw new ArgumentException("Attribute condition values exceed Access limits.", nameof(attributes));
        }
        Attributes = sorted is null ? EmptyAttributes : sorted.AsReadOnly();
    }

    public TeamId? Team { get; }
    public UserId? Assignee { get; }
    public IReadOnlyList<LabelId> Labels { get; }

    /// <summary>Attribute condition values keyed by attribute key (ordinal order). May contain personal data; never logged.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }

    public static SlaAccessSnapshot From(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Attributes.Count == 0)
            return new(conversation.Team, conversation.Assignee, conversation.Labels, EmptyAttributes);
        List<KeyValuePair<string, string>>? attrs = null;
        foreach (var pair in conversation.Attributes)
        {
            if (pair.Value.ConditionValue is { } cond)
            {
                attrs ??= new List<KeyValuePair<string, string>>(conversation.Attributes.Count);
                attrs.Add(new KeyValuePair<string, string>(pair.Key, cond));
            }
        }
        return new(conversation.Team, conversation.Assignee, conversation.Labels, attrs is null ? EmptyAttributes : attrs);
    }

    public AccessResource Resource(TicketScope scope, ConversationId conversation, InboxId inbox) =>
        new(scope, conversation.Value, Team, inbox, Assignee, Labels, Attributes);

    public bool Equals(SlaAccessSnapshot? other) =>
        other is not null && Team == other.Team && Assignee == other.Assignee && Labels.SequenceEqual(other.Labels)
        && Attributes.Count == other.Attributes.Count && Attributes.All(pair => other.Attributes.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Team, Assignee, Labels.Count, Attributes.Count);

    public override string ToString() => "SlaAccessSnapshot { <redacted> }";
}

/// <summary>
/// The tracker's derived schedule, persisted as indexed columns. <see cref="NextCheckAt"/> is the durable timer (the earliest
/// threshold, snooze expiry or pending emission); <see cref="DueAt"/> the earliest predicted breach of a running clock;
/// <see cref="AtRiskAt"/> the earliest instant a running, unbreached clock reaches the at-risk threshold; <see cref="Breached"/> is
/// true while an active clock is breached; <see cref="UrgencyAt"/> orders risk lists (the earlier of <see cref="DueAt"/> and the
/// first breach of an active clock).
/// </summary>
public sealed record SlaSchedule
{
    public SlaSchedule(DateTimeOffset? nextCheckAt, DateTimeOffset? dueAt, DateTimeOffset? atRiskAt, bool breached, DateTimeOffset? urgencyAt)
    {
        NextCheckAt = SlaText.Instant(nextCheckAt);
        DueAt = SlaText.Instant(dueAt);
        AtRiskAt = SlaText.Instant(atRiskAt);
        Breached = breached;
        UrgencyAt = SlaText.Instant(urgencyAt);
        if ((breached || dueAt is not null) != (urgencyAt is not null) || (atRiskAt is not null && dueAt is null))
            throw new ArgumentException("Urgency exists exactly when a clock is due or breached; at-risk clocks are due.", nameof(urgencyAt));
    }

    public static SlaSchedule None { get; } = new(null, null, null, false, null);

    public DateTimeOffset? NextCheckAt { get; }
    public DateTimeOffset? DueAt { get; }
    public DateTimeOffset? AtRiskAt { get; }
    public bool Breached { get; }
    public DateTimeOffset? UrgencyAt { get; }

    /// <summary>True when an active clock is breached or a running clock is past due at <paramref name="now"/>.</summary>
    public bool IsBreached(DateTimeOffset now) => Breached || DueAt <= now;

    /// <summary>True when not breached and a running clock passed its at-risk threshold at <paramref name="now"/>.</summary>
    public bool IsAtRisk(DateTimeOffset now) => !IsBreached(now) && AtRiskAt <= now;
}

/// <summary>
/// The SLA state of one conversation: the applied rule (if any), the conversation facts and clocks as of <see cref="LastSequence"/>,
/// bounded pause history (newest <see cref="MaximumPauses"/>), pending timeline emissions, the mirrored Access snapshot and the
/// derived schedule.
/// </summary>
public sealed record SlaTracker
{
    public const int MaximumPauses = 32;
    public const int MaximumPending = 64;

    public SlaTracker(
        ConversationId conversation,
        InboxId inbox,
        SlaAppliedRule? applied,
        SlaConversationFacts facts,
        IEnumerable<SlaClock> clocks,
        IEnumerable<SlaPauseInterval> pauses,
        IEnumerable<SlaPendingEmission> pending,
        long emissionCount,
        SlaAccessSnapshot access,
        long lastSequence,
        DateTimeOffset evaluatedAt,
        SlaSchedule schedule,
        DateTimeOffset? flushRetryAt = null)
    {
        if (conversation == default || inbox == default)
            throw new ArgumentException("Conversation and inbox identifiers are required.", conversation == default ? nameof(conversation) : nameof(inbox));
        Conversation = conversation;
        Inbox = inbox;
        Applied = applied;
        Facts = facts ?? throw new ArgumentNullException(nameof(facts));
        ArgumentNullException.ThrowIfNull(clocks);
        Clocks = [.. clocks.OrderBy(clock => clock?.Metric)];
        if (Clocks.Any(clock => clock is null) || Clocks.Select(clock => clock.Metric).Distinct().Count() != Clocks.Count)
            throw new ArgumentException("A tracker has at most one clock per metric.", nameof(clocks));
        ArgumentNullException.ThrowIfNull(pauses);
        Pauses = [.. pauses];
        if (Pauses.Count > MaximumPauses || Pauses.Any(pause => pause is null))
            throw new ArgumentException($"A tracker keeps at most {MaximumPauses} pauses.", nameof(pauses));
        ArgumentNullException.ThrowIfNull(pending);
        Pending = [.. pending];
        if (Pending.Count > MaximumPending || Pending.Any(item => item is null) || Pending.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != Pending.Count)
            throw new ArgumentException($"A tracker holds at most {MaximumPending} distinct pending emissions.", nameof(pending));
        ArgumentOutOfRangeException.ThrowIfNegative(emissionCount);
        EmissionCount = emissionCount;
        Access = access ?? throw new ArgumentNullException(nameof(access));
        ArgumentOutOfRangeException.ThrowIfNegative(lastSequence);
        LastSequence = lastSequence;
        EvaluatedAt = SlaText.Instant(evaluatedAt);
        Schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        FlushRetryAt = SlaText.Instant(flushRetryAt);
        if (FlushRetryAt is not null && Pending.Count == 0)
            throw new ArgumentException("Only trackers with pending emissions defer flushing.", nameof(flushRetryAt));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public SlaAppliedRule? Applied { get; }
    public SlaConversationFacts Facts { get; }

    /// <summary>At most one clock per metric (the latest cycle), ordered by metric.</summary>
    public IReadOnlyList<SlaClock> Clocks { get; }

    public IReadOnlyList<SlaPauseInterval> Pauses { get; }
    public IReadOnlyList<SlaPendingEmission> Pending { get; }

    /// <summary>Emissions ever recorded; the source of unique emission keys.</summary>
    public long EmissionCount { get; }

    public SlaAccessSnapshot Access { get; }

    /// <summary>The newest conversation timeline sequence applied to the clocks.</summary>
    public long LastSequence { get; }

    /// <summary>The instant the clocks were last advanced to.</summary>
    public DateTimeOffset EvaluatedAt { get; }

    public SlaSchedule Schedule { get; }

    /// <summary>
    /// When a failed flush (refused append, too many conversation conflicts, or an undecidable duplicate scan) may be retried; until then
    /// pending emissions do not keep the timer due. <see langword="null"/> when the last flush did not fail.
    /// </summary>
    public DateTimeOffset? FlushRetryAt { get; }

    public SlaClock? Clock(SlaMetric metric) => Clocks.FirstOrDefault(clock => clock.Metric == metric);

    public AccessResource Resource(TicketScope scope) => Access.Resource(scope, Conversation, Inbox);

    public override string ToString() => $"SlaTracker {{ Conversation = {Conversation}, Clocks = {Clocks.Count} }}";
}

/// <summary>A tracker with its scope and revision (first revision is 1).</summary>
public sealed record StoredSlaTracker(TicketScope Scope, SlaTracker Tracker, long Revision)
{
    public ConversationId Conversation => Tracker.Conversation;
}
