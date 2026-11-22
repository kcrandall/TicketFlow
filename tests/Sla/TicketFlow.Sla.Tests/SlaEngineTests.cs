using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla.Tests;

[TestClass]
public sealed class SlaEngineTests
{
    private static readonly AccessActor Actor = new(AccessActorKind.ApiClient, "channel");
    private static readonly ConversationId Conversation = new("c1");
    private static readonly InboxId Inbox = new("email");
    private static readonly SlaAccessSnapshot NoAccess = new(null, null, [], []);

    // Friday 2026-03-06 15:00 EST; US DST starts Sunday 2026-03-08; Monday 2026-03-09 09:00 EDT is 13:00Z.
    private static readonly DateTimeOffset FridayAfternoon = new(2026, 3, 6, 20, 0, 0, TimeSpan.Zero);

    internal static BusinessCalendar NewYork(params CalendarDateOverride[] overrides) => new(new BusinessCalendarId("ny"), "New York", "America/New_York",
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(day => new WeeklyBusinessHours(day, BusinessHours.From(9, 0, 17, 0)))], overrides);

    private static SlaPolicy FirstResponsePolicy(SlaClockBasis basis = SlaClockBasis.BusinessHours, SlaPausePolicy? pause = null) => new(new SlaPolicyId("standard"), "Standard",
        [new SlaRule("default", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(4)), basis,
            [new SlaEscalation(SlaMetric.FirstResponse, 75, [new NotifyUserAction(new UserId("lead"))]),
             new SlaEscalation(SlaMetric.FirstResponse, 100, [new RaisePriorityAction(ConversationPriority.Urgent), new ReassignTeamAction(new TeamId("tier2"))])])],
        pause);

    private static SlaEngine Engine(SlaPolicy policy, BusinessCalendar? calendar, DateTimeOffset createdAt, ConversationPriority priority = ConversationPriority.None,
        IEnumerable<LabelId>? labels = null)
    {
        var time = calendar is null ? null : SlaClockTime.Business(new BusinessCalendarCalculator(calendar));
        var engine = SlaEngine.Create(Conversation, Inbox, new SlaConversationFacts(ConversationStatusKind.Open, null, priority, null, false), labels ?? [], createdAt, 0,
            id => id == calendar?.Id ? time : null);
        engine.Policy = new StoredSlaPolicy(TestScopes.Create(), policy, 1);
        engine.InboxCalendar = calendar?.Id;
        engine.Rematch(createdAt);
        return engine;
    }

    private static TimelineEvent Event(long sequence, DateTimeOffset at, ConversationEvent payload) => new(Conversation, sequence, Guid.NewGuid(), at, Actor, payload);

    private static MessageEvent Incoming(string id) => new(new MessageId(id), MessageDirection.Incoming, new ContactParty(new ContactId("ada")), "Help", MessageContentType.PlainText,
        [], null, new MessageDelivery(MessageDeliveryState.Received, TestClock.DefaultStart));

    private static MessageEvent Outgoing(string id) => new(new MessageId(id), MessageDirection.Outgoing, new PrincipalParty(new AccessActor(AccessActorKind.User, "agent")), "Hi",
        MessageContentType.PlainText, [], null, new MessageDelivery(MessageDeliveryState.Pending, TestClock.DefaultStart));

    private static T Single<T>(SlaEngine engine) => engine.Pending.Select(item => item.Event.Value).OfType<T>().Single();

    /// <summary>Acknowledges every pending emission (as a successful flush would) and returns them.</summary>
    private static IReadOnlyList<SlaPendingEmission> Flush(SlaEngine engine)
    {
        var flushed = engine.Pending.ToList();
        engine.Acknowledge(flushed.Select(item => item.Key));
        return flushed;
    }

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [TestMethod]
    public void FirstResponseSpansWeekendDstChangeAndSnooze()
    {
        var engine = Engine(FirstResponsePolicy(), NewYork(), FridayAfternoon);
        engine.Apply(Event(1, FridayAfternoon, Incoming("m1")));
        // Snoozed Friday 16:00 EST until Monday 10:00 EDT (14:00Z): one business hour elapsed before the pause.
        engine.Apply(Event(2, Utc(3, 6, 21), new StatusChanged(ConversationStatus.Open, ConversationStatus.Snoozed(Utc(3, 9, 14)))));

        engine.AdvanceTo(Utc(3, 9, 13));
        var emitted = new List<SlaPendingEmission>(Flush(engine));
        var paused = engine.ToTracker(NoAccess);
        var clock = paused.Clock(SlaMetric.FirstResponse)!;
        Assert.AreEqual(SlaClockState.Paused, clock.State);
        Assert.AreEqual(TimeSpan.FromHours(1), clock.Elapsed);
        Assert.IsNull(clock.DueAt);
        Assert.AreEqual(Utc(3, 9, 14), paused.Schedule.NextCheckAt, "the snooze expiry is the next check");
        Assert.IsNull(paused.Schedule.UrgencyAt, "paused clocks are neither due nor breached");

        // The expired snooze resumes the clock at 10:00 EDT; 3 more business hours are due at 13:00 EDT (17:00Z).
        engine.AdvanceTo(Utc(3, 9, 16, 30));
        var resumed = engine.ToTracker(NoAccess);
        clock = resumed.Clock(SlaMetric.FirstResponse)!;
        Assert.AreEqual(SlaClockState.Running, clock.State);
        Assert.AreEqual(Utc(3, 9, 14), clock.RunningSince);
        Assert.AreEqual(Utc(3, 9, 17), clock.DueAt);
        Assert.AreEqual(Utc(3, 9, 17), resumed.Schedule.DueAt);
        Assert.AreEqual(Utc(3, 9, 16), resumed.Schedule.AtRiskAt, "75 % of four business hours");
        Assert.IsTrue(resumed.Schedule.IsAtRisk(Utc(3, 9, 16, 30)));
        Assert.AreEqual(new SlaPauseInterval(Utc(3, 6, 21), Utc(3, 9, 14)), resumed.Pauses.Single());
        var warning = Single<SlaWarningEvent>(engine);
        Assert.AreEqual(75, warning.ThresholdPercent);
        Assert.AreEqual(Utc(3, 9, 16), warning.At, "fires at the crossing instant, 12:00 EDT");
        Assert.AreEqual(Utc(3, 9, 17), warning.DueAt);
        Assert.AreEqual<SlaAction>(new NotifyUserAction(new UserId("lead")), warning.Actions.Single());

        engine.AdvanceTo(Utc(3, 9, 17, 30));
        var breached = Single<SlaBreachedEvent>(engine);
        Assert.AreEqual(Utc(3, 9, 17), breached.At);
        Assert.AreEqual(2, breached.Actions.Count);
        var afterBreach = engine.ToTracker(NoAccess);
        Assert.IsTrue(afterBreach.Schedule.Breached);
        Assert.AreEqual(Utc(3, 9, 17), afterBreach.Schedule.UrgencyAt);
        Assert.AreEqual(Utc(3, 9, 17, 30), afterBreach.Schedule.NextCheckAt, "pending emissions keep the timer due until they are flushed");
        emitted.AddRange(Flush(engine));
        Assert.IsNull(engine.ToTracker(NoAccess).Schedule.NextCheckAt, "a breached clock has no further thresholds");

        engine.Apply(Event(3, Utc(3, 9, 17, 45), Outgoing("m2")));
        var met = Single<SlaMetEvent>(engine);
        Assert.IsTrue(met.Breached);
        Assert.AreEqual(TimeSpan.FromMinutes(285), met.Elapsed, "1 h before the snooze plus 3 h 45 min after it");
        var done = engine.ToTracker(NoAccess).Clock(SlaMetric.FirstResponse)!;
        Assert.AreEqual(SlaClockOutcome.Missed, done.Outcome);
        emitted.AddRange(Flush(engine));
        CollectionAssert.AreEqual(new[] { SlaEventCodec.AppliedKind, SlaEventCodec.WarningKind, SlaEventCodec.BreachedKind, SlaEventCodec.MetKind },
            emitted.Select(item => item.Event.Kind).ToArray());
        CollectionAssert.AreEqual(new[] { "sla-1", "sla-2", "sla-3", "sla-4" }, emitted.Select(item => item.Key).ToArray());
    }

    [TestMethod]
    public void WeekendDstAndHolidayMoveTheDueInstant()
    {
        var plain = Engine(FirstResponsePolicy(), NewYork(), FridayAfternoon);
        plain.Apply(Event(1, FridayAfternoon, Incoming("m1")));
        Flush(plain);
        Assert.AreEqual(Utc(3, 9, 15), plain.ToTracker(NoAccess).Schedule.DueAt, "2 h Friday + 2 h Monday = 11:00 EDT");
        Assert.AreEqual(Utc(3, 9, 14), plain.ToTracker(NoAccess).Schedule.NextCheckAt, "the 75 % warning comes first");

        var holiday = Engine(FirstResponsePolicy(), NewYork(CalendarDateOverride.Holiday(new DateOnly(2026, 3, 9), "Closure")), FridayAfternoon);
        holiday.Apply(Event(1, FridayAfternoon, Incoming("m1")));
        Assert.AreEqual(Utc(3, 10, 15), holiday.ToTracker(NoAccess).Schedule.DueAt, "Monday is closed: Tuesday 11:00 EDT");

        var wallClock = Engine(FirstResponsePolicy(SlaClockBasis.CalendarTime), NewYork(), FridayAfternoon);
        wallClock.Apply(Event(1, FridayAfternoon, Incoming("m1")));
        Assert.AreEqual(FridayAfternoon.AddHours(4), wallClock.ToTracker(NoAccess).Schedule.DueAt, "calendar-time rules ignore the calendar");

        var noCalendar = Engine(FirstResponsePolicy(), null, FridayAfternoon);
        noCalendar.Apply(Event(1, FridayAfternoon, Incoming("m1")));
        Assert.AreEqual(FridayAfternoon.AddHours(4), noCalendar.ToTracker(NoAccess).Schedule.DueAt, "an inbox without a calendar measures calendar time");
    }

    [TestMethod]
    public void FallBackDayMeasuresRealElapsedTime()
    {
        var always = new BusinessCalendar(new BusinessCalendarId("ny"), "Always", "America/New_York",
            [.. Enum.GetValues<DayOfWeek>().Select(day => new WeeklyBusinessHours(day, BusinessHours.From(0, 0, 24, 0)))]);
        // Saturday 2026-10-31 22:00 EDT (02:00Z); clocks fall back at 02:00 EDT on Sunday.
        var start = Utc(11, 1, 2);
        var engine = Engine(FirstResponsePolicy(), always, start);
        engine.Apply(Event(1, start, Incoming("m1")));
        Assert.AreEqual(Utc(11, 1, 6), engine.ToTracker(NoAccess).Schedule.DueAt, "four real hours: 01:00 EST in the repeated hour");
        engine.AdvanceTo(Utc(11, 1, 5));
        Assert.AreEqual(Utc(11, 1, 5), Single<SlaWarningEvent>(engine).At);
    }

    [TestMethod]
    public void PendingPausesOnlyWhenConfigured()
    {
        foreach (var pauseWhenPending in new[] { true, false })
        {
            var start = Utc(3, 2, 14);
            var engine = Engine(FirstResponsePolicy(SlaClockBasis.CalendarTime, new SlaPausePolicy(pauseWhenPending, PauseWhenSnoozed: false)), null, start);
            engine.Apply(Event(1, start, Incoming("m1")));
            engine.Apply(Event(2, start.AddHours(1), new StatusChanged(ConversationStatus.Open, ConversationStatus.Pending)));
            engine.Apply(Event(3, start.AddHours(2), new StatusChanged(ConversationStatus.Pending, ConversationStatus.Open)));
            engine.AdvanceTo(start.AddHours(3));
            var view = SlaEngine.View(engine.ToTracker(NoAccess), _ => null, start.AddHours(3));
            var clock = view.Clocks.Single();
            Assert.AreEqual(TimeSpan.FromHours(pauseWhenPending ? 2 : 3), clock.Elapsed);
            Assert.AreEqual(TimeSpan.FromHours(pauseWhenPending ? 2 : 1), clock.Remaining);
            Assert.AreEqual(pauseWhenPending ? start.AddHours(5) : start.AddHours(4), clock.DueAt);
            Assert.AreEqual(!pauseWhenPending, clock.AtRisk, "75 % reached only without the pause");
        }

        // A snooze does not pause when the policy says so.
        var snoozeStart = Utc(3, 2, 14);
        var snoozing = Engine(FirstResponsePolicy(SlaClockBasis.CalendarTime, new SlaPausePolicy(PauseWhenPending: true, PauseWhenSnoozed: false)), null, snoozeStart);
        snoozing.Apply(Event(1, snoozeStart, Incoming("m1")));
        snoozing.Apply(Event(2, snoozeStart.AddHours(1), new StatusChanged(ConversationStatus.Open, ConversationStatus.Snoozed(snoozeStart.AddDays(1)))));
        Assert.AreEqual(SlaClockState.Running, snoozing.ToTracker(NoAccess).Clock(SlaMetric.FirstResponse)!.State);
    }

    [TestMethod]
    public void NextResponseCyclesAndResolutionResumesOnReopen()
    {
        var policy = new SlaPolicy(new SlaPolicyId("full"), "Full",
            [new SlaRule("all", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), TimeSpan.FromHours(8)), SlaClockBasis.CalendarTime)]);
        var t0 = Utc(4, 1, 12);
        var engine = Engine(policy, null, t0);
        engine.Apply(Event(1, t0, Incoming("m1")));
        engine.Apply(Event(2, t0.AddMinutes(20), Outgoing("m2")));
        engine.Apply(Event(3, t0.AddHours(1), Incoming("m3")));
        engine.AdvanceTo(t0.AddMinutes(100));
        engine.Apply(Event(4, t0.AddMinutes(105), Outgoing("m4")));
        engine.Apply(Event(5, t0.AddHours(2), new StatusChanged(ConversationStatus.Open, ConversationStatus.Resolved)));

        var resolved = engine.ToTracker(NoAccess);
        Assert.AreEqual(SlaClockOutcome.Met, resolved.Clock(SlaMetric.FirstResponse)!.Outcome);
        var next = resolved.Clock(SlaMetric.NextResponse)!;
        Assert.AreEqual((1, SlaClockOutcome.Missed), (next.Cycle, next.Outcome));
        Assert.AreEqual(t0.AddMinutes(90), next.BreachedAt);
        var resolution = resolved.Clock(SlaMetric.Resolution)!;
        Assert.AreEqual((SlaClockOutcome.Met, TimeSpan.FromHours(2)), (resolution.Outcome, resolution.Elapsed));
        Assert.IsNull(resolved.Schedule.UrgencyAt);
        Assert.IsNull(resolved.Facts.WaitingSince);

        // An incoming message reopens: StatusChanged then the message, as the conversation service records them.
        engine.Apply(Event(6, t0.AddHours(3), new StatusChanged(ConversationStatus.Resolved, ConversationStatus.Open)));
        engine.Apply(Event(7, t0.AddHours(3), Incoming("m5")));
        var reopened = engine.ToTracker(NoAccess);
        resolution = reopened.Clock(SlaMetric.Resolution)!;
        Assert.AreEqual((SlaClockState.Running, TimeSpan.FromHours(2), 1), (resolution.State, resolution.Elapsed, resolution.Completions));
        Assert.AreEqual(t0.AddHours(9), resolution.DueAt, "six hours remain");
        next = reopened.Clock(SlaMetric.NextResponse)!;
        Assert.AreEqual((2, SlaClockState.Running, t0.AddHours(3)), (next.Cycle, next.State, next.StartedAt));
        Assert.AreEqual(t0.AddMinutes(210), reopened.Schedule.DueAt);

        var mets = engine.Pending.Select(item => item.Event.Value).OfType<SlaMetEvent>().ToList();
        CollectionAssert.AreEqual(new[] { SlaMetric.FirstResponse, SlaMetric.NextResponse, SlaMetric.Resolution }, mets.Select(item => item.Metric).ToArray());
        CollectionAssert.AreEqual(new[] { false, true, false }, mets.Select(item => item.Breached).ToArray());
    }

    [TestMethod]
    public void PriorityChangeRematchesAndKeepsElapsedTime()
    {
        var policy = new SlaPolicy(new SlaPolicyId("tiered"), "Tiered",
        [
            new SlaRule("urgent", new SlaCondition([ConversationPriority.Urgent, ConversationPriority.High]), new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.CalendarTime),
            new SlaRule("default", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(4)), SlaClockBasis.CalendarTime),
        ]);
        var t0 = Utc(4, 1, 12);
        var engine = Engine(policy, null, t0);
        engine.Apply(Event(1, t0, Incoming("m1")));
        Assert.AreEqual(t0.AddHours(4), engine.ToTracker(NoAccess).Schedule.DueAt);
        engine.Apply(Event(2, t0.AddMinutes(30), new PriorityChanged(ConversationPriority.None, ConversationPriority.Urgent)));
        var tracker = engine.ToTracker(NoAccess);
        Assert.AreEqual(0, tracker.Applied!.RuleIndex);
        Assert.AreEqual(t0.AddHours(1), tracker.Schedule.DueAt, "30 minutes already elapsed against the new one-hour target");
        var applied = engine.Pending.Select(item => item.Event.Value).OfType<SlaAppliedEvent>().ToList();
        CollectionAssert.AreEqual(new[] { "default", "urgent" }, applied.Select(item => item.RuleName).ToArray());

        engine.Apply(Event(3, t0.AddMinutes(40), new PriorityChanged(ConversationPriority.Urgent, ConversationPriority.High)));
        Assert.AreEqual(2, engine.Pending.Count(item => item.Event.Value is SlaAppliedEvent), "the same rule is not re-applied");
        engine.AdvanceTo(t0.AddHours(2));
        Assert.AreEqual(t0.AddHours(1), Single<SlaBreachedEvent>(engine).At);
    }

    [TestMethod]
    public void LabelConditionsApplyWhenTheLabelArrives()
    {
        var policy = new SlaPolicy(new SlaPolicyId("vip"), "VIP",
            [new SlaRule("vip", new SlaCondition(labels: [new LabelId("vip")]), new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.CalendarTime)]);
        var t0 = Utc(4, 1, 12);
        var engine = Engine(policy, null, t0);
        engine.Apply(Event(1, t0, Incoming("m1")));
        var none = engine.ToTracker(NoAccess);
        Assert.IsNull(none.Applied);
        Assert.AreEqual(0, none.Clocks.Count);
        Assert.AreEqual(0, engine.Pending.Count);

        engine.Apply(Event(2, t0.AddMinutes(10), new LabelsChanged([new LabelId("vip")], [])));
        var applied = engine.ToTracker(NoAccess);
        Assert.IsNotNull(applied.Applied);
        Assert.AreEqual(t0, applied.Clock(SlaMetric.FirstResponse)!.StartedAt, "the clock starts when the contact started waiting");
        Assert.AreEqual(t0.AddHours(1), applied.Schedule.DueAt);

        engine.Apply(Event(3, t0.AddMinutes(20), new LabelsChanged([], [new LabelId("vip")])));
        var removed = engine.ToTracker(NoAccess);
        Assert.IsNull(removed.Applied);
        Assert.AreEqual(SlaClockOutcome.Cancelled, removed.Clock(SlaMetric.FirstResponse)!.Outcome);
        Assert.IsNull(removed.Schedule.UrgencyAt);
    }

    [TestMethod]
    public void ThresholdsFireInInstantOrderExactlyOnce()
    {
        var policy = new SlaPolicy(new SlaPolicyId("multi"), "Multi",
            [new SlaRule("all", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(2), resolution: TimeSpan.FromHours(3)), SlaClockBasis.CalendarTime,
            [
                new SlaEscalation(SlaMetric.FirstResponse, 50, [new NotifyTeamAction(new TeamId("billing"))]),
                new SlaEscalation(SlaMetric.FirstResponse, 90, [new NotifyUserAction(new UserId("lead"))]),
                new SlaEscalation(SlaMetric.Resolution, 50, [new NotifyUserAction(new UserId("manager"))]),
            ])]);
        var t0 = Utc(4, 1, 12);
        var engine = Engine(policy, null, t0);
        engine.Apply(Event(1, t0, Incoming("m1")));
        engine.AdvanceTo(t0.AddHours(10));
        var fired = engine.Pending.Skip(1).Select(item => item.Event.Value switch
        {
            SlaWarningEvent warning => $"{warning.Metric}:{warning.ThresholdPercent}@{(warning.At - t0).TotalMinutes}",
            SlaBreachedEvent breached => $"{breached.Metric}:100@{(breached.At - t0).TotalMinutes}",
            _ => "other",
        }).ToArray();
        CollectionAssert.AreEqual(new[] { "FirstResponse:50@60", "Resolution:50@90", "FirstResponse:90@108", "FirstResponse:100@120", "Resolution:100@180" }, fired);
        var count = engine.Pending.Count;
        engine.AdvanceTo(t0.AddHours(20));
        Assert.AreEqual(count, engine.Pending.Count, "nothing fires twice");
        var resumed = SlaEngine.From(engine.ToTracker(NoAccess), _ => null);
        resumed.AdvanceTo(t0.AddHours(30));
        Assert.AreEqual(count, resumed.Pending.Count, "fired thresholds survive persistence");
    }

    [TestMethod]
    public void LateFactsApplyAtTheLastEvaluationInstant()
    {
        var t0 = Utc(4, 1, 12);
        var engine = Engine(FirstResponsePolicy(SlaClockBasis.CalendarTime), null, t0);
        engine.Apply(Event(1, t0, Incoming("m1")));
        engine.AdvanceTo(t0.AddHours(2));
        engine.Apply(Event(2, t0.AddHours(1), Outgoing("m2")));
        Assert.AreEqual(TimeSpan.FromHours(2), Single<SlaMetEvent>(engine).Elapsed);
        engine.Apply(Event(2, t0.AddHours(3), Outgoing("m2-duplicate")));
        Assert.AreEqual(1, engine.Pending.Count(item => item.Event.Value is SlaMetEvent), "sequences at or before the last applied one are ignored");
    }
}
