using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Tests;

[TestClass]
public sealed class ConversationServiceTests
{
    private static readonly ConversationId C1 = new("c1");

    [TestMethod]
    public async Task CatalogRegistersFeaturePermissionsOnce()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        foreach (var definition in ConversationPermissions.Definitions)
            Assert.AreEqual(definition, fixture.Catalog.Find(definition.Permission));
        Assert.IsTrue(fixture.Catalog.Contains(ConversationPermissions.Manage));
    }

    [TestMethod]
    public async Task StartValidatesInboxContactTeamAndPermission()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var request = new StartConversationRequest(new InboxId("email"), new ContactId("ada"), Id: C1);
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Agent, request)));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Channel, request with { Inbox = new InboxId("closed-inbox") })));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Channel, request with { Inbox = new InboxId("nope") })));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Channel, request with { Contact = new ContactId("nobody") })));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Channel, request with { Team = new TeamId("ghosts") })));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Channel, request with
        {
            Attributes = new Dictionary<string, ConversationAttributeValue> { ["plan"] = new ConversationTextValue("pro") },
        })));

        var started = ConversationFixture.Applied(await fixture.Conversations.StartAsync(fixture.Channel, request with { Team = new TeamId("billing") }));
        Assert.AreEqual(1L, started.Revision);
        Assert.AreEqual(0, started.Events.Count);
        Assert.AreEqual("conflict:1", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Channel, request)));
        var view = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Agent, C1));
        Assert.AreEqual(ConversationStatus.Open, view.Status);
        Assert.AreEqual(new TeamId("billing"), view.Team);
        Assert.AreEqual(0L, view.LastSequence);

        var generated = ConversationFixture.Applied(await fixture.Conversations.StartAsync(fixture.Channel, new StartConversationRequest(new InboxId("email"), new ContactId("ada"))));
        Assert.AreEqual(32, generated.Conversation.Value.Length);
    }

    [TestMethod]
    public async Task IncomingMessagesAreIdempotentByExternalIdAndReopen()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var started = await fixture.StartAsync("c1", externalId: "ext-1");
        Assert.AreEqual(1, started.Events.Count);
        var replay = await fixture.Conversations.StartAsync(fixture.Channel, new StartConversationRequest(new InboxId("email"), new ContactId("ada"),
            InitialMessage: new IncomingMessageDraft("Help please", ExternalId: "ext-1"), Id: new ConversationId("c-dup")));
        var replayed = ConversationFixture.Applied(replay);
        Assert.IsTrue(replayed.Replayed);
        Assert.AreEqual(C1, replayed.Conversation);
        Assert.AreEqual(started.Events.Single().Id, replayed.Events.Single().Id);

        var second = ConversationFixture.Applied(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("More", ExternalId: "ext-2")));
        Assert.AreEqual(2L, second.Revision);
        var duplicate = ConversationFixture.Applied(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("More", ExternalId: "ext-2")));
        Assert.IsTrue(duplicate.Replayed);
        Assert.AreEqual(2L, duplicate.Revision);
        Assert.AreEqual(second.Events.Single().Id, duplicate.Events.Single().Id);
        Assert.AreEqual(2, ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Owner, C1)).Events.Count);
        var view = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, C1));
        Assert.AreEqual(fixture.Clock.GetUtcNow(), view.WaitingSince);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), view.LastIncomingAt);

        // Resolved conversations reopen on an incoming message; closed ones refuse it.
        ConversationFixture.Applied(await fixture.Conversations.ChangeStatusAsync(fixture.Agent, C1, ConversationStatus.Resolved));
        Assert.IsNull(ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, C1)).WaitingSince);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var reopened = ConversationFixture.Applied(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("Still broken")));
        Assert.AreEqual(2, reopened.Events.Count);
        Assert.AreEqual(new StatusChanged(ConversationStatus.Resolved, ConversationStatus.Open), reopened.Events[1].Event.Value);
        Assert.AreEqual(ConversationStatus.Open, ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, C1)).Status);
        ConversationFixture.Applied(await fixture.Conversations.ChangeStatusAsync(fixture.Agent, C1, ConversationStatus.Closed));
        Assert.AreEqual("refused:Closed", ConversationFixture.Describe(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("Hello?"))));
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.AppendIncomingAsync(fixture.Agent, C1, new IncomingMessageDraft("x"))));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, (await fixture.StartAsync("c2")).Conversation,
            new IncomingMessageDraft(new string('x', ConversationMessageLimits.MaximumBodyBytes + 1)))));
    }

    [TestMethod]
    public async Task RepliesArePendingOutboundMessagesWithADispatchEntry()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.Store.RegisterConsumerAsync("channel-dispatch");
        await fixture.StartAsync("c1");
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        var reply = ConversationFixture.Applied(await fixture.Conversations.ReplyAsync(fixture.Agent, C1, new OutgoingMessageDraft("We are on it", MessageContentType.Markdown)));
        var message = (MessageEvent)reply.Events.Single().Event.Value!;
        Assert.AreEqual(MessageDirection.Outgoing, message.Direction);
        Assert.AreEqual(MessageDeliveryState.Pending, message.Delivery.State);
        Assert.AreEqual(new PrincipalParty(new AccessActor(AccessActorKind.User, "agent")), message.Sender.Value);
        var view = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Agent, C1));
        Assert.IsNull(view.WaitingSince);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), view.FirstReplyAt);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), view.LastActivityAt);

        var claims = await fixture.Store.ClaimAsync("channel-dispatch", 10, TimeSpan.FromMinutes(1));
        Assert.AreEqual(ConversationChangeKind.Started, claims[0].Entry.Change.Kind);
        Assert.AreEqual(1, claims.Count, "the reply waits behind the start for the same conversation");
        Assert.IsTrue(await fixture.Store.AcknowledgeAsync("channel-dispatch", claims[0].Entry.Sequence, claims[0].Fence));
        var dispatch = (await fixture.Store.ClaimAsync("channel-dispatch", 10, TimeSpan.FromMinutes(1))).Single();
        CollectionAssert.AreEqual(new[] { message.Id }, dispatch.Entry.Change.OutgoingMessages.ToArray());
        Assert.AreEqual(reply.Revision, dispatch.Entry.Change.Revision);

        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.ReplyAsync(fixture.Light, C1, new OutgoingMessageDraft("no"))));
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.ReplyAsync(fixture.Viewer, C1, new OutgoingMessageDraft("no"))));
        Assert.AreEqual("applied:3", ConversationFixture.Describe(await fixture.Conversations.AddNoteAsync(fixture.Light, C1, new NoteDraft("internal"))));
        Assert.AreEqual("conflict:3", ConversationFixture.Describe(await fixture.Conversations.ReplyAsync(fixture.Agent, C1, new OutgoingMessageDraft("stale"), expectedRevision: 2)));
        Assert.AreEqual("not-found", ConversationFixture.Describe(await fixture.Conversations.ReplyAsync(fixture.Agent, new ConversationId("missing"), new OutgoingMessageDraft("x"))));
        var otherScope = TestPrincipals.User(TestScopes.Create(workspaceId: "elsewhere"), "agent");
        Assert.AreEqual("not-found", ConversationFixture.Describe(await fixture.Conversations.ReplyAsync(otherScope, C1, new OutgoingMessageDraft("x"))));
    }

    [TestMethod]
    public async Task NotesAreHiddenWithoutNoteRead()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var readOnly = await fixture.CustomRoleAsync("reader", ConversationPermissions.Read);
        await fixture.GrantAsync(new UserId("reader"), readOnly);
        var reader = TestPrincipals.User(fixture.Scope, "reader");
        await fixture.StartAsync("c1");
        ConversationFixture.Applied(await fixture.Conversations.AddNoteAsync(fixture.Agent, C1, new NoteDraft("customer is a VIP")));
        ConversationFixture.Applied(await fixture.Conversations.ReplyAsync(fixture.Agent, C1, new OutgoingMessageDraft("Hello")));

        var withNotes = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Light, C1));
        Assert.IsTrue(withNotes.NotesIncluded);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, withNotes.Events.Select(item => item.Event.Sequence).ToArray());
        var withoutNotes = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(reader, C1));
        Assert.IsFalse(withoutNotes.NotesIncluded);
        CollectionAssert.AreEqual(new long[] { 1, 3 }, withoutNotes.Events.Select(item => item.Event.Sequence).ToArray());
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.AddNoteAsync(reader, C1, new NoteDraft("x"))));

        var paged = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Light, C1, 0, 2));
        Assert.AreEqual(2L, paged.NextAfterSequence);
        Assert.IsNull(ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Light, C1, 2, 2)).NextAfterSequence);
        Assert.IsInstanceOfType<ConversationRefused>((await fixture.Conversations.ReadTimelineAsync(fixture.Light, C1, 0, 0)).Value);
        Assert.IsInstanceOfType<ConversationRefused>((await fixture.Conversations.ReadTimelineAsync(fixture.Channel, C1)).Value);
    }

    [TestMethod]
    public async Task ConcurrentRepliesAllApplyWithGaplessSequences()
    {
        using var fixture = await ConversationFixture.CreateAsync(options => options.MaximumWriteAttempts = 64);
        await fixture.StartAsync("c1");
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(index =>
            Task.Run(() => fixture.Conversations.ReplyAsync(fixture.Agent, C1, new OutgoingMessageDraft($"reply {index}")))));
        Assert.IsTrue(results.All(result => result.Value is ConversationApplied));
        var timeline = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Agent, C1, 0, 200));
        CollectionAssert.AreEqual(Enumerable.Range(1, 25).Select(index => (long)index).ToArray(), timeline.Events.Select(item => item.Event.Sequence).ToArray());
        Assert.AreEqual(25L, ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Agent, C1)).Revision);
    }

    [TestMethod]
    public async Task DeliveryStatusIsIdempotentAndMonotonic()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var started = await fixture.StartAsync("c1", externalId: "in-1");
        var reply = ConversationFixture.Applied(await fixture.Conversations.ReplyAsync(fixture.Agent, C1, new OutgoingMessageDraft("Hello")));
        var messageId = ((MessageEvent)reply.Events.Single().Event.Value!).Id;

        var sent = DeliveryStatusUpdate.ByMessage(C1, messageId, MessageDeliveryState.Sent, providerMessageId: "prov-9");
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Agent, sent)));
        Assert.AreEqual("applied:2", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel, sent)));
        Assert.AreEqual("replayed:2", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel, sent)));
        var delivered = DeliveryStatusUpdate.ByExternalId(new InboxId("email"), "prov-9", MessageDeliveryState.Delivered);
        Assert.AreEqual("applied:2", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel, delivered)));
        Assert.AreEqual("replayed:2", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel,
            DeliveryStatusUpdate.ByExternalId(new InboxId("email"), "prov-9", MessageDeliveryState.Sent))));
        Assert.AreEqual("replayed:2", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel,
            DeliveryStatusUpdate.ByExternalId(new InboxId("email"), "prov-9", MessageDeliveryState.Failed, "bounced"))));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel,
            DeliveryStatusUpdate.ByMessage(C1, messageId, MessageDeliveryState.Read, providerMessageId: "different"))));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel,
            DeliveryStatusUpdate.ByExternalId(new InboxId("email"), "in-1", MessageDeliveryState.Read))));
        Assert.AreEqual("not-found", ConversationFixture.Describe(await fixture.Conversations.UpdateDeliveryStatusAsync(fixture.Channel,
            DeliveryStatusUpdate.ByExternalId(new InboxId("email"), "unknown", MessageDeliveryState.Read))));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DeliveryStatusUpdate.ByMessage(C1, messageId, MessageDeliveryState.Pending));

        var timeline = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Owner, C1));
        var current = (MessageEvent)timeline.Events[1].Event.Event.Value!;
        Assert.AreEqual(MessageDeliveryState.Delivered, current.Delivery.State);
        Assert.AreEqual("prov-9", current.ExternalId);
        Assert.AreEqual(started.Events.Single().Id, timeline.Events[0].Event.Id);
    }

    [TestMethod]
    public async Task StatusSnoozeAndClosedAreEnforced()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.StartAsync("c1");
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.SnoozeAsync(fixture.Agent, C1, fixture.Clock.GetUtcNow())));
        Assert.AreEqual("applied:2", ConversationFixture.Describe(await fixture.Conversations.SnoozeAsync(fixture.Agent, C1, fixture.Clock.GetUtcNow().AddHours(2))));
        Assert.AreEqual("applied:2", ConversationFixture.Describe(await fixture.Conversations.SnoozeAsync(fixture.Agent, C1, fixture.Clock.GetUtcNow().AddHours(2))), "unchanged is a no-op");
        Assert.AreEqual(ConversationStatusKind.Snoozed, ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Agent, C1)).Status.Kind);
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.ChangeStatusAsync(fixture.Light, C1, ConversationStatus.Resolved)));
        Assert.AreEqual("applied:3", ConversationFixture.Describe(await fixture.Conversations.ChangeStatusAsync(fixture.Agent, C1, ConversationStatus.Pending)));
        Assert.AreEqual("applied:4", ConversationFixture.Describe(await fixture.Conversations.ChangeStatusAsync(fixture.Agent, C1, ConversationStatus.Closed)));
        Assert.AreEqual("refused:Closed", ConversationFixture.Describe(await fixture.Conversations.ChangeStatusAsync(fixture.Agent, C1, ConversationStatus.Open)));
        Assert.AreEqual("refused:Closed", ConversationFixture.Describe(await fixture.Conversations.ReplyAsync(fixture.Agent, C1, new OutgoingMessageDraft("x"))));
        Assert.AreEqual("refused:Closed", ConversationFixture.Describe(await fixture.Conversations.AddNoteAsync(fixture.Agent, C1, new NoteDraft("x"))));
        Assert.AreEqual("applied:5", ConversationFixture.Describe(await fixture.Conversations.SetPriorityAsync(fixture.Agent, C1, ConversationPriority.High)), "metadata stays editable");
    }

    [TestMethod]
    public async Task LabelsAttributesPriorityParticipantsAndExtensionsAppendEvents()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.DefineAsync(new ConversationAttributeDefinition("plan", ConversationAttributeType.Text));
        await fixture.DefineAsync(new ConversationAttributeDefinition("seats", ConversationAttributeType.Number));
        await fixture.StartAsync("c1");
        var labels = ConversationFixture.Applied(await fixture.Conversations.UpdateLabelsAsync(fixture.Agent, C1, [new LabelId("vip"), new LabelId("billing")], []));
        Assert.AreEqual(new LabelsChanged([new LabelId("billing"), new LabelId("vip")], []), labels.Events.Single().Event.Value);
        Assert.AreEqual("applied:2", ConversationFixture.Describe(await fixture.Conversations.UpdateLabelsAsync(fixture.Agent, C1, [new LabelId("vip")], [new LabelId("ghost")])));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.UpdateLabelsAsync(fixture.Agent, C1, [new LabelId("a")], [new LabelId("a")])));

        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.UpdateAttributesAsync(fixture.Agent, C1,
            new Dictionary<string, ConversationAttributeValue?> { ["undefined"] = new ConversationTextValue("x") })));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.UpdateAttributesAsync(fixture.Agent, C1,
            new Dictionary<string, ConversationAttributeValue?> { ["seats"] = new ConversationTextValue("many") })));
        var attributes = ConversationFixture.Applied(await fixture.Conversations.UpdateAttributesAsync(fixture.Agent, C1,
            new Dictionary<string, ConversationAttributeValue?> { ["plan"] = new ConversationTextValue("pro"), ["seats"] = new ConversationNumberValue(5) }));
        Assert.AreEqual(2, attributes.Events.Count);
        var removed = ConversationFixture.Applied(await fixture.Conversations.UpdateAttributesAsync(fixture.Agent, C1,
            new Dictionary<string, ConversationAttributeValue?> { ["seats"] = null, ["plan"] = new ConversationTextValue("pro") }));
        Assert.AreEqual(new AttributeChanged("seats", new ConversationNumberValue(5), null), removed.Events.Single().Event.Value);

        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.SetPriorityAsync(fixture.Light, C1, ConversationPriority.Urgent)));
        ConversationFixture.Applied(await fixture.Conversations.SetPriorityAsync(fixture.Agent, C1, ConversationPriority.Urgent));
        var participants = ConversationFixture.Applied(await fixture.Conversations.UpdateParticipantsAsync(fixture.Agent, C1, [new UserId("lead"), new UserId("agent")], []));
        Assert.AreEqual(2, participants.Events.Count);
        ConversationFixture.Applied(await fixture.Conversations.AppendExtensionAsync(fixture.Agent, C1, new ExtensionEvent("sla.warning", 1, """{"minutes":10}""")));

        var view = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Agent, C1));
        CollectionAssert.AreEqual(new[] { "billing", "vip" }, view.Labels.Select(label => label.Value).ToArray());
        Assert.AreEqual(ConversationPriority.Urgent, view.Priority);
        Assert.AreEqual(new ConversationTextValue("pro"), view.Attributes.Single().Value!.Value.Value);
        CollectionAssert.AreEqual(new[] { "agent", "lead" }, view.Participants.Select(user => user.Value).ToArray());
        var kinds = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Agent, C1)).Events.Select(item => item.Event.Event.Kind).ToList();
        CollectionAssert.AreEqual(new[]
        {
            ConversationEventKind.Message, ConversationEventKind.Labels, ConversationEventKind.Attribute, ConversationEventKind.Attribute, ConversationEventKind.Attribute,
            ConversationEventKind.Priority, ConversationEventKind.Participant, ConversationEventKind.Participant, ConversationEventKind.Extension,
        }, kinds);
    }

    [TestMethod]
    public async Task SensitiveAttributesAndChannelIdentifiersAreMasked()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.DefineAsync(new ConversationAttributeDefinition("ssn", ConversationAttributeType.Text, sensitive: true));
        await fixture.DefineAsync(new ConversationAttributeDefinition("account", ConversationAttributeType.Text, sensitive: true));
        await fixture.PolicyAsync(ConversationFieldPaths.SensitivePolicy(ConversationFieldPaths.Attribute("account"), [BuiltInRoles.Owner.Id], ConversationPermissions.Manage));
        await fixture.PolicyAsync(new AccessFieldPolicy(ConversationFieldPaths.Channel, [ConversationPermissions.Read], [BuiltInRoles.Owner.Id]));
        await fixture.StartAsync("c1", externalId: "<msg-1@example.com>", thread: new ChannelThreadRef("email", "ada@example.com"));
        ConversationFixture.Applied(await fixture.Conversations.UpdateAttributesAsync(fixture.Agent, C1, new Dictionary<string, ConversationAttributeValue?>
        {
            ["ssn"] = new ConversationTextValue("123-45-6789"),
            ["account"] = new ConversationTextValue("ACC-1"),
        }));

        var agentView = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Agent, C1));
        Assert.IsTrue(agentView.Attributes.All(attribute => attribute.Masked && attribute.Value is null));
        Assert.IsTrue(agentView.Attributes.Single(attribute => attribute.Key == "account").Revealable);
        Assert.IsFalse(agentView.Attributes.Single(attribute => attribute.Key == "ssn").Revealable);
        Assert.IsTrue(agentView.ChannelThread!.Masked);
        Assert.IsNull(agentView.ChannelThread.ExternalThreadId);
        CollectionAssert.AreEqual(new[] { "conversation.attribute.account", "conversation.attribute.ssn", "conversation.channel" },
            agentView.MaskedFields.Select(field => field.Field.Value).ToArray());
        Assert.IsFalse(agentView.ToString().Contains("ada@", StringComparison.Ordinal));

        var ownerView = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, C1));
        Assert.AreEqual(new ConversationTextValue("ACC-1"), ownerView.Attributes.Single(attribute => attribute.Key == "account").Value!.Value.Value);
        Assert.IsTrue(ownerView.Attributes.Single(attribute => attribute.Key == "ssn").Masked, "a sensitive attribute without a policy fails closed for everyone");
        Assert.AreEqual("ada@example.com", ownerView.ChannelThread!.ExternalThreadId);

        var agentTimeline = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Agent, C1));
        Assert.IsNull(((MessageEvent)agentTimeline.Events[0].Event.Event.Value!).ExternalId);
        Assert.IsTrue(agentTimeline.Events.Skip(1).All(item => item.Event.Event.Value is AttributeChanged { Previous: null, Current: null } && item.MaskedFields.Count == 1));
        var ownerTimeline = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Owner, C1));
        Assert.AreEqual("<msg-1@example.com>", ((MessageEvent)ownerTimeline.Events[0].Event.Event.Value!).ExternalId);
        Assert.IsNotNull(((AttributeChanged)ownerTimeline.Events.Single(item => item.Event.Event.Value is AttributeChanged { Key: "account" }).Event.Event.Value!).Current);
    }

    [TestMethod]
    public async Task ListsAreFilteredByAccessAndKeysetPaged()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.GrantAsync(new UserId("teamer"), BuiltInRoles.Agent, scope: new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        var teamer = TestPrincipals.User(fixture.Scope, "teamer", "billing");
        for (var index = 0; index < 7; index++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.StartAsync($"c{index}", team: index % 2 == 0 ? new TeamId("billing") : null, assignee: index == 3 ? new UserId("agent") : null);
        }

        var all = ConversationFixture.List(await fixture.Conversations.ListAsync(fixture.Agent, new ConversationListQuery(limit: 100)));
        CollectionAssert.AreEqual(new[] { "c6", "c5", "c4", "c3", "c2", "c1", "c0" }, all.Conversations.Select(view => view.Id.Value).ToArray());
        var team = ConversationFixture.List(await fixture.Conversations.ListAsync(teamer, new ConversationListQuery(limit: 100)));
        CollectionAssert.AreEqual(new[] { "c6", "c4", "c2", "c0" }, team.Conversations.Select(view => view.Id.Value).ToArray());
        Assert.AreEqual(0, ConversationFixture.List(await fixture.Conversations.ListAsync(fixture.Channel, new ConversationListQuery())).Conversations.Count);
        CollectionAssert.AreEqual(new[] { "c3" }, ConversationFixture.List(await fixture.Conversations.ListAsync(fixture.Agent,
            new ConversationListQuery(assignee: new UserId("agent")))).Conversations.Select(view => view.Id.Value).ToArray());

        var query = new ConversationListQuery(limit: 3);
        var first = ConversationFixture.List(await fixture.Conversations.ListAsync(fixture.Agent, query));
        var second = ConversationFixture.List(await fixture.Conversations.ListAsync(fixture.Agent, query, first.NextCursor));
        var third = ConversationFixture.List(await fixture.Conversations.ListAsync(fixture.Agent, query, second.NextCursor));
        CollectionAssert.AreEqual(all.Conversations.Select(view => view.Id).ToArray(), first.Conversations.Concat(second.Conversations).Concat(third.Conversations).Select(view => view.Id).ToArray());
        Assert.IsNull(third.NextCursor);
        Assert.IsInstanceOfType<ConversationRefused>((await fixture.Conversations.ListAsync(fixture.Agent, new ConversationListQuery(limit: 4), first.NextCursor)).Value);
        Assert.IsInstanceOfType<ConversationRefused>((await fixture.Conversations.ListAsync(fixture.Agent, query, "not-a-cursor")).Value);

        // Team-scoped agents act only inside their team and cannot move conversations out of it.
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.ReplyAsync(teamer, new ConversationId("c1"), new OutgoingMessageDraft("x"))));
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.SetTeamAsync(teamer, new ConversationId("c0"), null)));
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.SetTeamAsync(fixture.Agent, new ConversationId("c1"), new TeamId("ghosts"))));
        ConversationFixture.Applied(await fixture.Conversations.SetTeamAsync(fixture.Agent, new ConversationId("c1"), new TeamId("billing")));
        ConversationFixture.Applied(await fixture.Conversations.ReplyAsync(teamer, new ConversationId("c1"), new OutgoingMessageDraft("now mine")));
        ConversationFixture.Applied(await fixture.Conversations.AssignAsync(teamer, new ConversationId("c1"), new UserId("teamer")));
        CollectionAssert.AreEqual(new[] { "c1" }, ConversationFixture.List(await fixture.Conversations.ListAsync(teamer,
            new ConversationListQuery(assignee: new UserId("teamer")))).Conversations.Select(view => view.Id.Value).ToArray());
    }

    [TestMethod]
    public async Task FindByThreadDeleteAndDefinitionsAreAuthorized()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var thread = new ChannelThreadRef("email", "root-1");
        await fixture.StartAsync("c1", thread: thread);
        Assert.AreEqual(C1, ConversationFixture.View(await fixture.Conversations.FindByThreadAsync(fixture.Channel, new InboxId("email"), thread)).Id);
        Assert.IsInstanceOfType<ConversationRefused>((await fixture.Conversations.FindByThreadAsync(fixture.Agent, new InboxId("email"), thread)).Value);
        Assert.IsInstanceOfType<ConversationNotFound>((await fixture.Conversations.FindByThreadAsync(fixture.Channel, new InboxId("email"), new ChannelThreadRef("email", "x"))).Value);

        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(await fixture.Conversations.DeleteAsync(fixture.Agent, C1, 1)));
        Assert.AreEqual("conflict:1", ConversationFixture.Describe(await fixture.Conversations.DeleteAsync(fixture.Owner, C1, 2)));
        Assert.AreEqual("applied:1", ConversationFixture.Describe(await fixture.Conversations.DeleteAsync(fixture.Owner, C1, 1)));
        Assert.IsInstanceOfType<ConversationNotFound>((await fixture.Conversations.GetAsync(fixture.Owner, C1)).Value);

        var definition = new ConversationAttributeDefinition("plan", ConversationAttributeType.Text);
        Assert.AreEqual(TicketFailureKind.Forbidden, Assert.IsInstanceOfType<TicketFailure>((await fixture.Definitions.CreateAsync(fixture.Agent, definition)).Value).Kind);
        await fixture.DefineAsync(definition);
        Assert.AreEqual(TicketFailureKind.Conflict, Assert.IsInstanceOfType<TicketFailure>((await fixture.Definitions.CreateAsync(fixture.Owner, definition)).Value).Kind);
        Assert.AreEqual(1, ((TicketValue<IReadOnlyList<StoredConversationAttributeDefinition>>)(await fixture.Definitions.ListAsync(fixture.Agent)).Value!).Value.Count);
        Assert.AreEqual(TicketFailureKind.Rejected, Assert.IsInstanceOfType<TicketFailure>((await fixture.Definitions.UpdateAsync(fixture.Owner,
            new ConversationAttributeDefinition("plan", ConversationAttributeType.Number), 1)).Value).Kind);
        Assert.IsInstanceOfType<TicketValue<StoredConversationAttributeDefinition>>((await fixture.Definitions.DeleteAsync(fixture.Owner, "plan", 1)).Value);
    }

    [TestMethod]
    public async Task MergedContactsCannotStartConversations()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var merge = await fixture.ContactsService.MergeAsync(fixture.Owner, new ContactMergeRequest(new ContactId("ada"), 1, new ContactId("grace"), 1));
        Assert.IsInstanceOfType<TicketValue<ContactMergeOutcome>>(merge.Value);
        Assert.AreEqual("refused:Invalid", ConversationFixture.Describe(await fixture.Conversations.StartAsync(fixture.Channel,
            new StartConversationRequest(new InboxId("email"), new ContactId("grace")))));
    }
}
