using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>
/// The deterministic SLA state machine. It replays conversation timeline facts in order, advancing every clock to each fact's
/// instant (firing thresholds whose crossing instant has passed, in instant order) before applying the fact, and records timeline
/// emissions as pending. Facts older than the last evaluation are applied at the last evaluation instant. All measured time goes
/// through <see cref="SlaClockTime"/>.
/// </summary>
public sealed class SlaEngine
{
    private readonly ConversationId conversation;
    private readonly InboxId inbox;
    private readonly Func<BusinessCalendarId?, SlaClockTime?> calendars;
    private readonly Clock?[] clocks = new Clock?[3];
    private readonly List<SlaPauseInterval> pauses = [];
    private readonly List<SlaPendingEmission> pending = [];
    private readonly SortedSet<string> labels = new(StringComparer.Ordinal);
    private SlaConversationFacts facts;
    private SlaAppliedRule? applied;
    private SlaClockTime? cachedTime;

    private SlaEngine(ConversationId conversation, InboxId inbox, SlaConversationFacts facts, Func<BusinessCalendarId?, SlaClockTime?> calendars)
    {
        this.conversation = conversation;
        this.inbox = inbox;
        this.facts = facts;
        this.calendars = calendars;
    }

    /// <summary>The rule in force, if any.</summary>
    public SlaAppliedRule? Applied
    {
        get => applied;
        private set
        {
            applied = value;
            cachedTime = null;
        }
    }

    /// <summary>The policy (and inbox calendar) used when a priority or label change re-matches rules.</summary>
    public StoredSlaPolicy? Policy { get; set; }

    public BusinessCalendarId? InboxCalendar { get; set; }

    /// <summary>The conversation head when this run started; recorded on new emissions for duplicate detection.</summary>
    public long Head { get; set; }

    public long LastSequence { get; private set; }
    public DateTimeOffset EvaluatedAt { get; private set; }
    public long EmissionCount { get; private set; }

    /// <summary>When a failed flush may be retried (see <see cref="SlaTracker.FlushRetryAt"/>).</summary>
    public DateTimeOffset? FlushRetryAt { get; private set; }
    public SlaConversationFacts Facts => facts;
    public IReadOnlyList<SlaPendingEmission> Pending => pending;

    private bool IsPaused => pauses.Count > 0 && pauses[^1].End is null;

    /// <summary>The time basis of the applied rule (calendar time without a rule, calendar or a resolvable business calendar).</summary>
    public SlaClockTime Time => cachedTime ??= applied is null || applied.Rule.Basis == SlaClockBasis.CalendarTime || applied.Calendar is null
        ? SlaClockTime.Calendar
        : calendars(applied.Calendar) ?? SlaClockTime.Calendar;

    /// <summary>A fresh engine for a conversation created at <paramref name="createdAt"/> with the given initial facts.</summary>
    public static SlaEngine Create(ConversationId conversation, InboxId inbox, SlaConversationFacts initial, IEnumerable<LabelId> labels, DateTimeOffset createdAt,
        long lastSequence, Func<BusinessCalendarId?, SlaClockTime?> calendars)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(calendars);
        var engine = new SlaEngine(conversation, inbox, initial, calendars) { EvaluatedAt = SlaMath.Instant(createdAt), LastSequence = lastSequence };
        engine.SetLabels(labels);
        return engine;
    }

    /// <summary>An engine resuming <paramref name="tracker"/>.</summary>
    public static SlaEngine From(SlaTracker tracker, Func<BusinessCalendarId?, SlaClockTime?> calendars)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(calendars);
        var engine = new SlaEngine(tracker.Conversation, tracker.Inbox, tracker.Facts, calendars)
        {
            Applied = tracker.Applied,
            LastSequence = tracker.LastSequence,
            EvaluatedAt = tracker.EvaluatedAt,
            EmissionCount = tracker.EmissionCount,
            FlushRetryAt = tracker.FlushRetryAt,
        };
        for (var i = 0; i < tracker.Clocks.Count; i++)
        {
            var record = tracker.Clocks[i];
            engine.clocks[Slot(record.Metric)] = Clock.From(record);
        }
        engine.pauses.AddRange(tracker.Pauses);
        engine.pending.AddRange(tracker.Pending);
        engine.SetLabels(tracker.Access.Labels);
        return engine;
    }

    /// <summary>Replaces the label set used for rule matching (for example with the current conversation labels).</summary>
    public void SetLabels(IEnumerable<LabelId> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        labels.Clear();
        foreach (var label in values)
            labels.Add(label.Value);
    }

    /// <summary>Re-matches the policy at <paramref name="at"/> (initial application or after priority/label changes).</summary>
    public void Rematch(DateTimeOffset at)
    {
        var t = Clamp(at);
        AdvanceTo(t);
        SlaAppliedRule? next = null;
        if (Policy is { } stored && MatchPolicy(stored.Policy, facts.Priority, labels) is { } index)
        {
            var policy = stored.Policy;
            next = new SlaAppliedRule(policy.Id, stored.Revision, index, policy.Rules[index], policy.Pause, policy.AtRiskPercent, InboxCalendar);
        }
        if (Same(applied, next))
            return;
        ApplyRule(next, t);
        AdvanceTo(t);
    }

    private static int? MatchPolicy(SlaPolicy policy, ConversationPriority priority, SortedSet<string> labelValues)
    {
        for (var i = 0; i < policy.Rules.Count; i++)
        {
            var cond = policy.Rules[i].Condition;
            if (!cond.MatchesPriority(priority))
                continue;
            var matched = true;
            for (var j = 0; j < cond.Labels.Count; j++)
            {
                if (!labelValues.Contains(cond.Labels[j].Value))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
                return i;
        }
        return null;
    }

    /// <summary>Applies one timeline event (events at or before <see cref="LastSequence"/> are ignored).</summary>
    public void Apply(TimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);
        if (timelineEvent.Sequence <= LastSequence)
            return;
        var t = Clamp(timelineEvent.OccurredAt);
        AdvanceTo(t);
        var mutated = true;
        switch (timelineEvent.Event)
        {
            case MessageEvent message when message.Direction == MessageDirection.Incoming:
                Incoming(t);
                break;
            case MessageEvent:
                Outgoing(t);
                break;
            case StatusChanged status:
                Status(status.Current, t);
                break;
            case PriorityChanged priority:
                facts = new(facts.Status, facts.SnoozedUntil, priority.Current, facts.WaitingSince, facts.Replied);
                Rematch(t);
                mutated = false;
                break;
            case LabelsChanged changed:
                for (var i = 0; i < changed.Added.Count; i++)
                    labels.Add(changed.Added[i].Value);
                for (var i = 0; i < changed.Removed.Count; i++)
                    labels.Remove(changed.Removed[i].Value);
                Rematch(t);
                mutated = false;
                break;
            case NoteEvent or AssignmentChanged or TeamChanged or AttributeChanged or ParticipantChanged or ContactChanged or SystemEvent or ExtensionEvent:
                mutated = false;
                break;
        }
        LastSequence = timelineEvent.Sequence;
        if (mutated)
            AdvanceTo(t);
    }

    /// <summary>
    /// Adopts a conversation first seen mid-life from its snapshot facts: clocks start at the waiting/creation instants and overdue
    /// thresholds fire at their crossing instants.
    /// </summary>
    public void Adopt(Conversation snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        facts = new(snapshot.Status.Kind, snapshot.Status.SnoozedUntil, snapshot.Priority, snapshot.WaitingSince, snapshot.FirstReplyAt is not null);
        LastSequence = snapshot.LastSequence;
        SetLabels(snapshot.Labels);
        Rematch(snapshot.CreatedAt);
    }

    /// <summary>Advances every clock to <paramref name="at"/>, firing due thresholds and resuming expired snoozes.</summary>
    public void AdvanceTo(DateTimeOffset at)
    {
        var target = SlaMath.Instant(at);
        if (applied is not null)
        {
            while (true)
            {
                // An expired snooze resumes paused clocks at its Until instant, before later thresholds are considered.
                if (IsPaused && facts.SnoozedUntil is { } until && until <= target && !applied.Pause.Pauses(facts.Status, facts.SnoozedUntil, until))
                {
                    var resumeAt = until > EvaluatedAt ? until : EvaluatedAt;
                    FireUntil(resumeAt);
                    Resume(resumeAt);
                    continue;
                }
                if (!FireNext(target))
                    break;
            }
        }
        if (target > EvaluatedAt)
            EvaluatedAt = target;
    }

    /// <summary>Removes flushed pending emissions.</summary>
    public void Acknowledge(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var flushed = keys as HashSet<string> ?? new HashSet<string>(keys, StringComparer.Ordinal);
        if (flushed.Count > 0)
            pending.RemoveAll(item => flushed.Contains(item.Key));
        if (pending.Count == 0)
            FlushRetryAt = null;
    }

    /// <summary>Defers flushing the remaining pending emissions until <paramref name="retryAt"/> (no effect without pending emissions).</summary>
    public void DeferFlush(DateTimeOffset retryAt) => FlushRetryAt = pending.Count == 0 ? null : SlaMath.Instant(retryAt);

    /// <summary>The immutable tracker with its derived schedule.</summary>
    public SlaTracker ToTracker(SlaAccessSnapshot access)
    {
        ArgumentNullException.ThrowIfNull(access);
        var time = Time;
        DateTimeOffset? next = null, due = null, risk = null, firstBreach = null;
        var breached = false;
        var snapshot = new List<SlaClock>(3);
        for (var i = 0; i < 3; i++)
        {
            var clock = clocks[i];
            if (clock is null)
                continue;
            DateTimeOffset? clockDue = null;
            if (clock.State == SlaClockState.Running)
            {
                if (clock.BreachedAt is null)
                {
                    clockDue = Crossing(clock, clock.Target, time);
                    due = Min(due, clockDue);
                    risk = Min(risk, Crossing(clock, Percent(clock.Target, applied!.AtRiskPercent), time));
                }
                if (FirstUnfiredThreshold(clock) is { } firstThreshold)
                {
                    var firstCrossing = firstThreshold == 100 && clockDue is not null
                        ? clockDue
                        : Crossing(clock, Percent(clock.Target, firstThreshold), time);
                    next = Min(next, firstCrossing);
                }
            }
            if (clock.State != SlaClockState.Completed && clock.BreachedAt is { } breachedAt)
            {
                breached = true;
                firstBreach = Min(firstBreach, breachedAt);
            }
            snapshot.Add(clock.ToRecord(clockDue));
        }
        if (IsPaused && facts.SnoozedUntil is { } until && until > EvaluatedAt)
            next = Min(next, until);
        if (pending.Count > 0)
            next = FlushRetryAt is { } retry && retry > EvaluatedAt ? Min(next, retry) : EvaluatedAt;
        if (risk is not null && due is null)
            risk = null;
        var schedule = new SlaSchedule(next, due, risk, breached, Min(due, firstBreach));
        return new SlaTracker(conversation, inbox, applied, facts, snapshot, pauses, pending, EmissionCount, access, LastSequence, EvaluatedAt, schedule,
            pending.Count == 0 ? null : FlushRetryAt);
    }

    /// <summary>
    /// Evaluates <paramref name="tracker"/> at <paramref name="now"/> without persisting anything: a copy is advanced to
    /// <paramref name="now"/> (expired snoozes resume, crossed targets count as breached) and each clock reports elapsed and remaining
    /// measured time, the predicted breach instant and the at-risk/breached flags.
    /// </summary>
    public static SlaStatusView View(SlaTracker tracker, Func<BusinessCalendarId?, SlaClockTime?> calendars, DateTimeOffset now)
    {
        var engine = From(tracker, calendars);
        engine.AdvanceTo(now);
        var at = engine.EvaluatedAt;
        var time = engine.Time;
        var risk = engine.applied?.AtRiskPercent ?? SlaPolicy.DefaultAtRiskPercent;
        var views = new List<SlaClockView>(3);
        DateTimeOffset? earliestDue = null;
        var breachedAny = false;
        var atRiskAny = false;
        for (var i = 0; i < 3; i++)
        {
            var clock = engine.clocks[i];
            if (clock is null)
                continue;
            DateTimeOffset? clockDue = null;
            if (clock.State == SlaClockState.Running && clock.BreachedAt is null)
            {
                clockDue = Crossing(clock, clock.Target, time);
                earliestDue = Min(earliestDue, clockDue);
            }
            var elapsed = clock.Elapsed + (clock.RunningSince is { } since ? time.Between(since, at) : TimeSpan.Zero);
            var active = clock.State != SlaClockState.Completed;
            var breached = active && (clock.BreachedAt is not null || elapsed >= clock.Target);
            var atRisk = clock.State == SlaClockState.Running && !breached && elapsed >= Percent(clock.Target, risk);
            if (breached)
                breachedAny = true;
            else if (atRisk)
                atRiskAny = true;
            views.Add(new SlaClockView(clock.Metric, clock.Cycle, clock.State, clock.Outcome, clock.Target, elapsed, clock.Target - elapsed, clock.StartedAt,
                clockDue, clock.BreachedAt, clock.CompletedAt, atRisk, breached));
        }
        return new SlaStatusView(engine.conversation, engine.inbox, engine.applied?.Policy, engine.applied?.Rule.Name, engine.applied?.Rule.Basis,
            engine.IsPaused, views, !breachedAny && atRiskAny, breachedAny, earliestDue, at);
    }

    private void Incoming(DateTimeOffset t)
    {
        if (facts.WaitingSince is null)
            facts = new(facts.Status, facts.SnoozedUntil, facts.Priority, t, facts.Replied);
        EnsureClocks(t);
    }

    private void Outgoing(DateTimeOffset t)
    {
        for (var i = 0; i < 2; i++)
        {
            var clock = clocks[i];
            if (clock is not null && clock.State != SlaClockState.Completed)
                Complete(clock, t, cancel: false);
        }
        facts = new(facts.Status, facts.SnoozedUntil, facts.Priority, null, true);
    }

    private void Status(ConversationStatus status, DateTimeOffset t)
    {
        var previous = facts.Status;
        var current = status.Kind;
        facts = new(current, status.SnoozedUntil, facts.Priority, current is ConversationStatusKind.Resolved or ConversationStatusKind.Closed ? null : facts.WaitingSince, facts.Replied);
        if (current is ConversationStatusKind.Resolved or ConversationStatusKind.Closed)
        {
            for (var i = 0; i < 3; i++)
            {
                var clock = clocks[i];
                if (clock is not null && clock.State != SlaClockState.Completed)
                    Complete(clock, t, cancel: clock.Metric != SlaMetric.Resolution);
            }
        }
        else if (previous is ConversationStatusKind.Resolved or ConversationStatusKind.Closed && clocks[Slot(SlaMetric.Resolution)] is { State: SlaClockState.Completed } resolution
            && resolution.Outcome != SlaClockOutcome.Cancelled)
        {
            resolution.State = IsPaused ? SlaClockState.Paused : SlaClockState.Running;
            resolution.RunningSince = IsPaused ? null : t;
            resolution.CompletedAt = null;
            resolution.Outcome = null;
        }
        UpdatePause(t);
        EnsureClocks(t);
    }

    private void ApplyRule(SlaAppliedRule? next, DateTimeOffset t)
    {
        var oldTime = Time;
        for (var i = 0; i < 3; i++)
        {
            var clock = clocks[i];
            if (clock is not null && clock.State == SlaClockState.Running)
            {
                clock.Elapsed += oldTime.Between(clock.RunningSince!.Value, t);
                clock.RunningSince = t;
            }
        }
        if (next is null)
        {
            for (var i = 0; i < 3; i++)
            {
                var clock = clocks[i];
                if (clock is not null && clock.State != SlaClockState.Completed)
                    Complete(clock, t, cancel: true);
            }
            if (IsPaused)
                pauses[^1] = new SlaPauseInterval(pauses[^1].Start, t);
            Applied = null;
            return;
        }
        Applied = next;
        for (var i = 0; i < 3; i++)
        {
            var clock = clocks[i];
            if (clock is not null && clock.State != SlaClockState.Completed)
            {
                if (next.Rule.Targets.For(clock.Metric) is { } target)
                    clock.Target = target;
                else
                    Complete(clock, t, cancel: true);
            }
        }
        Emit(new SlaAppliedEvent(NextKey(), next.Policy, next.PolicyRevision, next.RuleIndex, next.Rule.Name, next.Rule.Basis, next.Calendar, next.Rule.Targets, t));
        UpdatePause(t);
        EnsureClocks(t);
    }

    /// <summary>
    /// Starts the clocks the facts call for: first response while waiting before any reply and next response while waiting after one
    /// (both from the instant the contact started waiting, even when the rule applies later), resolution while unresolved (from the
    /// rule's application).
    /// </summary>
    private void EnsureClocks(DateTimeOffset t)
    {
        if (applied is null)
            return;
        var targets = applied.Rule.Targets;
        var unresolved = facts.Status is not (ConversationStatusKind.Resolved or ConversationStatusKind.Closed);
        if (targets.FirstResponse is not null && facts.WaitingSince is { } waiting && !facts.Replied && clocks[0] is null)
            Start(SlaMetric.FirstResponse, waiting, cycle: 1);
        if (targets.NextResponse is not null && facts.WaitingSince is { } waitingAgain && facts.Replied && clocks[1] is not { State: not SlaClockState.Completed })
            Start(SlaMetric.NextResponse, waitingAgain, (clocks[1]?.Cycle ?? 0) + 1);
        if (targets.Resolution is not null && unresolved && clocks[2] is null)
            Start(SlaMetric.Resolution, t, cycle: 1);
    }

    private void Start(SlaMetric metric, DateTimeOffset from, int cycle)
    {
        var target = applied!.Rule.Targets.For(metric)!.Value;
        clocks[Slot(metric)] = new Clock
        {
            Metric = metric,
            Cycle = cycle,
            StartedAt = from,
            Target = target,
            State = IsPaused ? SlaClockState.Paused : SlaClockState.Running,
            RunningSince = IsPaused ? null : from,
        };
    }

    private void Complete(Clock clock, DateTimeOffset t, bool cancel)
    {
        if (clock.State == SlaClockState.Running)
            clock.Elapsed += Time.Between(clock.RunningSince!.Value, t);
        clock.State = SlaClockState.Completed;
        clock.RunningSince = null;
        clock.CompletedAt = t;
        clock.Outcome = cancel ? SlaClockOutcome.Cancelled : clock.BreachedAt is null ? SlaClockOutcome.Met : SlaClockOutcome.Missed;
        clock.Completions++;
        if (!cancel)
            Emit(new SlaMetEvent(NextKey(), applied!.Policy, clock.Metric, clock.Cycle, clock.Target, clock.Elapsed, clock.BreachedAt is not null, t));
    }

    private void UpdatePause(DateTimeOffset t)
    {
        var shouldPause = applied is not null && applied.Pause.Pauses(facts.Status, facts.SnoozedUntil, t);
        if (shouldPause && !IsPaused)
        {
            var time = Time;
            for (var i = 0; i < 3; i++)
            {
                var clock = clocks[i];
                if (clock is not null && clock.State == SlaClockState.Running)
                {
                    clock.Elapsed += time.Between(clock.RunningSince!.Value, t);
                    clock.RunningSince = null;
                    clock.State = SlaClockState.Paused;
                }
            }
            pauses.Add(new SlaPauseInterval(t, null));
            if (pauses.Count > SlaTracker.MaximumPauses)
                pauses.RemoveAt(0);
        }
        else if (!shouldPause && IsPaused)
            Resume(t);
    }

    private void Resume(DateTimeOffset t)
    {
        for (var i = 0; i < 3; i++)
        {
            var clock = clocks[i];
            if (clock is not null && clock.State == SlaClockState.Paused)
            {
                clock.State = SlaClockState.Running;
                clock.RunningSince = t;
            }
        }
        pauses[^1] = new SlaPauseInterval(pauses[^1].Start, t);
    }

    private void FireUntil(DateTimeOffset at)
    {
        while (FireNext(at))
        {
        }
    }

    /// <summary>Fires the earliest unfired threshold crossing at or before <paramref name="at"/>; false when none is due.</summary>
    private bool FireNext(DateTimeOffset at)
    {
        if (applied is null)
            return false;
        var time = Time;
        Clock? bestClock = null;
        var bestThreshold = 0;
        DateTimeOffset bestInstant = default;
        for (var i = 0; i < 3; i++)
        {
            var clock = clocks[i];
            if (clock is null || clock.State != SlaClockState.Running)
                continue;
            // Thresholds are ascending and Crossing is monotonic in threshold, so only the first unfired threshold can be earliest for this clock.
            if (FirstUnfiredThreshold(clock) is not { } threshold)
                continue;
            if (Crossing(clock, Percent(clock.Target, threshold), time) is not { } instant || instant > at)
                continue;
            if (bestClock is null || instant < bestInstant || (instant == bestInstant && (clock.Metric < bestClock.Metric || (clock.Metric == bestClock.Metric && threshold < bestThreshold))))
            {
                bestClock = clock;
                bestThreshold = threshold;
                bestInstant = instant;
            }
        }
        if (bestClock is null)
            return false;
        bestClock.MarkFired(bestThreshold);
        var actions = applied.Rule.ActionsAt(bestClock.Metric, bestThreshold);
        if (bestThreshold == 100)
        {
            bestClock.BreachedAt = bestInstant;
            Emit(new SlaBreachedEvent(NextKey(), applied.Policy, bestClock.Metric, bestClock.Cycle, bestClock.Target, bestInstant, actions));
        }
        else
            Emit(new SlaWarningEvent(NextKey(), applied.Policy, bestClock.Metric, bestClock.Cycle, bestThreshold, bestClock.Target, bestInstant,
                bestClock.BreachedAt is null ? Crossing(bestClock, bestClock.Target, time) : null, actions));
        return true;
    }

    /// <summary>The earliest unfired threshold for the clock (a breached clock ignores thresholds below 100).</summary>
    private int? FirstUnfiredThreshold(Clock clock)
    {
        if (applied is null)
            return null;
        var thresholds = applied.Rule.Thresholds(clock.Metric);
        var breached = clock.BreachedAt is not null;
        for (var i = 0; i < thresholds.Count; i++)
        {
            var threshold = thresholds[i];
            if (breached && threshold < 100)
                continue;
            if (!clock.HasFired(threshold))
                return threshold;
        }
        return null;
    }

    /// <summary>The instant a running clock's measured time reaches <paramref name="elapsed"/>.</summary>
    private static DateTimeOffset? Crossing(Clock clock, TimeSpan elapsed, SlaClockTime time) =>
        clock.RunningSince is not { } since ? null : elapsed <= clock.Elapsed ? since : time.Add(since, elapsed - clock.Elapsed);

    private static TimeSpan Percent(TimeSpan target, int percent) => SlaMath.Micros(TimeSpan.FromTicks(target.Ticks / 100 * percent + target.Ticks % 100 * percent / 100));

    private void Emit(SlaTimelineEvent timelineEvent)
    {
        if (pending.Count >= SlaTracker.MaximumPending)
            throw new InvalidOperationException("Too many SLA emissions are pending for one conversation.");
        pending.Add(new SlaPendingEmission(timelineEvent.Key, timelineEvent, Head));
    }

    private string NextKey() => $"sla-{++EmissionCount}";

    private static int Slot(SlaMetric metric) => (int)metric - 1;

    private DateTimeOffset Clamp(DateTimeOffset at)
    {
        var instant = SlaMath.Instant(at);
        return instant < EvaluatedAt ? EvaluatedAt : instant;
    }

    private static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left < right ? left : right;

    private static bool Same(SlaAppliedRule? current, SlaAppliedRule? next) =>
        current is null ? next is null
            : next is not null && current.Policy == next.Policy && current.RuleIndex == next.RuleIndex && current.Rule.Equals(next.Rule) && current.Pause == next.Pause
              && current.AtRiskPercent == next.AtRiskPercent && current.Calendar == next.Calendar;

    private sealed class Clock
    {
        private UInt128 firedMask;

        public SlaMetric Metric { get; init; }
        public int Cycle { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public TimeSpan Target { get; set; }
        public TimeSpan Elapsed { get; set; }
        public DateTimeOffset? RunningSince { get; set; }
        public SlaClockState State { get; set; }
        public DateTimeOffset? BreachedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public SlaClockOutcome? Outcome { get; set; }
        public int Completions { get; set; }

        public bool HasFired(int percent) => (firedMask & (UInt128.One << percent)) != 0;

        public void MarkFired(int percent) => firedMask |= UInt128.One << percent;

        public static Clock From(SlaClock record)
        {
            var clock = new Clock
            {
                Metric = record.Metric,
                Cycle = record.Cycle,
                StartedAt = record.StartedAt,
                Target = record.Target,
                Elapsed = record.Elapsed,
                RunningSince = record.RunningSince,
                State = record.State,
                BreachedAt = record.BreachedAt,
                CompletedAt = record.CompletedAt,
                Outcome = record.Outcome,
                Completions = record.Completions,
            };
            for (var i = 0; i < record.FiredThresholds.Count; i++)
                clock.MarkFired(record.FiredThresholds[i]);
            return clock;
        }

        public SlaClock ToRecord(DateTimeOffset? due)
        {
            int[] fired;
            if (firedMask == 0)
                fired = [];
            else
            {
                var list = new List<int>(4);
                var mask = firedMask;
                for (var p = 1; p <= 100 && mask != 0; p++)
                {
                    if ((mask & (UInt128.One << p)) != 0)
                    {
                        list.Add(p);
                        mask &= ~(UInt128.One << p);
                    }
                }
                fired = [.. list];
            }
            return new SlaClock(Metric, Cycle, StartedAt, Target, Elapsed, RunningSince, State, due, BreachedAt, CompletedAt, Outcome, Completions, fired);
        }
    }
}
