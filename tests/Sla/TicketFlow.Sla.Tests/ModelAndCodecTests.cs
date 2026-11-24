using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla.StoreContracts;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla.Tests;

[TestClass]
public sealed class ModelAndCodecTests
{
    private static readonly DateTimeOffset At = new(2026, 3, 9, 14, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PoliciesAreBoundedAndValidated()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SlaTargets());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlaTargets(TimeSpan.FromSeconds(30)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlaTargets(TimeSpan.FromMinutes(1.5) + TimeSpan.FromMilliseconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlaTargets(TimeSpan.FromDays(366)));
        Assert.ThrowsExactly<ArgumentException>(() => new SlaEscalation(SlaMetric.FirstResponse, 50, []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlaEscalation(SlaMetric.FirstResponse, 0, [new NotifyUserAction(new UserId("u"))]));
        Assert.ThrowsExactly<ArgumentException>(() => new SlaRule("r", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.BusinessHours,
            [new SlaEscalation(SlaMetric.Resolution, 50, [new NotifyUserAction(new UserId("u"))])]), "escalations need a target for their metric");
        Assert.ThrowsExactly<ArgumentException>(() => new SlaRule("r", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.BusinessHours,
            [new SlaEscalation(SlaMetric.FirstResponse, 50, [new NotifyUserAction(new UserId("u"))]), new SlaEscalation(SlaMetric.FirstResponse, 50, [new NotifyUserAction(new UserId("v"))])]));
        Assert.ThrowsExactly<ArgumentException>(() => new SlaRule("bad\u0001name", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(1))));
        Assert.ThrowsExactly<ArgumentException>(() => new SlaRule("\ud800", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(1))), "malformed UTF-16");
        Assert.ThrowsExactly<ArgumentException>(() => new SlaPolicy(new SlaPolicyId("p"), "P", []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SlaPolicy(new SlaPolicyId("p"), "P", SlaStoreContract.Policy("x").Rules, atRiskPercent: 100));
        Assert.ThrowsExactly<ArgumentException>(() => new SlaCondition(labels: Enumerable.Range(0, 17).Select(index => new LabelId($"l{index}"))));
    }

    [TestMethod]
    public void FirstMatchingRuleWins()
    {
        var policy = SlaStoreContract.Policy("standard");
        Assert.AreEqual(0, policy.Match(ConversationPriority.Urgent, []));
        Assert.AreEqual(1, policy.Match(ConversationPriority.Low, []));
        var labelled = new SlaPolicy(new SlaPolicyId("l"), "L",
            [new SlaRule("both", new SlaCondition([ConversationPriority.High], [new LabelId("vip"), new LabelId("eu")]), new SlaTargets(TimeSpan.FromHours(1)))]);
        Assert.AreEqual(0, labelled.Match(ConversationPriority.High, [new LabelId("eu"), new LabelId("vip"), new LabelId("x")]));
        Assert.IsNull(labelled.Match(ConversationPriority.High, [new LabelId("vip")]));
        Assert.IsNull(labelled.Match(ConversationPriority.Low, [new LabelId("vip"), new LabelId("eu")]));
        CollectionAssert.AreEqual(new[] { 75, 100 }, policy.Rules[1].Thresholds(SlaMetric.FirstResponse).ToArray());
        CollectionAssert.AreEqual(new[] { 100 }, policy.Rules[1].Thresholds(SlaMetric.Resolution).ToArray());
    }

    [TestMethod]
    public void PolicyCodecRoundTripsAndIsStrict()
    {
        var policy = new SlaPolicy(new SlaPolicyId("all"), "All actions",
            [new SlaRule("r", new SlaCondition([ConversationPriority.High, ConversationPriority.Low], [new LabelId("vip")]),
                new SlaTargets(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), TimeSpan.FromDays(2)), SlaClockBasis.CalendarTime,
                [new SlaEscalation(SlaMetric.NextResponse, 80, [new NotifyUserAction(new UserId("u")), new NotifyTeamAction(new TeamId("t")),
                    new RaisePriorityAction(ConversationPriority.Urgent), new ReassignTeamAction(new TeamId("t2"))])])],
            new SlaPausePolicy(false, true), 60);
        var json = SlaPolicyCodec.Serialize(policy);
        Assert.AreEqual(policy, SlaPolicyCodec.Deserialize(new SlaPolicyId("all"), json));
        StringAssert.StartsWith(json, "{\"v\":1,");
        Assert.ThrowsExactly<FormatException>(() => SlaPolicyCodec.Deserialize(new SlaPolicyId("all"), json.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal)));
        Assert.ThrowsExactly<FormatException>(() => SlaPolicyCodec.Deserialize(new SlaPolicyId("all"), json.Replace("\"risk\":60", "\"risk\":60,\"extra\":1", StringComparison.Ordinal)));
        Assert.ThrowsExactly<FormatException>(() => SlaPolicyCodec.Deserialize(new SlaPolicyId("all"), json.Replace("notify-user", "notify-everyone", StringComparison.Ordinal)));
        Assert.ThrowsExactly<FormatException>(() => SlaPolicyCodec.Deserialize(new SlaPolicyId("all"), json.Replace("\"risk\":60", "\"risk\":100", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void EventCodecRoundTripsEveryKind()
    {
        SlaTimelineEvent[] events =
        [
            new SlaAppliedEvent("sla-1", new SlaPolicyId("p"), 3, 1, "default", SlaClockBasis.BusinessHours, new BusinessCalendarId("ny"), new SlaTargets(TimeSpan.FromHours(4)), At),
            new SlaWarningEvent("sla-2", new SlaPolicyId("p"), SlaMetric.FirstResponse, 1, 75, TimeSpan.FromHours(4), At, At.AddHours(1), [new NotifyUserAction(new UserId("lead"))]),
            new SlaBreachedEvent("sla-3", new SlaPolicyId("p"), SlaMetric.NextResponse, 2, TimeSpan.FromMinutes(30), At, []),
            new SlaMetEvent("sla-4", new SlaPolicyId("p"), SlaMetric.Resolution, 1, TimeSpan.FromDays(1), TimeSpan.FromTicks(12_345_670), true, At),
        ];
        foreach (var item in events)
        {
            var extension = SlaEventCodec.Encode(item);
            Assert.AreEqual(item.Kind, extension.Kind);
            Assert.AreEqual(1, extension.PayloadVersion);
            Assert.IsTrue(SlaEventCodec.TryDecode(extension, out var decoded));
            Assert.AreEqual(item.Value, decoded.Value);
            Assert.AreEqual(item.Key, decoded.Key);
        }
        Assert.IsFalse(SlaEventCodec.TryDecode(new ExtensionEvent("automation.rule-applied", 1, "{}"), out _));
        Assert.IsFalse(SlaEventCodec.TryDecode(new ExtensionEvent(SlaEventCodec.MetKind, 2, SlaEventCodec.Serialize(events[3])), out _));
        Assert.ThrowsExactly<FormatException>(() => SlaEventCodec.Deserialize(SlaEventCodec.BreachedKind, 1, SlaEventCodec.Serialize(events[3])));
    }

    [TestMethod]
    public void TrackerCodecRoundTripsEngineState()
    {
        var calendar = SlaEngineTests.NewYork();
        var time = SlaClockTime.Business(new BusinessCalendarCalculator(calendar));
        var engine = SlaEngine.Create(new ConversationId("c1"), new InboxId("email"),
            new SlaConversationFacts(ConversationStatusKind.Open, null, ConversationPriority.None, null, false), [new LabelId("vip")], At, 0, _ => time);
        engine.Policy = new StoredSlaPolicy(TicketFlow.Testing.TestScopes.Create(), SlaStoreContract.Policy("standard"), 2);
        engine.InboxCalendar = calendar.Id;
        engine.Rematch(At);
        engine.Apply(new TimelineEvent(new ConversationId("c1"), 1, Guid.NewGuid(), At, new TicketFlow.Access.AccessActor(TicketFlow.Access.AccessActorKind.ApiClient, "c"),
            new MessageEvent(new MessageId("m"), MessageDirection.Incoming, new ContactParty(new ContactId("ada")), "x", MessageContentType.PlainText, [], null,
                new MessageDelivery(MessageDeliveryState.Received, At))));
        engine.Apply(new TimelineEvent(new ConversationId("c1"), 2, Guid.NewGuid(), At.AddMinutes(30), new TicketFlow.Access.AccessActor(TicketFlow.Access.AccessActorKind.User, "a"),
            new StatusChanged(ConversationStatus.Open, ConversationStatus.Pending)));
        engine.AdvanceTo(At.AddDays(2));
        var tracker = engine.ToTracker(new SlaAccessSnapshot(new TeamId("billing"), new UserId("agent"), [new LabelId("vip")], [new("tier", "gold")]));
        var json = SlaTrackerCodec.Serialize(tracker);
        var decoded = SlaTrackerCodec.Deserialize(json);
        Assert.AreEqual(json, SlaTrackerCodec.Serialize(decoded));
        Assert.AreEqual(tracker.Applied!.Rule, decoded.Applied!.Rule);
        Assert.AreEqual(tracker.Clocks.Count, decoded.Clocks.Count);
        Assert.IsTrue(tracker.Clocks.SequenceEqual(decoded.Clocks));
        Assert.AreEqual(tracker.Access, decoded.Access);
        Assert.AreEqual(tracker.Schedule, decoded.Schedule);
        Assert.ThrowsExactly<FormatException>(() => SlaTrackerCodec.Deserialize(json.Replace("\"v\":1", "\"v\":9", StringComparison.Ordinal)));
        Assert.ThrowsExactly<FormatException>(() => SlaTrackerCodec.Deserialize(json.Replace("\"emissions\":", "\"unknown\":0,\"emissions\":", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SlaMetricWireNamesAreStable()
    {
        Assert.AreEqual("first-response", SlaMetric.FirstResponse.WireName);
        Assert.AreEqual("next-response", SlaMetric.NextResponse.WireName);
        Assert.AreEqual("resolution", SlaMetric.Resolution.WireName);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = ((SlaMetric)99).WireName);
    }
}
