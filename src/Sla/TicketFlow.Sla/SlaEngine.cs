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
    private readonly List<Clock> clocks = [];
    private readonly List<SlaPauseInterval> pauses = [];
    private readonly List<SlaPendingEmission> pending = [];
    private readonly SortedSet<string> labels = new(StringComparer.Ordinal);
    private SlaConversationFacts facts;

    private SlaEngine(ConversationId conversation, InboxId inbox, SlaConversationFacts facts, Func<BusinessCalendarId?, SlaClockTime?> calendars)
    {
        this.conversation = conversation;
        this.inbox = inbox;
        this.facts = facts;
        this.calendars = calendars;
    }

    /// <summary>The rule in force, if any.</summary>
    public SlaAppliedRule? Applied { get; private set; }

    /// <summary>The policy (and inbox calendar) used when a priority or label change re-matches rules.</summary>
    public StoredSlaPolicy? Policy { get; set; }

    public BusinessCalendarId? InboxCalendar { get; set; }

    /// <summary>The conversation head when this run started; recorded on new emissions for duplicate detection.</summary>
    public long Head { get; set; }

    public long LastSequence { get; private set; }
    public DateTimeOffset EvaluatedAt { get; private set; }
    public long EmissionCount { get; private set; }
    public SlaConversationFacts Facts => facts;
    public IReadOnlyList<SlaPendingEmission> Pending => pending;

    private bool IsPaused => pauses.Count > 0 && pauses[^1].End is null;

    /// <summary>The time basis of the applied rule (calendar time without a rule, calendar or a resolvable business calendar).</summary>
    public SlaClockTime Time => Applied is not { } applied || applied.Rule.Basis == SlaClockBasis.CalendarTime || applied.Calendar is null
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
        };
        engine.clocks.AddRange(tracker.Clocks.Select(Clock.From));
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
        if (Policy is { } stored && stored.Policy.Match(facts.Priority, labels.Select(label => new LabelId(label))) is { } index)
        {
            var policy = stored.Policy;
            next = new SlaAppliedRule(policy.Id, stored.Revision, index, policy.Rules[index], policy.Pause, policy.AtRiskPercent, InboxCalendar);
        }
        if (Same(Applied, next))
            return;
        ApplyRule(next, t);
        AdvanceTo(t);
    }

    /// <summary>Applies one timeline event (events at or before <see cref="LastSequence"/> are ignored).</summary>
    public void Apply(TimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);
        if (timelineEvent.Sequence <= LastSequence)
            return;
        var t = Clamp(timelineEvent.OccurredAt);
        AdvanceTo(t);
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
                break;
            case LabelsChanged changed:
                foreach (var label in changed.Added)
                    labels.Add(label.Value);
                foreach (var label in changed.Removed)
                    labels.Remove(label.Value);
                Rematch(t);
                break;
            case NoteEvent or AssignmentChanged or TeamChanged or AttributeChanged or ParticipantChanged or ContactChanged or SystemEvent or ExtensionEvent:
                break;
        }
        LastSequence = timelineEvent.Sequence;
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
        while (true)
        {
            // An expired snooze resumes paused clocks at its Until instant, before later thresholds are considered.
            if (IsPaused && facts.SnoozedUntil is { } until && until <= target && Applied is { } applied && !applied.Pause.Pauses(facts.Status, facts.SnoozedUntil, until))
            {
                var resumeAt = until > EvaluatedAt ? until : EvaluatedAt;
                FireUntil(resumeAt);
                Resume(resumeAt);
                continue;
            }
            if (!FireNext(target))
                break;
        }
        if (target > EvaluatedAt)
            EvaluatedAt = target;
    }

    /// <summary>Removes flushed pending emissions.</summary>
    public void Acknowledge(IEnumerable<string> keys)
    {
        var flushed = new HashSet<string>(keys ?? throw new ArgumentNullException(nameof(keys)), StringComparer.Ordinal);
        pending.RemoveAll(item => flushed.Contains(item.Key));
    }

    /// <summary>The immutable tracker with its derived schedule.</summary>
    public SlaTracker ToTracker(SlaAccessSnapshot access)
    {
        ArgumentNullException.ThrowIfNull(access);
        var time = Time;
        DateTimeOffset? next = null, due = null, risk = null, firstBreach = null;
        var breached = false;
        var snapshot = new List<SlaClock>(clocks.Count);
        foreach (var clock in clocks)
        {
            DateTimeOffset? clockDue = null;
            if (clock.State == SlaClockState.Running)
            {
                if (clock.BreachedAt is null)
                {
                    clockDue = Crossing(clock, clock.Target, time);
                    due = Min(due, clockDue);
                    risk = Min(risk, Crossing(clock, Percent(clock.Target, Applied!.AtRiskPercent), time));
                }
                foreach (var threshold in Thresholds(clock))
                    next = Min(next, Crossing(clock, Percent(clock.Target, threshold), time));
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
            next = EvaluatedAt;
        if (risk is not null && due is null)
            risk = null;
        var schedule = new SlaSchedule(next, due, risk, breached, Min(due, firstBreach));
        return new SlaTracker(conversation, inbox, Applied, facts, snapshot, pauses, pending, EmissionCount, access, LastSequence, EvaluatedAt, schedule);
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
        var advanced = engine.ToTracker(tracker.Access);
        var risk = advanced.Applied?.AtRiskPercent ?? SlaPolicy.DefaultAtRiskPercent;
        var views = new List<SlaClockView>(advanced.Clocks.Count);
        foreach (var clock in advanced.Clocks)
        {
            var elapsed = clock.Elapsed + (clock.RunningSince is { } since ? time.Between(since, at) : TimeSpan.Zero);
            var active = clock.State != SlaClockState.Completed;
            var breached = active && (clock.BreachedAt is not null || elapsed >= clock.Target);
            var atRisk = clock.State == SlaClockState.Running && !breached && elapsed >= Percent(clock.Target, risk);
            views.Add(new SlaClockView(clock.Metric, clock.Cycle, clock.State, clock.Outcome, clock.Target, elapsed, clock.Target - elapsed, clock.StartedAt,
                clock.DueAt, clock.BreachedAt, clock.CompletedAt, atRisk, breached));
        }
        var breachedAny = views.Any(view => view.Breached);
        return new SlaStatusView(advanced.Conversation, advanced.Inbox, advanced.Applied?.Policy, advanced.Applied?.Rule.Name, advanced.Applied?.Rule.Basis,
            engine.IsPaused, views, !breachedAny && views.Any(view => view.AtRisk), breachedAny, advanced.Schedule.DueAt, at);
    }

    private void Incoming(DateTimeOffset t)
    {
        if (facts.WaitingSince is null)
            facts = new(facts.Status, facts.SnoozedUntil, facts.Priority, t, facts.Replied);
        EnsureClocks(t);
    }

    private void Outgoing(DateTimeOffset t)
    {
        foreach (var clock in clocks.Where(clock => clock.State != SlaClockState.Completed && clock.Metric is SlaMetric.FirstResponse or SlaMetric.NextResponse).ToList())
            Complete(clock, t, cancel: false);
        facts = new(facts.Status, facts.SnoozedUntil, facts.Priority, null, true);
    }

    private void Status(ConversationStatus status, DateTimeOffset t)
    {
        var previous = facts.Status;
        var current = status.Kind;
        facts = new(current, status.SnoozedUntil, facts.Priority, current is ConversationStatusKind.Resolved or ConversationStatusKind.Closed ? null : facts.WaitingSince, facts.Replied);
        if (current is ConversationStatusKind.Resolved or ConversationStatusKind.Closed)
        {
            foreach (var clock in clocks.Where(clock => clock.State != SlaClockState.Completed).ToList())
                Complete(clock, t, cancel: clock.Metric != SlaMetric.Resolution);
        }
        else if (previous is ConversationStatusKind.Resolved or ConversationStatusKind.Closed && Find(SlaMetric.Resolution) is { State: SlaClockState.Completed } resolution
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
        foreach (var clock in clocks.Where(clock => clock.State == SlaClockState.Running))
        {
            clock.Elapsed += oldTime.Between(clock.RunningSince!.Value, t);
            clock.RunningSince = t;
        }
        if (next is null)
        {
            foreach (var clock in clocks.Where(clock => clock.State != SlaClockState.Completed).ToList())
                Complete(clock, t, cancel: true);
            if (IsPaused)
                pauses[^1] = new SlaPauseInterval(pauses[^1].Start, t);
            Applied = null;
            return;
        }
        Applied = next;
        foreach (var clock in clocks.Where(clock => clock.State != SlaClockState.Completed).ToList())
        {
            if (next.Rule.Targets.For(clock.Metric) is { } target)
                clock.Target = target;
            else
                Complete(clock, t, cancel: true);
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
        if (Applied is not { } applied)
            return;
        var targets = applied.Rule.Targets;
        var unresolved = facts.Status is not (ConversationStatusKind.Resolved or ConversationStatusKind.Closed);
        if (targets.FirstResponse is not null && facts.WaitingSince is { } waiting && !facts.Replied && Find(SlaMetric.FirstResponse) is null)
            Start(SlaMetric.FirstResponse, waiting, cycle: 1);
        if (targets.NextResponse is not null && facts.WaitingSince is { } waitingAgain && facts.Replied && Find(SlaMetric.NextResponse) is not { State: not SlaClockState.Completed })
            Start(SlaMetric.NextResponse, waitingAgain, (Find(SlaMetric.NextResponse)?.Cycle ?? 0) + 1);
        if (targets.Resolution is not null && unresolved && Find(SlaMetric.Resolution) is null)
            Start(SlaMetric.Resolution, t, cycle: 1);
    }

    private void Start(SlaMetric metric, DateTimeOffset from, int cycle)
    {
        var target = Applied!.Rule.Targets.For(metric)!.Value;
        clocks.RemoveAll(clock => clock.Metric == metric);
        clocks.Add(new Clock
        {
            Metric = metric,
            Cycle = cycle,
            StartedAt = from,
            Target = target,
            State = IsPaused ? SlaClockState.Paused : SlaClockState.Running,
            RunningSince = IsPaused ? null : from,
        });
        clocks.Sort((left, right) => left.Metric.CompareTo(right.Metric));
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
            Emit(new SlaMetEvent(NextKey(), Applied!.Policy, clock.Metric, clock.Cycle, clock.Target, clock.Elapsed, clock.BreachedAt is not null, t));
    }

    private void UpdatePause(DateTimeOffset t)
    {
        var shouldPause = Applied is { } applied && applied.Pause.Pauses(facts.Status, facts.SnoozedUntil, t);
        if (shouldPause && !IsPaused)
        {
            foreach (var clock in clocks.Where(clock => clock.State == SlaClockState.Running))
            {
                clock.Elapsed += Time.Between(clock.RunningSince!.Value, t);
                clock.RunningSince = null;
                clock.State = SlaClockState.Paused;
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
        foreach (var clock in clocks.Where(clock => clock.State == SlaClockState.Paused))
        {
            clock.State = SlaClockState.Running;
            clock.RunningSince = t;
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
        if (Applied is not { } applied)
            return false;
        var time = Time;
        (Clock Clock, int Threshold, DateTimeOffset Instant)? earliest = null;
        foreach (var clock in clocks.Where(clock => clock.State == SlaClockState.Running))
            foreach (var threshold in Thresholds(clock))
                if (Crossing(clock, Percent(clock.Target, threshold), time) is { } instant && instant <= at
                    && (earliest is not { } best || instant < best.Instant || (instant == best.Instant && (clock.Metric, threshold).CompareTo((best.Clock.Metric, best.Threshold)) < 0)))
                    earliest = (clock, threshold, instant);
        if (earliest is not { } fire)
            return false;
        var (target, percent, crossing) = fire;
        target.Fired.Add(percent);
        var actions = applied.Rule.ActionsAt(target.Metric, percent);
        if (percent == 100)
        {
            target.BreachedAt = crossing;
            Emit(new SlaBreachedEvent(NextKey(), applied.Policy, target.Metric, target.Cycle, target.Target, crossing, actions));
        }
        else
            Emit(new SlaWarningEvent(NextKey(), applied.Policy, target.Metric, target.Cycle, percent, target.Target, crossing,
                target.BreachedAt is null ? Crossing(target, target.Target, time) : null, actions));
        return true;
    }

    /// <summary>Thresholds not yet fired for the clock (a breached clock only has its remaining escalations below 100 left).</summary>
    private IEnumerable<int> Thresholds(Clock clock) =>
        Applied is not { } applied ? [] : applied.Rule.Thresholds(clock.Metric).Where(threshold => !clock.Fired.Contains(threshold) && !(threshold < 100 && clock.BreachedAt is not null));

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

    private Clock? Find(SlaMetric metric) => clocks.FirstOrDefault(clock => clock.Metric == metric);

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
        public SortedSet<int> Fired { get; } = [];

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
            clock.Fired.UnionWith(record.FiredThresholds);
            return clock;
        }

        public SlaClock ToRecord(DateTimeOffset? due) =>
            new(Metric, Cycle, StartedAt, Target, Elapsed, RunningSince, State, due, BreachedAt, CompletedAt, Outcome, Completions, Fired);
    }
}
