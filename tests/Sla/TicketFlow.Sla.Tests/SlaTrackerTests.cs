using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla.Tests;

[TestClass]
public sealed class SlaTrackerTests
{
    // Friday 2026-03-06 15:00 EST. Four business hours end Monday 2026-03-09 11:00 EDT (15:00Z) after the DST change.
    private static readonly DateTimeOffset Friday = new(2026, 3, 6, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Monday(int hour, int minute = 0) => new(2026, 3, 9, hour, minute, 0, TimeSpan.Zero);

    private static void AdvanceTo(SlaFixture fixture, DateTimeOffset instant) => fixture.Clock.Advance(instant - fixture.Clock.GetUtcNow());

    private static Task<TicketResult<SlaStatusView>> StatusAsync(SlaFixture fixture, PrincipalRef actor, string id) =>
        fixture.InScopeAsync(services => services.GetRequiredService<ISlaService>().GetStatusAsync(actor, new ConversationId(id)));

    private static Task<TicketResult<SlaRiskPage>> ListAsync(SlaFixture fixture, PrincipalRef actor, SlaRiskQuery query, string? cursor = null) =>
        fixture.InScopeAsync(services => services.GetRequiredService<ISlaService>().ListAsync(actor, query, cursor));

    private static T Value<T>(TicketResult<T> result) => result switch
    {
        TicketValue<T> value => value.Value,
        TicketFailure failure => throw new AssertFailedException($"Expected a value but was {failure.Kind} {failure.CurrentRevision}."),
    };

    private static string[] Ids(SlaRiskPage page) => [.. page.Items.Select(item => item.Conversation.Value)];

    private static string[] Kinds(IReadOnlyList<SlaTimelineEvent> events) => [.. events.Select(item => item.Kind)];

    [TestMethod]
    public async Task WarningsBreachesAndMetFireOnTheTimelineExactlyOnce()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await fixture.StartAsync("c1");
        Assert.AreEqual(2, (await fixture.DispatchAsync()).Handled, "the start, then the tracker's own (ignored) extension append");
        CollectionAssert.AreEqual(new[] { SlaEventCodec.AppliedKind }, Kinds(await fixture.SlaEventsAsync("c1")));
        var applied = (SlaAppliedEvent)(await fixture.SlaEventsAsync("c1"))[0].Value!;
        Assert.AreEqual(("standard", "default", SlaClockBasis.BusinessHours), (applied.Policy.Value, applied.RuleName, applied.Basis));

        var status = Value(await StatusAsync(fixture, fixture.Supervisor, "c1"));
        Assert.AreEqual("standard", status.Policy?.Value);
        var firstResponse = status.Clocks.Single(clock => clock.Metric == SlaMetric.FirstResponse);
        Assert.AreEqual(Monday(15), firstResponse.DueAt);
        Assert.AreEqual(TimeSpan.FromHours(4), firstResponse.Remaining);
        Assert.IsFalse(status.AtRisk || status.Breached);
        Assert.AreEqual(TicketFailureKind.Forbidden, Assert.IsInstanceOfType<TicketFailure>((await StatusAsync(fixture, fixture.Agent, "c1")).Value, "agents need sla:read").Kind);
        Assert.AreEqual(TicketFailureKind.NotFound, Assert.IsInstanceOfType<TicketFailure>((await StatusAsync(fixture, fixture.Supervisor, "missing")).Value).Kind);

        // Saturday: nothing is due over the weekend.
        AdvanceTo(fixture, Friday.AddDays(1));
        Assert.AreEqual(new SlaTimerRunResult(0, 0, 0), await fixture.RunTimersAsync());

        // Monday 10:30 EDT: the 75 % warning (3 business hours, at 10:00 EDT) fires from the timer alone.
        AdvanceTo(fixture, Monday(14, 30));
        Assert.AreEqual(1, (await fixture.RunTimersAsync()).Evaluated);
        var events = await fixture.SlaEventsAsync("c1");
        CollectionAssert.AreEqual(new[] { SlaEventCodec.AppliedKind, SlaEventCodec.WarningKind }, Kinds(events));
        var warning = (SlaWarningEvent)events[1].Value!;
        Assert.AreEqual((Monday(14), Monday(15), 75), (warning.At, warning.DueAt, warning.ThresholdPercent));
        Assert.AreEqual<SlaAction>(new NotifyTeamAction(new TeamId("billing")), warning.Actions.Single());
        CollectionAssert.AreEqual(new[] { "c1" }, Ids(Value(await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery(SlaRiskFilter.AtRisk)))));
        Assert.AreEqual(0, Value(await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery(SlaRiskFilter.Breached))).Items.Count);
        Assert.IsTrue(Value(await StatusAsync(fixture, fixture.Supervisor, "c1")).AtRisk);

        AdvanceTo(fixture, Monday(15, 30));
        Assert.AreEqual(1, (await fixture.RunTimersAsync()).Evaluated);
        events = await fixture.SlaEventsAsync("c1");
        Assert.AreEqual(Monday(15), ((SlaBreachedEvent)events[2].Value!).At);
        CollectionAssert.AreEqual(new[] { "c1" }, Ids(Value(await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery(SlaRiskFilter.Breached)))));
        status = Value(await StatusAsync(fixture, fixture.Supervisor, "c1"));
        Assert.IsTrue(status.Breached);
        Assert.AreEqual(TimeSpan.FromMinutes(-30), status.Clocks.Single(clock => clock.Metric == SlaMetric.FirstResponse).Remaining);

        // The extension appends themselves produce outbox entries; the tracker ignores them.
        await fixture.DispatchAsync();
        Assert.AreEqual(3, (await fixture.SlaEventsAsync("c1")).Count);

        var reply = await fixture.CommandAsync(service => service.ReplyAsync(fixture.Agent, new ConversationId("c1"), new OutgoingMessageDraft("On it")));
        Assert.IsInstanceOfType<ConversationApplied>(reply.Value);
        await fixture.DispatchAsync();
        events = await fixture.SlaEventsAsync("c1");
        CollectionAssert.AreEqual(new[] { SlaEventCodec.AppliedKind, SlaEventCodec.WarningKind, SlaEventCodec.BreachedKind, SlaEventCodec.MetKind }, Kinds(events));
        var met = (SlaMetEvent)events[3].Value!;
        Assert.AreEqual((SlaMetric.FirstResponse, true, TimeSpan.FromMinutes(270)), (met.Metric, met.Breached, met.Elapsed));

        // Redelivery and later timer runs never duplicate timeline entries.
        await fixture.RunTimersAsync();
        await fixture.DispatchAsync();
        await fixture.InScopeAsync(async services =>
        {
            await services.GetRequiredService<SlaConversationTracker>().SyncAsync(fixture.Scope, new ConversationId("c1"));
            return 0;
        });
        Assert.AreEqual(4, (await fixture.SlaEventsAsync("c1")).Count);
        status = Value(await StatusAsync(fixture, fixture.Supervisor, "c1"));
        Assert.AreEqual(SlaClockOutcome.Missed, status.Clocks.Single(clock => clock.Metric == SlaMetric.FirstResponse).Outcome);
        Assert.IsFalse(status.Breached, "the breached first response is complete; resolution is still within target");
        Assert.AreEqual(0, Value(await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery())).Items.Count);
    }

    [TestMethod]
    public async Task SnoozePausesTheClockUntilItExpires()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await fixture.StartAsync("c1");
        AdvanceTo(fixture, Friday.AddHours(1));
        Assert.IsInstanceOfType<ConversationApplied>((await fixture.CommandAsync(service => service.SnoozeAsync(fixture.Agent, new ConversationId("c1"), Monday(14)))).Value);
        await fixture.DispatchAsync();
        var paused = Value(await StatusAsync(fixture, fixture.Supervisor, "c1"));
        Assert.IsTrue(paused.Paused);
        Assert.AreEqual(TimeSpan.FromHours(1), paused.Clocks.Single(clock => clock.Metric == SlaMetric.FirstResponse).Elapsed);

        // The snooze is still Snoozed in the conversation after Until; the timer resumes the clock at Until anyway.
        AdvanceTo(fixture, Monday(16, 30));
        Assert.AreEqual(1, (await fixture.RunTimersAsync()).Evaluated);
        var events = await fixture.SlaEventsAsync("c1");
        Assert.AreEqual(Monday(16), ((SlaWarningEvent)events[1].Value!).At);
        var status = Value(await StatusAsync(fixture, fixture.Supervisor, "c1"));
        Assert.IsFalse(status.Paused);
        Assert.AreEqual(Monday(17), status.DueAt);
    }

    [TestMethod]
    public async Task RiskListsApplyAccessFiltersInsideTheQuery()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await fixture.StartAsync("c1");
        await fixture.StartAsync("c2", team: new TeamId("billing"));
        await fixture.StartAsync("c3", team: new TeamId("billing"));
        await fixture.DispatchAsync();
        AdvanceTo(fixture, Monday(16));
        Assert.AreEqual(3, (await fixture.RunTimersAsync()).Evaluated);

        CollectionAssert.AreEqual(new[] { "c1", "c2", "c3" }, Ids(Value(await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery(SlaRiskFilter.Breached)))));
        CollectionAssert.AreEqual(new[] { "c2", "c3" }, Ids(Value(await ListAsync(fixture, fixture.BillingLead, new SlaRiskQuery()))), "team-scoped grants filter rows");
        Assert.AreEqual(0, Value(await ListAsync(fixture, fixture.Agent, new SlaRiskQuery())).Items.Count, "no sla:read, no rows");
        Assert.AreEqual(TicketFailureKind.Forbidden, Assert.IsInstanceOfType<TicketFailure>((await StatusAsync(fixture, fixture.BillingLead, "c1")).Value).Kind);
        Assert.IsInstanceOfType<TicketValue<SlaStatusView>>((await StatusAsync(fixture, fixture.BillingLead, "c2")).Value);

        var first = Value(await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery(limit: 2)));
        CollectionAssert.AreEqual(new[] { "c1", "c2" }, Ids(first));
        Assert.IsNotNull(first.NextCursor);
        CollectionAssert.AreEqual(new[] { "c3" }, Ids(Value(await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery(limit: 2), first.NextCursor))));
        Assert.AreEqual(TicketFailureKind.Invalid, Assert.IsInstanceOfType<TicketFailure>((await ListAsync(fixture, fixture.Supervisor, new SlaRiskQuery(SlaRiskFilter.AtRisk, limit: 2), first.NextCursor)).Value,
            "cursors are bound to their query").Kind);

        // Moving a conversation out of the team updates the mirrored access snapshot.
        Assert.IsInstanceOfType<ConversationApplied>((await fixture.CommandAsync(service => service.SetTeamAsync(fixture.Owner, new ConversationId("c3"), null))).Value);
        await fixture.DispatchAsync();
        CollectionAssert.AreEqual(new[] { "c2" }, Ids(Value(await ListAsync(fixture, fixture.BillingLead, new SlaRiskQuery()))));
    }

    [TestMethod]
    public async Task LostAcknowledgementNeverDuplicatesTimelineEvents()
    {
        var flaky = new FlakyTrackerStore();
        using var fixture = await SlaFixture.CreateAsync(Friday, services => services.AddSingleton<ISlaTrackerStore>(provider =>
        {
            flaky.Inner = new InMemorySlaTrackerStore(provider.GetRequiredService<TimeProvider>());
            return flaky;
        }));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        AdvanceTo(fixture, Monday(14, 30));
        flaky.FailNextAcknowledgement = true;
        // The first tracker write kept the lease (emissions were pending), so the failed evaluation releases it for a retry.
        Assert.AreEqual(new SlaTimerRunResult(0, 1, 0), await fixture.RunTimersAsync(), "the write after the append failed");
        Assert.AreEqual(2, (await fixture.SlaEventsAsync("c1")).Count, "the warning was appended");
        Assert.AreEqual(1, (await flaky.GetAsync(fixture.Scope, new ConversationId("c1")))!.Tracker.Pending.Count, "and is still pending");

        AdvanceTo(fixture, Monday(14, 32));
        Assert.AreEqual(1, (await fixture.RunTimersAsync()).Evaluated);
        CollectionAssert.AreEqual(new[] { SlaEventCodec.AppliedKind, SlaEventCodec.WarningKind }, Kinds(await fixture.SlaEventsAsync("c1")));
        Assert.AreEqual(0, (await flaky.GetAsync(fixture.Scope, new ConversationId("c1")))!.Tracker.Pending.Count);
    }

    [TestMethod]
    public async Task WorkerWithoutGrantRetriesUntilGranted()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday, grantWorker: false);
        await fixture.StartAsync("c1");
        var result = await fixture.DispatchAsync();
        Assert.AreEqual((0, 1), (result.Handled, result.Retried));
        Assert.AreEqual(0, (await fixture.SlaEventsAsync("c1")).Count);
        await fixture.GrantWorkerAsync();
        AdvanceTo(fixture, Friday.AddMinutes(1));
        Assert.AreEqual(2, (await fixture.DispatchAsync()).Handled);
        CollectionAssert.AreEqual(new[] { SlaEventCodec.AppliedKind }, Kinds(await fixture.SlaEventsAsync("c1")));
    }

    [TestMethod]
    public async Task InboxWithoutPolicyHasNoSlaAndDeletionRemovesTrackers()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await fixture.StartAsync("plain-1", inbox: "plain");
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        Assert.AreEqual(0, (await fixture.SlaEventsAsync("plain-1")).Count);
        var status = Value(await StatusAsync(fixture, fixture.Owner, "plain-1"));
        Assert.IsNull(status.Policy);
        Assert.AreEqual(0, status.Clocks.Count);

        var trackers = fixture.Get<ISlaTrackerStore>();
        var revision = (await fixture.Get<IConversationStore>().GetAsync(fixture.Scope, new ConversationId("c1")))!.Revision;
        Assert.IsNotNull(await trackers.GetAsync(fixture.Scope, new ConversationId("c1")));
        Assert.IsInstanceOfType<ConversationApplied>((await fixture.CommandAsync(service => service.DeleteAsync(fixture.Owner, new ConversationId("c1"), revision))).Value);
        await fixture.DispatchAsync();
        Assert.IsNull(await trackers.GetAsync(fixture.Scope, new ConversationId("c1")));
        AdvanceTo(fixture, Monday(18));
        Assert.AreEqual(new SlaTimerRunResult(0, 0, 0), await fixture.RunTimersAsync());
    }

    [TestMethod]
    public async Task PolicyServiceAuthorizesAtWorkspaceLevel()
    {
        using var fixture = await SlaFixture.CreateAsync(Friday);
        await fixture.InScopeAsync(async services =>
        {
            var policies = services.GetRequiredService<ISlaPolicyService>();
            Assert.AreEqual(TicketFailureKind.Forbidden, Assert.IsInstanceOfType<TicketFailure>((await policies.ListAsync(fixture.Agent)).Value).Kind);
            Assert.AreEqual(1, Value(await policies.ListAsync(fixture.Supervisor)).Count);
            var standard = Value(await policies.GetAsync(fixture.Supervisor, new SlaPolicyId("standard")));
            Assert.AreEqual(TicketFailureKind.Forbidden, Assert.IsInstanceOfType<TicketFailure>((await policies.UpdateAsync(fixture.Supervisor, standard.Policy, standard.Revision)).Value, "sla:read cannot manage").Kind);
            Assert.AreEqual(TicketFailureKind.Conflict, Assert.IsInstanceOfType<TicketFailure>((await policies.UpdateAsync(fixture.Owner, standard.Policy, standard.Revision + 1)).Value).Kind);
            Assert.AreEqual(2, Value(await policies.UpdateAsync(fixture.Owner, standard.Policy, standard.Revision)).Revision);
            Assert.AreEqual(TicketFailureKind.NotFound, Assert.IsInstanceOfType<TicketFailure>((await policies.GetAsync(fixture.Owner, new SlaPolicyId("missing"))).Value).Kind);
            Assert.IsInstanceOfType<TicketValue<StoredSlaPolicy>>((await policies.DeleteAsync(fixture.Owner, new SlaPolicyId("standard"), 2)).Value);
            return 0;
        });
        Assert.IsTrue(fixture.Get<IPermissionCatalog>().Contains(SlaPermissions.Read));
        Assert.IsTrue(fixture.Get<IPermissionCatalog>().Contains(SlaPermissions.Manage));
    }

    /// <summary>Fails the first tracker write that clears pending emissions after <see cref="FailNextAcknowledgement"/> is set.</summary>
    private sealed class FlakyTrackerStore : ISlaTrackerStore
    {
        public InMemorySlaTrackerStore Inner { get; set; } = null!;
        public bool FailNextAcknowledgement { get; set; }

        public Task<StoredSlaTracker?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default) =>
            Inner.GetAsync(scope, conversation, cancellationToken);

        public async Task<StoreWriteResult<StoredSlaTracker, SlaWriteRejection>> WriteAsync(TicketScope scope, SlaTracker tracker, long? expectedRevision, SlaTimerClaim? release = null,
            CancellationToken cancellationToken = default)
        {
            if (FailNextAcknowledgement && tracker.Pending.Count == 0 && expectedRevision is not null
                && (await Inner.GetAsync(scope, tracker.Conversation, cancellationToken))?.Tracker.Pending.Count > 0)
            {
                FailNextAcknowledgement = false;
                throw new InvalidOperationException("Simulated crash after the timeline append.");
            }
            return await Inner.WriteAsync(scope, tracker, expectedRevision, release, cancellationToken);
        }

        public Task<StoreWriteResult<StoredSlaTracker, SlaWriteRejection>> DeleteAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default) =>
            Inner.DeleteAsync(scope, conversation, expectedRevision, cancellationToken);

        public Task<IReadOnlyList<SlaTimerClaim>> ClaimDueAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default) =>
            Inner.ClaimDueAsync(limit, lease, cancellationToken);

        public Task<bool> ReleaseAsync(SlaTimerClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default) =>
            Inner.ReleaseAsync(claim, retryAt, cancellationToken);

        public Task<SlaTrackerPage> ListAsync(TicketScope scope, SlaRiskQuery query, DateTimeOffset now, AccessFilter filter, CancellationToken cancellationToken = default) =>
            Inner.ListAsync(scope, query, now, filter, cancellationToken);
    }
}
