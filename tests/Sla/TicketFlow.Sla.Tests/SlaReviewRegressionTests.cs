using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;
using static TicketFlow.Sla.Tests.SlaScenarios;

namespace TicketFlow.Sla.Tests;

[TestClass]
public sealed class SlaReviewRegressionTests
{
    private static Task<StoredSlaTracker?> TrackerAsync(SlaFixture fixture, string id) => fixture.Get<ISlaTrackerStore>().GetAsync(fixture.Scope, new ConversationId(id));

    [TestMethod]
    public async Task ConcurrentSchedulersSyncsAndDispatcherEmitExactlyOnce()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await VerifyExactlyOnceUnderConcurrencyAsync(fixture);
    }

    [TestMethod]
    public async Task OutboxWritesDoNotReleaseAnotherWorkersLease()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        AdvanceTo(fixture, Monday(14, 30));
        var store = fixture.Get<ISlaTrackerStore>();
        var claim = (await store.ClaimDueAsync(10, TimeSpan.FromMinutes(5))).Single();
        // An outbox-path sync fires the warning while the timer is leased elsewhere; the lease must survive.
        await fixture.InScopeAsync(async services =>
        {
            await services.GetRequiredService<SlaConversationTracker>().SyncAsync(fixture.Scope, new ConversationId("c1"));
            return 0;
        });
        Assert.AreEqual(SlaEventCodec.WarningKind, (await fixture.SlaEventsAsync("c1"))[^1].Kind);
        AdvanceTo(fixture, Monday(14, 33));
        Assert.IsTrue(await store.ReleaseAsync(claim, fixture.Clock.GetUtcNow()), "the lease owner still holds its lease and fence");
    }

    [TestMethod]
    public async Task ForgedSlaEventsDoNotSuppressRealEmissionsAndFailedFlushesBackOff()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday, grantWorker: false);
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        var tracker = (await TrackerAsync(fixture, "c1"))!.Tracker;
        Assert.AreEqual("sla-1", tracker.Pending.Single().Key);
        Assert.AreEqual(Friday.AddMinutes(1), tracker.FlushRetryAt, "a refused append defers flushing by TimerRetryDelay");
        Assert.AreEqual(Friday.AddMinutes(1), tracker.Schedule.NextCheckAt);

        // No spin: the timer is not due again until the retry instant.
        AdvanceTo(fixture, Friday.AddSeconds(30));
        Assert.AreEqual(new SlaTimerRunResult(0, 0, 0), await fixture.RunTimersAsync());
        AdvanceTo(fixture, Friday.AddSeconds(90));
        Assert.AreEqual(new SlaTimerRunResult(0, 1, 0), await fixture.RunTimersAsync(), "refused again: deferred, not lost");
        Assert.AreEqual(Friday.AddSeconds(150), (await TrackerAsync(fixture, "c1"))!.Tracker.Schedule.NextCheckAt);
        Assert.AreEqual(new SlaTimerRunResult(0, 0, 0), await fixture.RunTimersAsync());

        // Another conversation:manage holder forges the pending emission's key; it must not count as the SLA worker's.
        var forged = SlaEventCodec.Encode(new SlaAppliedEvent("sla-1", new SlaPolicyId("standard"), 1, 0, "default", SlaClockBasis.BusinessHours, null,
            new SlaTargets(TimeSpan.FromHours(4)), Friday));
        Assert.IsInstanceOfType<ConversationApplied>((await fixture.CommandAsync(service => service.AppendExtensionAsync(fixture.Owner, new ConversationId("c1"), forged))).Value);
        await fixture.GrantWorkerAsync();
        AdvanceTo(fixture, Friday.AddMinutes(3));
        Assert.AreEqual(1, (await fixture.RunTimersAsync()).Evaluated);

        var events = await fixture.SlaEventsWithActorsAsync("c1");
        Assert.AreEqual(2, events.Count(item => string.Equals(item.Event.Kind, SlaEventCodec.AppliedKind, StringComparison.Ordinal)), "the forged and the real event");
        Assert.AreEqual(1, events.Count(item => item.Actor == SlaFixture.Worker));
        tracker = (await TrackerAsync(fixture, "c1"))!.Tracker;
        Assert.AreEqual(0, tracker.Pending.Count);
        Assert.IsNull(tracker.FlushRetryAt);
        Assert.AreEqual(Monday(14), tracker.Schedule.NextCheckAt);
    }

    [TestMethod]
    public async Task UndecidableDuplicateScanKeepsEmissionsPending()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday, grantWorker: false);
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        // More than MaximumDuplicateScanEvents (1000) events after the pending emission's recorded head.
        await fixture.InScopeAsync(async services =>
        {
            var service = services.GetRequiredService<IConversationService>();
            for (var index = 0; index < 1005; index++)
                Assert.IsInstanceOfType<ConversationApplied>((await service.AppendExtensionAsync(fixture.Owner, new ConversationId("c1"),
                    new ExtensionEvent("test.noise", 1, "{}"))).Value);
            return 0;
        });
        await fixture.GrantWorkerAsync();
        AdvanceTo(fixture, Friday.AddMinutes(5));
        Assert.AreEqual(new SlaTimerRunResult(0, 1, 0), await fixture.RunTimersAsync(), "deferred, never appended blindly");
        Assert.AreEqual(0, (await fixture.SlaEventsWithActorsAsync("c1")).Count);
        var tracker = (await TrackerAsync(fixture, "c1"))!.Tracker;
        Assert.AreEqual(1, tracker.Pending.Count);
        Assert.AreEqual(Friday.AddMinutes(6), tracker.FlushRetryAt);
    }

    [TestMethod]
    public async Task ResumedTrackersReplayLabelsInsteadOfUsingTheSnapshot()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await fixture.InScopeAsync(async services =>
        {
            var policies = services.GetRequiredService<ISlaPolicyService>();
            Assert.IsInstanceOfType<TicketValue<StoredSlaPolicy>>((await policies.UpdateAsync(fixture.Owner, new SlaPolicy(new SlaPolicyId("standard"), "VIP only",
                [new SlaRule("vip", new SlaCondition(labels: [new LabelId("vip")]), new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.CalendarTime)]), 1)).Value);
            return 0;
        });
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        Assert.IsNull((await TrackerAsync(fixture, "c1"))!.Tracker.Applied);

        // A priority change at +10 min and the vip label at +20 min, synchronized together: the rule applies at +20 min, not +10.
        AdvanceTo(fixture, Friday.AddMinutes(10));
        await fixture.CommandAsync(service => service.SetPriorityAsync(fixture.Owner, new ConversationId("c1"), ConversationPriority.High));
        AdvanceTo(fixture, Friday.AddMinutes(20));
        await fixture.CommandAsync(service => service.UpdateLabelsAsync(fixture.Owner, new ConversationId("c1"), [new LabelId("vip")], []));
        await fixture.InScopeAsync(async services =>
        {
            await services.GetRequiredService<SlaConversationTracker>().SyncAsync(fixture.Scope, new ConversationId("c1"));
            return 0;
        });
        var applied = (SlaAppliedEvent)(await fixture.SlaEventsAsync("c1")).Single().Value!;
        Assert.AreEqual(Friday.AddMinutes(20), applied.At);
        await fixture.DispatchAsync();
        Assert.AreEqual(1, (await fixture.SlaEventsAsync("c1")).Count);
    }

    [TestMethod]
    public async Task UnknownCalendarTimeZoneFallsBackToCalendarTimeWithoutThrowing()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        var badCalendar = new BusinessCalendar(new BusinessCalendarId("bad-tz"), "Unknown TZ", "Invalid/Zone_Does_Not_Exist",
            [new WeeklyBusinessHours(DayOfWeek.Monday, BusinessHours.From(9, 0, 17, 0))]);
        Assert.IsInstanceOfType<StoreApplied<StoredBusinessCalendar>>((await fixture.Get<IBusinessCalendarStore>().CreateAsync(fixture.Scope, badCalendar)).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await fixture.Get<IInboxStore>().CreateAsync(fixture.Scope,
            new Inbox(new InboxId("bad-tz-inbox"), "Bad TZ", new ChannelBinding(new ChannelKind("email"), "cfg-bad-tz"),
                new BusinessCalendarId("bad-tz"), new SlaPolicyId("standard")))).Value);

        await fixture.StartAsync("c-bad-tz", inbox: "bad-tz-inbox");
        var dispatch = await fixture.DispatchAsync();
        Assert.AreEqual(0, dispatch.Retried);
        Assert.AreEqual(0, dispatch.DeadLettered);

        var status = await fixture.InScopeAsync(services => services.GetRequiredService<ISlaService>().GetStatusAsync(fixture.Supervisor, new ConversationId("c-bad-tz")));
        var view = ((TicketValue<SlaStatusView>)status.Value!).Value;
        Assert.AreEqual(new SlaPolicyId("standard"), view.Policy);
        Assert.AreEqual(Friday.AddHours(4), view.Clocks.Single(clock => clock.Metric == SlaMetric.FirstResponse).DueAt, "Falls back cleanly to calendar time.");

        AdvanceTo(fixture, Friday.AddHours(3.5));
        var listed = await fixture.InScopeAsync(services => services.GetRequiredService<ISlaService>().ListAsync(fixture.Supervisor, new SlaRiskQuery()));
        var page = ((TicketValue<SlaRiskPage>)listed.Value!).Value;
        Assert.IsTrue(page.Items.Any(item => item.Conversation == new ConversationId("c-bad-tz")));
    }
}
