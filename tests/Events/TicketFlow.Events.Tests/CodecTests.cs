using System.Text.Json;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Events.Tests;

[TestClass]
public sealed class CodecTests
{
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 3, 2, 9, 30, 15, TimeSpan.Zero).AddTicks(1_234_567);
    private static readonly ConversationId C = new("c1");
    private static readonly InboxId I = new("email");

    public static IReadOnlyList<IntegrationEventData> Samples() =>
    [
        new ConversationCreatedData(C, I, new ContactId("ada"), new UserId("ann"), new TeamId("billing"), "open", "high", [new LabelId("vip")], "email"),
        new ConversationCreatedData(C, I, new ContactId("ada"), null, null, "open", "none", [], null),
        new ConversationStatusChangedData(C, I, "open", "snoozed", At.AddDays(1)),
        new ConversationAssignedData(C, I, null, new UserId("ann")),
        new ConversationTeamChangedData(C, I, new TeamId("billing"), null),
        new ConversationPriorityChangedData(C, I, "none", "urgent"),
        new ConversationLabelsChangedData(C, I, [new LabelId("vip")], [new LabelId("new")]),
        new ConversationMessageReceivedData(C, I, new MessageId("m1"), new ContactId("ada"), 1, "plain-text", 2),
        new ConversationMessageSentData(C, I, new MessageId("m2"), new EventActor("user", "ann"), 3, "markdown", 0),
        new ConversationNoteAddedData(C, I, new MessageId("n1"), new EventActor("user", "ann")),
        new ConversationResolvedData(C, I, "resolved"),
        new ConversationReopenedData(C, I, "resolved", "open"),
        new ConversationDeletedData(C, I),
        new ContactCreatedData(new ContactId("ada")),
        new ContactUpdatedData(new ContactId("ada")),
        new ContactDeletedData(new ContactId("ada")),
        new ContactMergedData(new ContactId("dup"), new ContactId("ada"), Guid.NewGuid()),
        new ContactUnmergedData(new ContactId("dup"), new ContactId("ada"), Guid.NewGuid()),
        new ContactIdentityLinkedData(new ContactId("ada"), "external-user", true),
        new SlaAppliedData(C, I, "gold", "gold:1", "business-hours", "ny", TimeSpan.FromMinutes(30), null, TimeSpan.FromHours(8), At),
        new SlaWarningData(C, I, "gold", "gold:1", "first-response", 1, 80, TimeSpan.FromMinutes(30), At, At.AddMinutes(6)),
        new SlaBreachedData(C, I, "gold", "gold:1", "resolution", 2, TimeSpan.FromHours(8), At),
        new SlaMetData(C, I, "gold", "gold:1", "first-response", 1, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(12.5), false, At),
        new RoutingAssignedData(C, I, new UserId("ann"), 7),
        new RoutingOverflowedData(C, I, new TeamId("support"), null),
        new RoutingOverflowedData(C, I, null, new InboxId("chat")),
        new RoutingQueueWaitExceededData(C, I, new InboxId("chat")),
        new SurveyRespondedData(C, new ContactId("ada"), "csat", Guid.NewGuid().ToString("D"), I, null, new UserId("ann"), "csat", 5, At),
        new ChannelDeliveryStatusData(C, I, new MessageId("m2"), "delivered"),
    ];

    [TestMethod]
    public void EveryTypeHasASampleAndRoundTrips()
    {
        var samples = Samples();
        CollectionAssert.AreEquivalent(IntegrationEventTypes.All.ToArray(), samples.Select(sample => sample.Type).Distinct().ToArray());
        foreach (var sample in samples)
        {
            Assert.IsTrue(IntegrationEventTypes.IsWellFormed(sample.Type), sample.Type);
            var json = IntegrationEventCodec.EncodeData(sample);
            StringAssert.StartsWith(json, "{\"v\":1,");
            var decoded = IntegrationEventCodec.DecodeData(sample.Type, json);
            Assert.AreEqual(json, IntegrationEventCodec.EncodeData(decoded), sample.Type);
            Assert.AreEqual(sample.Subject, decoded.Subject);
        }
    }

    [TestMethod]
    public void DecodingIsStrict()
    {
        var json = IntegrationEventCodec.EncodeData(new ConversationAssignedData(C, I, null, new UserId("ann")));
        Assert.ThrowsExactly<FormatException>(() => IntegrationEventCodec.DecodeData(IntegrationEventTypes.ConversationTeamChanged + "x", json));
        Assert.ThrowsExactly<FormatException>(() => IntegrationEventCodec.DecodeData("ticketflow.unknown.thing.v1", json));
        Assert.ThrowsExactly<FormatException>(() => IntegrationEventCodec.DecodeData(IntegrationEventTypes.ConversationAssigned, json.Replace("\"v\":1", "\"v\":2")));
        Assert.ThrowsExactly<FormatException>(() => IntegrationEventCodec.DecodeData(IntegrationEventTypes.ConversationAssigned, json.Replace("}", ",\"extra\":1}")));
        Assert.ThrowsExactly<FormatException>(() => IntegrationEventCodec.DecodeData(IntegrationEventTypes.ConversationAssigned, json.Replace("}", ",\"current\":\"bob\"}")));
        Assert.ThrowsExactly<FormatException>(() => IntegrationEventCodec.DecodeData(IntegrationEventTypes.ConversationAssigned, json.Replace("\"ann\"", "null")));
        Assert.ThrowsExactly<FormatException>(() => IntegrationEventCodec.DecodeData(IntegrationEventTypes.ConversationPriorityChanged,
            IntegrationEventCodec.EncodeData(new ConversationPriorityChangedData(C, I, "none", "low")).Replace("\"low\"", "\"Not A Token\"")));
    }

    [TestMethod]
    public void RenderingAppliesPrivacyLevels()
    {
        var scope = TestScopes.Create();
        var data = new ConversationCreatedData(C, I, new ContactId("ada"), new UserId("ann"), null, "open", "high", [new LabelId("vip")], "email");
        var stored = new StoredIntegrationEvent(scope, 42, new IntegrationEvent(EventText.EventId("source", data.Type), At, new EventActor("api-client", "channel"), data),
            new EventResource(C, I), At);

        using var ids = JsonDocument.Parse(IntegrationEventCodec.Render(stored, EventPrivacy.IdsOnly));
        var root = ids.RootElement;
        Assert.AreEqual("1.0", root.GetProperty("specversion").GetString());
        Assert.AreEqual(IntegrationEventTypes.ConversationCreated, root.GetProperty("type").GetString());
        Assert.AreEqual("/ticketflow", root.GetProperty("source").GetString());
        Assert.AreEqual("conversations/c1", root.GetProperty("subject").GetString());
        Assert.AreEqual("42", root.GetProperty("sequence").GetString());
        Assert.AreEqual("2026-03-02T09:30:15.012345Z".Length, root.GetProperty("time").GetString()!.Length);
        Assert.AreEqual("workspace", root.GetProperty("workspaceid").GetString());
        Assert.AreEqual("ids-only", root.GetProperty("privacy").GetString());
        Assert.AreEqual("channel", root.GetProperty("actorid").GetString());
        Assert.AreEqual(stored.Event.Id.ToString("D"), root.GetProperty("id").GetString());
        var idData = root.GetProperty("data");
        CollectionAssert.AreEquivalent(new[] { "conversation", "inbox", "contact", "assignee" }, idData.EnumerateObject().Select(property => property.Name).ToArray());

        using var metadata = JsonDocument.Parse(IntegrationEventCodec.Render(stored, EventPrivacy.Metadata, options: new CloudEventRenderOptions("https://support.example.com/events")));
        var metaData = metadata.RootElement.GetProperty("data");
        Assert.AreEqual("high", metaData.GetProperty("priority").GetString());
        Assert.AreEqual("vip", metaData.GetProperty("labels")[0].GetString());
        Assert.AreEqual("https://support.example.com/events", metadata.RootElement.GetProperty("source").GetString());

        var content = new EventContent(new MessageEventContent("hello", "plain-text", [], null), ["conversation.channel"]);
        Assert.ThrowsExactly<ArgumentException>(() => IntegrationEventCodec.Render(stored, EventPrivacy.Metadata, content));
        using var withContent = JsonDocument.Parse(IntegrationEventCodec.Render(stored, EventPrivacy.Content, content));
        var rendered = withContent.RootElement.GetProperty("data").GetProperty("content");
        Assert.AreEqual("hello", rendered.GetProperty("body").GetString());
        Assert.AreEqual("conversation.channel", rendered.GetProperty("maskedFields")[0].GetString());
    }

    [TestMethod]
    public void EventIdsAreDeterministicVersion8Uuids()
    {
        var id = EventText.EventId("source", IntegrationEventTypes.ConversationCreated);
        Assert.AreEqual(id, EventText.EventId("source", IntegrationEventTypes.ConversationCreated));
        Assert.AreNotEqual(id, EventText.EventId("source", IntegrationEventTypes.ConversationDeleted));
        Assert.AreNotEqual(id, EventText.EventId("other", IntegrationEventTypes.ConversationCreated));
        Assert.AreEqual('8', id.ToString("D")[14]);
    }

    [TestMethod]
    public void TypePatternsMatchExactPrefixAndWildcard()
    {
        Assert.IsTrue(EventTypePattern.All.Matches(IntegrationEventTypes.SlaMet));
        Assert.IsTrue(new EventTypePattern("ticketflow.*").Matches(IntegrationEventTypes.SlaMet));
        Assert.IsTrue(new EventTypePattern("ticketflow.sla.*").Matches(IntegrationEventTypes.SlaMet));
        Assert.IsFalse(new EventTypePattern("ticketflow.sla.*").Matches(IntegrationEventTypes.SurveyResponded));
        Assert.IsTrue(new EventTypePattern(IntegrationEventTypes.SlaMet).Matches(IntegrationEventTypes.SlaMet));
        Assert.IsFalse(new EventTypePattern(IntegrationEventTypes.SlaMet).Matches(IntegrationEventTypes.SlaBreached));
        foreach (var invalid in new[] { "", "sla.*", "ticketflow.sla", "ticketflow.Sla.*", "ticketflow.sla.met.v0", "ticketflow.*.met.v1", "**" })
            Assert.ThrowsExactly<ArgumentException>(() => new EventTypePattern(invalid), invalid);
    }
}
