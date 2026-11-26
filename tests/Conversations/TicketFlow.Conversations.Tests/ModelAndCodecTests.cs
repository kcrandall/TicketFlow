using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Tests;

[TestClass]
public sealed class ModelAndCodecTests
{
    private static readonly DateTimeOffset At = TestClock.DefaultStart;

    [TestMethod]
    public void BodiesAreBoundedInUtf8BytesAndRejectControlCharacters()
    {
        var id = new MessageId("n1");
        Assert.AreEqual(ConversationMessageLimits.MaximumBodyBytes, new NoteEvent(id, new string('a', ConversationMessageLimits.MaximumBodyBytes)).Body.Length);
        Assert.ThrowsExactly<ArgumentException>(() => new NoteEvent(id, new string('a', ConversationMessageLimits.MaximumBodyBytes + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => new NoteEvent(id, new string('€', 30_000)), "30,000 three-byte characters exceed 64 KiB");
        Assert.AreEqual("line\r\n\tnext", new NoteEvent(id, "line\r\n\tnext").Body);
        Assert.ThrowsExactly<ArgumentException>(() => new NoteEvent(id, "bell\a"));
        Assert.ThrowsExactly<ArgumentException>(() => new NoteEvent(id, "\u0085"));
        Assert.ThrowsExactly<ArgumentException>(() => new NoteEvent(id, "lone \ud800 surrogate"));
        Assert.ThrowsExactly<ArgumentException>(() => new NoteEvent(id, "  "));
        Assert.AreEqual(string.Empty, new NoteEvent(id, "", [new AttachmentRef("blob://x", "a.txt", "text/plain", 1)]).Body, "attachments allow empty text");
    }

    [TestMethod]
    public void MessagesEnforceDirectionSenderAndDeliveryConsistency()
    {
        var contact = new ContactParty(new ContactId("ada"));
        var agent = new PrincipalParty(new AccessActor(AccessActorKind.User, "agent"));
        Assert.ThrowsExactly<ArgumentException>(() => new MessageEvent(new MessageId("m"), MessageDirection.Incoming, agent, "x", MessageContentType.PlainText, [], null,
            new MessageDelivery(MessageDeliveryState.Received, At)));
        Assert.ThrowsExactly<ArgumentException>(() => new MessageEvent(new MessageId("m"), MessageDirection.Outgoing, agent, "x", MessageContentType.PlainText, [], null,
            new MessageDelivery(MessageDeliveryState.Received, At)));
        Assert.ThrowsExactly<ArgumentException>(() => new MessageDelivery(MessageDeliveryState.Sent, At, "oops"));
        Assert.ThrowsExactly<ArgumentException>(() => new AttachmentRef("ref", "a.bin", "notamediatype", 1));
        Assert.IsTrue(MessageDelivery.CanAdvance(MessageDeliveryState.Pending, MessageDeliveryState.Read));
        Assert.IsFalse(MessageDelivery.CanAdvance(MessageDeliveryState.Delivered, MessageDeliveryState.Sent));
        Assert.IsFalse(MessageDelivery.CanAdvance(MessageDeliveryState.Failed, MessageDeliveryState.Delivered));
        Assert.IsFalse(MessageDelivery.CanAdvance(MessageDeliveryState.Read, MessageDeliveryState.Failed));
        _ = new MessageEvent(new MessageId("m"), MessageDirection.Incoming, contact, "x", MessageContentType.Html, [], "ext", new MessageDelivery(MessageDeliveryState.Received, At));
    }

    [TestMethod]
    public void StatusUnionPersistsThroughKindAndInstant()
    {
        var snoozed = ConversationStatus.Snoozed(At.AddTicks(7));
        Assert.AreEqual(ConversationStatusKind.Snoozed, snoozed.Kind);
        Assert.AreEqual(At, snoozed.SnoozedUntil, "instants are truncated to microseconds");
        Assert.AreEqual(snoozed, ConversationStatus.FromKind(ConversationStatusKind.Snoozed, At));
        Assert.AreEqual(ConversationStatus.Closed, ConversationStatus.FromKind(ConversationStatusKind.Closed, null));
        Assert.ThrowsExactly<FormatException>(() => ConversationStatus.FromKind(ConversationStatusKind.Open, At));
        Assert.ThrowsExactly<FormatException>(() => ConversationStatus.FromKind(ConversationStatusKind.Snoozed, null));
    }

    [TestMethod]
    public void EveryEventKindRoundTripsThroughTheCodec()
    {
        ConversationEvent[] events =
        [
            new MessageEvent(new MessageId("m1"), MessageDirection.Outgoing, new PrincipalParty(new AccessActor(AccessActorKind.ApiClient, "bot")), "Hi *there*",
                MessageContentType.Markdown, [new AttachmentRef("blob://1", "a b.pdf", "application/pdf", 42)], "prov-1",
                new MessageDelivery(MessageDeliveryState.Failed, At, "bounced")),
            new MessageEvent(new MessageId("m2"), MessageDirection.Incoming, new ContactParty(new ContactId("ada")), "<p>x</p>", MessageContentType.Html, [], null,
                new MessageDelivery(MessageDeliveryState.Received, At)),
            new NoteEvent(new MessageId("n1"), "note"),
            new AssignmentChanged(null, new UserId("agent")),
            new AssignmentChanged(new UserId("agent"), null),
            new TeamChanged(new TeamId("a"), new TeamId("b")),
            new StatusChanged(ConversationStatus.Open, ConversationStatus.Snoozed(At)),
            new StatusChanged(ConversationStatus.Pending, ConversationStatus.Resolved),
            new PriorityChanged(ConversationPriority.None, ConversationPriority.Urgent),
            new LabelsChanged([new LabelId("vip"), new LabelId("a")], [new LabelId("old")]),
            new AttributeChanged("plan", new ConversationTextValue("free"), new ConversationListValue(["a", "b"])),
            new AttributeChanged("seats", new ConversationNumberValue(-1.25m), null),
            new AttributeChanged("renewal", null, new ConversationDateValue(new DateOnly(2027, 2, 28))),
            new AttributeChanged("vip", null, new ConversationBooleanValue(false)),
            new ParticipantChanged(new UserId("lead"), ParticipantChange.Removed),
            new ContactChanged(new ContactId("a"), new ContactId("b"), Guid.NewGuid()),
            new ContactChanged(new ContactId("a"), new ContactId("b"), null),
            new SystemEvent("conversation.imported", "batch 4"),
            new ExtensionEvent("assist.suggested", 3, """{"z":1,"a":{"nested":[true,null]}}"""),
        ];
        foreach (var item in events)
        {
            var json = ConversationJsonCodec.SerializeEvent(item);
            StringAssert.StartsWith(json, "{\"v\":1,\"k\":", StringComparison.Ordinal);
            Assert.AreEqual(item.Value, ConversationJsonCodec.DeserializeEvent(json).Value, json);
        }
    }

    [TestMethod]
    public void CodecRejectsUnknownVersionsDiscriminatorsAndProperties()
    {
        var json = ConversationJsonCodec.SerializeEvent(new AssignmentChanged(null, new UserId("agent")));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeEvent(json.Replace("\"v\":1", "\"v\":2")));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeEvent(json.Replace("assignment", "teleport")));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeEvent(json.Replace("}", ",\"extra\":1}")));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeEvent("""{"v":1,"k":"status","prev":{"s":"open"},"cur":{"s":"snoozed"}}"""));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeEvent("""{"v":1,"k":"priority","prev":1,"cur":99}"""));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeEvent("""{"v":1,"k":"note","id":"n","body":"\u0007","att":[]}"""));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeAttributes("""{"v":1,"a":{"plan":{"t":"blob","v":"x"}}}"""));
        Assert.ThrowsExactly<FormatException>(() => ConversationJsonCodec.DeserializeRedirects("""{"v":1,"r":[{"m":"00000000-0000-0000-0000-000000000000","p":"a"}]}"""));
    }

    [TestMethod]
    public void AttributesRedirectsAndChangesRoundTrip()
    {
        var attributes = new Dictionary<string, ConversationAttributeValue>
        {
            ["plan"] = new ConversationTextValue("pro"),
            ["seats"] = new ConversationNumberValue(3),
            ["tags"] = new ConversationListValue(["x"]),
        };
        var decoded = ConversationJsonCodec.DeserializeAttributes(ConversationJsonCodec.SerializeAttributes(attributes));
        Assert.AreEqual(3, decoded.Count);
        Assert.AreEqual(attributes["tags"], decoded["tags"]);
        Assert.AreEqual("""{"plan":"pro","seats":"3"}""", ConversationJsonCodec.SerializeConditionAttributes(attributes), "lists have no condition value");

        IReadOnlyList<ContactRedirect> redirects = [new(Guid.NewGuid(), new ContactId("a")), new(Guid.NewGuid(), new ContactId("b"))];
        CollectionAssert.AreEqual(redirects.ToArray(), ConversationJsonCodec.DeserializeRedirects(ConversationJsonCodec.SerializeRedirects(redirects)).ToArray());

        var scope = TestScopes.Create();
        var change = new ConversationChange(Guid.NewGuid(), scope, new ConversationId("c"), new InboxId("i"), 4, ConversationChangeKind.EventsAppended,
            [new ConversationEventSummary(3, ConversationEventKind.Message, new MessageId("m"), MessageDirection.Outgoing), new ConversationEventSummary(4, ConversationEventKind.Status)],
            null, null, At);
        Assert.AreEqual(change, ConversationJsonCodec.DeserializeChange(ConversationJsonCodec.SerializeChangePayload(change), change.Id, scope, change.Conversation, change.Inbox,
            change.Revision, change.Kind, change.OccurredAt));
        CollectionAssert.AreEqual(new[] { new MessageId("m") }, change.OutgoingMessages.ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => new ConversationChange(Guid.NewGuid(), scope, change.Conversation, change.Inbox, 1, ConversationChangeKind.EventsAppended, [], null, null, At));
        Assert.ThrowsExactly<ArgumentException>(() => new ConversationChange(Guid.NewGuid(), scope, change.Conversation, change.Inbox, 1, ConversationChangeKind.DeliveryUpdated, [], null, null, At));
    }

    [TestMethod]
    public void ExtensionPayloadsAreBoundedJsonObjects()
    {
        Assert.AreEqual("""{"a":1}""", new ExtensionEvent("sla.breached", 1, """ { "a" : 1 } """).Payload);
        Assert.ThrowsExactly<ArgumentException>(() => new ExtensionEvent("sla.breached", 1, "[1]"));
        Assert.ThrowsExactly<ArgumentException>(() => new ExtensionEvent("sla.breached", 1, "{"));
        Assert.ThrowsExactly<ArgumentException>(() => new ExtensionEvent("sla.breached", 1, "{\"a\":\"" + new string('x', ExtensionEvent.MaximumPayloadBytes) + "\"}"));
        Assert.ThrowsExactly<ArgumentException>(() => new ExtensionEvent("SLA", 1, "{}"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ExtensionEvent("sla.breached", 0, "{}"));
    }

    [TestMethod]
    public void ConversationSnapshotsAreCanonicalAndRedacted()
    {
        var thread = new ChannelThreadRef("sms", "+44 20 7946 0000");
        Assert.IsFalse(thread.ToString().Contains("7946", StringComparison.Ordinal));
        var conversation = new Conversation(new ConversationId("c"), new InboxId("i"), new ContactId("a"), ConversationStatus.Open, ConversationPriority.High, null, null,
            [new LabelId("b"), new LabelId("a"), new LabelId("b")], [], [new UserId("z"), new UserId("y")], thread, At, At, null, null, null, null, 0);
        CollectionAssert.AreEqual(new[] { "a", "b" }, conversation.Labels.Select(label => label.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "y", "z" }, conversation.Participants.Select(user => user.Value).ToArray());
        Assert.IsFalse(conversation.ToString().Contains("7946", StringComparison.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => conversation.With(labels: Enumerable.Range(0, Conversation.MaximumLabels + 1).Select(index => new LabelId($"l{index}"))));
        Assert.AreEqual(new UserId("x"), conversation.With(assignee: new Optional<UserId?>(new UserId("x"))).Assignee);
        Assert.IsNull(conversation.With(assignee: new Optional<UserId?>(new UserId("x"))).With(assignee: new Optional<UserId?>(null)).Assignee);
        Assert.ThrowsExactly<ArgumentException>(() => conversation.With(lastActivityAt: At.AddDays(-1)));
    }

    [TestMethod]
    public void ResourcesCarryConditionAttributesAndPermissionsAreCataloged()
    {
        var scope = TestScopes.Create();
        var conversation = new Conversation(new ConversationId("c"), new InboxId("i"), new ContactId("a"), ConversationStatus.Open, ConversationPriority.None,
            new UserId("u"), new TeamId("t"), [new LabelId("vip")],
            [new("region", new ConversationTextValue("eu")), new("tags", new ConversationListValue(["x"]))], [], null, At, At, null, null, null, null, 0);
        var resource = ConversationAccess.Resource(scope, conversation);
        Assert.AreEqual(new TeamId("t"), resource.TeamId);
        Assert.AreEqual(new InboxId("i"), resource.InboxId);
        Assert.AreEqual(new UserId("u"), resource.AssigneeId);
        Assert.IsTrue(resource.Labels!.Contains(new LabelId("vip")));
        Assert.AreEqual("eu", resource.Attributes["region"]);
        Assert.IsFalse(resource.Attributes.ContainsKey("tags"));

        Assert.IsTrue(BuiltInRoles.Agent.Includes(ConversationPermissions.Manage));
        Assert.IsTrue(BuiltInRoles.LightAgent.Includes(ConversationPermissions.NoteWrite));
        Assert.IsFalse(BuiltInRoles.LightAgent.Includes(ConversationPermissions.Reply));
        Assert.IsFalse(BuiltInRoles.Agent.Includes(ConversationPermissions.Create));
        var catalog = PermissionCatalog.Create(ConversationPermissions.Definitions);
        Assert.IsTrue(catalog.Contains(ConversationPermissions.Manage));
        Assert.IsTrue(catalog.Find(ConversationPermissions.ManageAttributes)!.IsSensitive);
        Assert.AreEqual("conversation.attribute.plan", ConversationFieldPaths.Attribute("plan").Value);
    }

    [TestMethod]
    public void ConversationEnumWireNamesAreStable()
    {
        Assert.AreEqual("open", ConversationStatusKind.Open.WireName);
        Assert.AreEqual("pending", ConversationStatusKind.Pending.WireName);
        Assert.AreEqual("snoozed", ConversationStatusKind.Snoozed.WireName);
        Assert.AreEqual("resolved", ConversationStatusKind.Resolved.WireName);
        Assert.AreEqual("closed", ConversationStatusKind.Closed.WireName);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = ((ConversationStatusKind)99).WireName);

        Assert.AreEqual("none", ConversationPriority.None.WireName);
        Assert.AreEqual("low", ConversationPriority.Low.WireName);
        Assert.AreEqual("medium", ConversationPriority.Medium.WireName);
        Assert.AreEqual("high", ConversationPriority.High.WireName);
        Assert.AreEqual("urgent", ConversationPriority.Urgent.WireName);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = ((ConversationPriority)99).WireName);
    }
}
