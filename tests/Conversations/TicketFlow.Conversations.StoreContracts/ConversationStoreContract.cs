using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.StoreContracts;

/// <summary>
/// Behavioral contract every conversation store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the
/// same storage (for example two data sources), so fencing is verified across clients. Outbox checks need the stores to share the
/// supplied <see cref="FakeTimeProvider"/>.
/// </summary>
public static class ConversationStoreContract
{
    public static readonly DateTimeOffset At = TestClock.DefaultStart;
    public static readonly AccessActor Agent = new(AccessActorKind.User, "agent");
    public static readonly AccessActor Ingest = new(AccessActorKind.ApiClient, "ingest");

    public static string Describe(ConversationWriteResult result) => result switch
    {
        ConversationWriteApplied applied => $"applied:{applied.Conversation.Revision}",
        ConversationWriteNotFound => "not-found",
        ConversationWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
        ConversationDuplicateMessage duplicate => $"duplicate:{duplicate.Existing.Message}",
        ConversationWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static string Describe(MessageDeliveryWriteResult result) => result switch
    {
        MessageDeliveryApplied applied => $"applied:{applied.Message.DeliveryRevision}",
        ConversationWriteNotFound => "not-found",
        MessageDeliveryConflict conflict => $"conflict:{conflict.Current.DeliveryRevision}",
        ConversationDuplicateMessage duplicate => $"duplicate:{duplicate.Existing.Message}",
    };

    public static string Describe(ConversationAttributeDefinitionWriteResult result) => result switch
    {
        ConversationAttributeDefinitionApplied applied => $"applied:{applied.Value.Revision}",
        ConversationWriteNotFound => "not-found",
        ConversationWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
        ConversationWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static Conversation NewConversation(string id, string inbox = "email", string contact = "ada", string? team = null, string? assignee = null,
        IEnumerable<string>? labels = null, IEnumerable<KeyValuePair<string, ConversationAttributeValue>>? attributes = null, DateTimeOffset? activity = null,
        ChannelThreadRef? thread = null, ConversationStatus? status = null) =>
        new(new ConversationId(id), new InboxId(inbox), new ContactId(contact), status ?? ConversationStatus.Open, ConversationPriority.None,
            assignee is null ? null : new UserId(assignee), team is null ? null : new TeamId(team), (labels ?? []).Select(label => new LabelId(label)),
            attributes ?? [], [], thread, At, activity ?? At, null, null, null, null, 0);

    public static MessageEvent Incoming(string id, string body = "Hello", string? externalId = null, string contact = "ada") =>
        new(new MessageId(id), MessageDirection.Incoming, new ContactParty(new ContactId(contact)), body, MessageContentType.PlainText, [], externalId,
            new MessageDelivery(MessageDeliveryState.Received, At));

    public static MessageEvent Outgoing(string id, string body = "Hi there") =>
        new(new MessageId(id), MessageDirection.Outgoing, new PrincipalParty(Agent), body, MessageContentType.Markdown,
            [new AttachmentRef("blob://a/1", "invoice.pdf", "application/pdf", 1024)], null, new MessageDelivery(MessageDeliveryState.Pending, At));

    public static ConversationChange Change(TicketScope scope, Conversation conversation, long revision, ConversationChangeKind kind, IEnumerable<TimelineEvent> events) =>
        new(Guid.NewGuid(), scope, conversation.Id, conversation.Inbox, revision, kind, events.Select(ConversationEventSummary.From), null, null, At);

    public static async Task<StoredConversation> CreateAsync(IConversationStore store, TicketScope scope, Conversation conversation, params ConversationEvent[] events)
    {
        var timeline = events.Select((item, index) => new TimelineEvent(conversation.Id, index + 1, Guid.NewGuid(), At, Ingest, item)).ToList();
        var created = conversation.With(lastSequence: timeline.Count);
        var result = await store.WriteAsync(scope, new ConversationWrite(created, null, timeline, Change(scope, created, 1, ConversationChangeKind.Started, timeline)));
        return Assert.IsInstanceOfType<ConversationWriteApplied>(result.Value).Conversation;
    }

    /// <summary>Appends <paramref name="events"/> to the stored snapshot (optionally transformed) at the next sequences.</summary>
    public static Task<ConversationWriteResult> AppendAsync(IConversationStore store, StoredConversation stored, Func<Conversation, Conversation>? transform, params ConversationEvent[] events)
    {
        var head = stored.Conversation.LastSequence;
        var timeline = events.Select((item, index) => new TimelineEvent(stored.Id, head + index + 1, Guid.NewGuid(), At, Agent, item)).ToList();
        var next = (transform ?? (conversation => conversation))(stored.Conversation).With(lastSequence: head + timeline.Count);
        return store.WriteAsync(stored.Scope, new ConversationWrite(next, stored.Revision, timeline,
            timeline.Count == 0 ? null : Change(stored.Scope, next, stored.Revision + 1, ConversationChangeKind.EventsAppended, timeline)));
    }

    public static async Task VerifyWritesAsync(IConversationStore primary, IConversationStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "conversations-contract");
        var attributes = new Dictionary<string, ConversationAttributeValue>
        {
            ["plan"] = new ConversationTextValue("pro"),
            ["seats"] = new ConversationNumberValue(12.5m),
            ["vip"] = new ConversationBooleanValue(true),
            ["renewal"] = new ConversationDateValue(new DateOnly(2027, 1, 31)),
            ["tags"] = new ConversationListValue(["billing", "urgent"]),
        };
        var thread = new ChannelThreadRef("email", "<root@example.com>");
        var conversation = NewConversation("c1", team: "billing", assignee: "agent", labels: ["vip", "billing"], attributes: attributes, thread: thread);
        var stored = await CreateAsync(primary, scope, conversation, Incoming("m1", externalId: "ext-1"));
        Assert.AreEqual(1L, stored.Revision);
        Assert.AreEqual(1L, stored.Conversation.LastSequence);
        Assert.AreEqual("conflict:1", Describe(await primary.WriteAsync(scope, new ConversationWrite(conversation, null))));

        var read = (await competing.GetAsync(scope, conversation.Id))!;
        Assert.AreEqual(stored.Conversation, read.Conversation);
        Assert.AreEqual(scope, read.Scope);
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await competing.GetAsync(other, conversation.Id));

        // Revision fencing across clients, with snoozed status and redirects round-tripping.
        var snoozed = await AppendAsync(competing, read, current => current.With(status: ConversationStatus.Snoozed(At.AddHours(4)), priority: ConversationPriority.Urgent,
            contactRedirects: [new ContactRedirect(Guid.NewGuid(), new ContactId("old"))]), new StatusChanged(ConversationStatus.Open, ConversationStatus.Snoozed(At.AddHours(4))));
        Assert.AreEqual("applied:2", Describe(snoozed));
        Assert.AreEqual("conflict:2", Describe(await AppendAsync(primary, stored, null, new PriorityChanged(ConversationPriority.None, ConversationPriority.Low))));
        var current = (await primary.GetAsync(scope, conversation.Id))!;
        Assert.AreEqual(ConversationStatus.Snoozed(At.AddHours(4)), current.Conversation.Status);
        Assert.AreEqual(1, current.Conversation.ContactRedirects.Count);
        Assert.AreEqual(2L, current.Conversation.LastSequence);
        Assert.AreEqual("not-found", Describe(await primary.WriteAsync(scope, new ConversationWrite(NewConversation("missing"), 1))));

        // A matching revision with a stale timeline head is refused.
        var staleHead = new ConversationWrite(current.Conversation.With(lastSequence: 2), current.Revision,
            [new TimelineEvent(conversation.Id, 2, Guid.NewGuid(), At, Agent, new SystemEvent("test.stale"))]);
        Assert.AreEqual($"conflict:{current.Revision}", Describe(await primary.WriteAsync(scope, staleHead)));

        // Snapshot-only writes (no events) advance the revision without touching the timeline.
        Assert.AreEqual("applied:3", Describe(await AppendAsync(primary, current, c => c.With(priority: ConversationPriority.Low))));
        Assert.AreEqual(2, (await competing.ReadTimelineAsync(scope, conversation.Id, 0, 10, includeNotes: true)).Count);
    }

    public static async Task VerifyTimelineAndMessagesAsync(IConversationStore primary, IConversationStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "timeline-contract");
        var stored = await CreateAsync(primary, scope, NewConversation("t1"), Incoming("m1", "Hello\nthere", "ext-1"));
        var appended = Assert.IsInstanceOfType<ConversationWriteApplied>((await AppendAsync(competing, stored, null,
            new NoteEvent(new MessageId("n1"), "Private note", [new AttachmentRef("blob://n", "screen.png", "image/png", 7)]),
            Outgoing("m2"),
            new AssignmentChanged(null, new UserId("agent")),
            new LabelsChanged([new LabelId("vip")], []),
            new AttributeChanged("plan", null, new ConversationTextValue("pro")),
            new ParticipantChanged(new UserId("lead"), ParticipantChange.Added),
            new ContactChanged(new ContactId("ada"), new ContactId("ada-2"), Guid.NewGuid()),
            new TeamChanged(null, new TeamId("billing")),
            new ExtensionEvent("sla.breached", 2, """{ "policy": "gold", "target": [1, 2] }"""))).Value);
        Assert.AreEqual(10L, appended.Conversation.Conversation.LastSequence);
        Assert.IsNotNull(appended.Entry);
        Assert.AreEqual(ConversationChangeKind.EventsAppended, appended.Entry.Change.Kind);
        CollectionAssert.AreEqual(new[] { new MessageId("m2") }, appended.Entry.Change.OutgoingMessages.ToArray());

        var all = await primary.ReadTimelineAsync(scope, new ConversationId("t1"), 0, 50, includeNotes: true);
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).Select(index => (long)index).ToArray(), all.Select(item => item.Sequence).ToArray());
        for (var index = 1; index < all.Count; index++)
            Assert.AreEqual(appended.Events[index - 1].Event.Value, all[index].Event.Value, $"event {index + 1} round-trips");
        Assert.AreEqual(Ingest, all[0].Actor);
        Assert.AreEqual("Hello\nthere", ((MessageEvent)all[0].Event.Value!).Body);
        Assert.AreEqual("{\"policy\":\"gold\",\"target\":[1,2]}", ((ExtensionEvent)all[^1].Event.Value!).Payload);
        var withoutNotes = await competing.ReadTimelineAsync(scope, new ConversationId("t1"), 0, 50, includeNotes: false);
        Assert.AreEqual(9, withoutNotes.Count);
        Assert.IsFalse(withoutNotes.Any(item => item.Event.Kind == ConversationEventKind.Note));
        var paged = await competing.ReadTimelineAsync(scope, new ConversationId("t1"), 3, 2, includeNotes: true);
        CollectionAssert.AreEqual(new long[] { 4, 5 }, paged.Select(item => item.Sequence).ToArray());

        // Message rows: lookup by id and by external id (scoped to inbox); duplicates are detected.
        var message = (await competing.GetMessageAsync(scope, new ConversationId("t1"), new MessageId("m2")))!;
        Assert.AreEqual(MessageDirection.Outgoing, message.Direction);
        Assert.AreEqual(3L, message.Sequence);
        Assert.AreEqual(MessageDeliveryState.Pending, message.Delivery.State);
        Assert.AreEqual(1L, message.DeliveryRevision);
        Assert.IsNull(await competing.GetMessageAsync(scope, new ConversationId("t1"), new MessageId("n1")), "notes have no delivery row");
        Assert.AreEqual(new MessageId("m1"), (await primary.FindMessageByExternalIdAsync(scope, new InboxId("email"), "ext-1"))!.Message);
        Assert.IsNull(await primary.FindMessageByExternalIdAsync(scope, new InboxId("sms"), "ext-1"));

        var current = (await primary.GetAsync(scope, new ConversationId("t1")))!;
        Assert.AreEqual("duplicate:m1", Describe(await AppendAsync(primary, current, null, Incoming("m3", externalId: "ext-1"))));
        Assert.AreEqual("rejected:DuplicateMessageId", Describe(await AppendAsync(primary, current, null, Incoming("m2"))));
        Assert.AreEqual(current.Revision, (await primary.GetAsync(scope, new ConversationId("t1")))!.Revision, "failed writes change nothing");
        Assert.AreEqual(10, (await primary.ReadTimelineAsync(scope, new ConversationId("t1"), 0, 50, true)).Count);
        // The same external id in another inbox is distinct.
        await CreateAsync(competing, scope, NewConversation("t2", inbox: "sms"), Incoming("m1", externalId: "ext-1"));

        // Starting a conversation whose first message duplicates an existing one writes nothing.
        Assert.AreEqual("duplicate:m1", Describe(await primary.WriteAsync(scope, new ConversationWrite(NewConversation("t3").With(lastSequence: 1), null,
            [new TimelineEvent(new ConversationId("t3"), 1, Guid.NewGuid(), At, Ingest, Incoming("m9", externalId: "ext-1"))]))));
        Assert.IsNull(await primary.GetAsync(scope, new ConversationId("t3")));
    }

    public static async Task VerifyDeliveryAsync(IConversationStore primary, IConversationStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "delivery-contract");
        var stored = await CreateAsync(primary, scope, NewConversation("d1"), Outgoing("m1"), Outgoing("m2"));
        var conversation = stored.Conversation;
        ConversationChange Delivered(string message, MessageDeliveryState state) =>
            new(Guid.NewGuid(), scope, conversation.Id, conversation.Inbox, stored.Revision, ConversationChangeKind.DeliveryUpdated, [], new MessageId(message), state, At);

        var sent = new MessageDeliveryWrite(conversation.Id, new MessageId("m1"), 1, new MessageDelivery(MessageDeliveryState.Sent, At.AddSeconds(1)), "prov-1",
            Delivered("m1", MessageDeliveryState.Sent));
        var applied = await primary.UpdateDeliveryAsync(scope, sent);
        Assert.AreEqual("applied:2", Describe(applied));
        Assert.AreEqual(ConversationChangeKind.DeliveryUpdated, ((MessageDeliveryApplied)applied.Value!).Entry.Change.Kind);
        Assert.AreEqual("conflict:2", Describe(await competing.UpdateDeliveryAsync(scope, sent)));
        Assert.AreEqual(stored.Revision, (await competing.GetAsync(scope, conversation.Id))!.Revision, "delivery updates do not change the conversation revision");
        Assert.AreEqual(new MessageId("m1"), (await competing.FindMessageByExternalIdAsync(scope, conversation.Inbox, "prov-1"))!.Message);

        var failed = new MessageDeliveryWrite(conversation.Id, new MessageId("m1"), 2, new MessageDelivery(MessageDeliveryState.Failed, At.AddSeconds(2), "rejected"), null,
            Delivered("m1", MessageDeliveryState.Failed));
        Assert.AreEqual("applied:3", Describe(await competing.UpdateDeliveryAsync(scope, failed)));
        var timeline = await primary.ReadTimelineAsync(scope, conversation.Id, 0, 10, includeNotes: false);
        var first = (MessageEvent)timeline[0].Event.Value!;
        Assert.AreEqual(MessageDeliveryState.Failed, first.Delivery.State);
        Assert.AreEqual("rejected", first.Delivery.FailureCode);
        Assert.AreEqual("prov-1", first.ExternalId);
        Assert.AreEqual(MessageDeliveryState.Pending, ((MessageEvent)timeline[1].Event.Value!).Delivery.State);

        // Recording a provider id already held by another message in the inbox is a duplicate; nothing changes.
        var stolen = new MessageDeliveryWrite(conversation.Id, new MessageId("m2"), 1, new MessageDelivery(MessageDeliveryState.Sent, At), "prov-1",
            Delivered("m2", MessageDeliveryState.Sent));
        Assert.AreEqual("duplicate:m1", Describe(await primary.UpdateDeliveryAsync(scope, stolen)));
        Assert.AreEqual(1L, (await primary.GetMessageAsync(scope, conversation.Id, new MessageId("m2")))!.DeliveryRevision);
        Assert.AreEqual("not-found", Describe(await primary.UpdateDeliveryAsync(scope, new MessageDeliveryWrite(conversation.Id, new MessageId("missing"), 1,
            new MessageDelivery(MessageDeliveryState.Sent, At), null, Delivered("missing", MessageDeliveryState.Sent)))));
    }

    public static async Task VerifyListAndFiltersAsync(IConversationStore store)
    {
        var scope = TestScopes.Create(workspaceId: "list-contract");
        var other = TestScopes.Create(workspaceId: "list-contract-other");
        var rows = new List<Conversation>();
        for (var index = 0; index < 24; index++)
        {
            var conversation = NewConversation($"l{index:D2}", inbox: index % 3 == 0 ? "email" : "chat", contact: $"contact-{index % 4}",
                team: index % 2 == 0 ? "billing" : index % 5 == 0 ? null : "support", assignee: index % 4 == 0 ? null : $"agent-{index % 3}",
                labels: index % 3 == 1 ? ["vip"] : [], attributes: index % 2 == 0 ? [new("region", new ConversationTextValue(index % 4 == 0 ? "eu" : "us"))] : [],
                activity: At.AddMinutes(index % 6), status: index % 7 == 0 ? ConversationStatus.Resolved : ConversationStatus.Open);
            rows.Add((await CreateAsync(store, scope, conversation)).Conversation);
        }
        await CreateAsync(store, other, NewConversation("l00"));

        var allIds = Expected(rows, scope, new ConversationListQuery(limit: 100), AllowAllFilter.Instance);
        Assert.AreEqual(24, allIds.Count);
        CollectionAssert.AreEqual(allIds, await PagedAsync(store, scope, new ConversationListQuery(limit: 5), AllowAllFilter.Instance));

        AccessFilter[] filters =
        [
            AllowAllFilter.Instance,
            DenyAllFilter.Instance,
            UnknownFilter.Instance,
            new ScopeFilter(scope),
            new ScopeFilter(other),
            new TeamInFilter([new TeamId("billing")]),
            new NotFilter(new TeamInFilter([new TeamId("billing")])),
            new InboxInFilter([new InboxId("email")]),
            new AssigneeIsFilter(new UserId("agent-1")),
            new NotFilter(new AssigneeIsFilter(new UserId("agent-1"))),
            new HasLabelFilter(new LabelId("vip")),
            new NotFilter(new HasLabelFilter(new LabelId("vip"))),
            new AttributeEqualsFilter("region", "eu"),
            new NotFilter(new AttributeEqualsFilter("region", "eu")),
            new AttributeInFilter("region", ["eu", "us"]),
            new AndFilter(new ScopeFilter(scope), new OrFilter(new TeamInFilter([new TeamId("support")]), new HasLabelFilter(new LabelId("vip")))),
            new AndFilter(new ScopeFilter(scope), new NotFilter(new OrFilter(new InboxInFilter([new InboxId("chat")]), new AttributeEqualsFilter("region", "us")))),
        ];
        ConversationListQuery[] queries =
        [
            new(limit: 7),
            new(inbox: new InboxId("chat"), limit: 4),
            new(status: ConversationStatusKind.Resolved),
            new(assignee: new UserId("agent-2"), limit: 3),
            new(unassigned: true, limit: 3),
            new(team: new TeamId("billing"), limit: 4),
            new(label: new LabelId("vip"), limit: 2),
            new(contact: new ContactId("contact-1"), limit: 2),
            new(inbox: new InboxId("email"), team: new TeamId("billing"), limit: 2),
        ];
        foreach (var query in queries)
            foreach (var filter in filters)
                CollectionAssert.AreEqual(Expected(rows, scope, query, filter), await PagedAsync(store, scope, query, filter), $"query/filter parity: {filter.GetType().Name}");

        var thread = new ChannelThreadRef("email", "thread-1");
        await CreateAsync(store, scope, NewConversation("th-old", thread: thread, status: ConversationStatus.Resolved));
        await CreateAsync(store, scope, NewConversation("th-closed", thread: thread, status: ConversationStatus.Closed));
        Assert.AreEqual(new ConversationId("th-old"), (await store.FindByThreadAsync(scope, new InboxId("email"), thread))!.Id);
        Assert.IsNull(await store.FindByThreadAsync(scope, new InboxId("chat"), thread));
        Assert.IsNull(await store.FindByThreadAsync(scope, new InboxId("email"), new ChannelThreadRef("sms", "thread-1")));
    }

    public static async Task VerifyDeleteAsync(IConversationStore primary, IConversationStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "delete-contract");
        var stored = await CreateAsync(primary, scope, NewConversation("x1"), Incoming("m1", externalId: "ext-x"), new NoteEvent(new MessageId("n1"), "note"));
        ConversationChange Deleted(long revision) => new(Guid.NewGuid(), scope, stored.Id, stored.Conversation.Inbox, revision, ConversationChangeKind.Deleted, [], null, null, At);
        Assert.AreEqual("conflict:1", Describe(await competing.DeleteAsync(scope, stored.Id, 2, Deleted(2))));
        var deleted = await competing.DeleteAsync(scope, stored.Id, 1, Deleted(1));
        Assert.AreEqual("applied:1", Describe(deleted));
        Assert.AreEqual(ConversationChangeKind.Deleted, ((ConversationWriteApplied)deleted.Value!).Entry!.Change.Kind);
        Assert.IsNull(await primary.GetAsync(scope, stored.Id));
        Assert.AreEqual(0, (await primary.ReadTimelineAsync(scope, stored.Id, 0, 10, true)).Count);
        Assert.IsNull(await primary.FindMessageByExternalIdAsync(scope, stored.Conversation.Inbox, "ext-x"));
        Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(scope, stored.Id, 1, Deleted(1))));
        // The identifier and external id can be reused after deletion.
        await CreateAsync(primary, scope, NewConversation("x1"), Incoming("m1", externalId: "ext-x"));
    }

    /// <summary>Concurrent appenders that re-read on conflict all land, with gapless sequences and one outbox entry each.</summary>
    public static async Task VerifyConcurrentAppendsAsync(IConversationStore primary, IConversationStore competing, int writers = 12)
    {
        var scope = TestScopes.Create(workspaceId: "concurrency-contract");
        var stored = await CreateAsync(primary, scope, NewConversation("cc"));
        var stores = new[] { primary, competing };
        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            var store = stores[writer % 2];
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var current = (await store.GetAsync(scope, stored.Id))!;
                var result = await AppendAsync(store, current, null, Outgoing($"w{writer}"));
                if (result.Value is ConversationWriteApplied)
                    return;
                Assert.IsInstanceOfType<ConversationWriteConflict>(result.Value);
            }
            Assert.Fail("A writer never won.");
        })));
        var timeline = await primary.ReadTimelineAsync(scope, stored.Id, 0, 200, includeNotes: true);
        CollectionAssert.AreEqual(Enumerable.Range(1, writers).Select(index => (long)index).ToArray(), timeline.Select(item => item.Sequence).ToArray());
        Assert.AreEqual(writers, timeline.Select(item => ((MessageEvent)item.Event.Value!).Id).Distinct().Count());
        var final = (await competing.GetAsync(scope, stored.Id))!;
        Assert.AreEqual(1L + writers, final.Revision);
        Assert.AreEqual(writers, final.Conversation.LastSequence);
    }

    /// <summary>Concurrent writes of the same inbound external id admit exactly one message.</summary>
    public static async Task VerifyConcurrentDuplicateInboundAsync(IConversationStore primary, IConversationStore competing, int writers = 8)
    {
        var scope = TestScopes.Create(workspaceId: "duplicate-contract");
        var conversations = new List<StoredConversation>();
        for (var index = 0; index < writers; index++)
            conversations.Add(await CreateAsync(primary, scope, NewConversation($"dup{index}")));
        var stores = new[] { primary, competing };
        var results = await Task.WhenAll(conversations.Select((stored, index) =>
            Task.Run(() => AppendAsync(stores[index % 2], stored, null, Incoming($"m{index}", externalId: "same-provider-id")))));
        Assert.AreEqual(1, results.Count(result => result.Value is ConversationWriteApplied));
        Assert.AreEqual(writers - 1, results.Count(result => result.Value is ConversationDuplicateMessage));
        var holder = (await primary.FindMessageByExternalIdAsync(scope, new InboxId("email"), "same-provider-id"))!;
        Assert.IsTrue(results.Where(result => result.Value is ConversationDuplicateMessage).All(result => ((ConversationDuplicateMessage)result.Value!).Existing.Message == holder.Message));
    }

    public static async Task VerifyOutboxAsync(IConversationStore primaryStore, IConversationOutbox primary, IConversationOutbox competing, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "outbox-contract");
        Assert.AreEqual(0, (await primary.ClaimAsync("unregistered", 10, TimeSpan.FromMinutes(1))).Count);
        await CreateAsync(primaryStore, scope, NewConversation("before-registration"));
        await primary.RegisterConsumerAsync("realtime");
        await competing.RegisterConsumerAsync("realtime");
        await competing.RegisterConsumerAsync("sla");
        CollectionAssert.IsSubsetOf(new[] { "realtime", "sla" }, (await primary.ListConsumersAsync()).ToArray());

        var a = await CreateAsync(primaryStore, scope, NewConversation("oa"), Incoming("a1"));
        var b = await CreateAsync(primaryStore, scope, NewConversation("ob"));
        var a2 = Assert.IsInstanceOfType<ConversationWriteApplied>((await AppendAsync(primaryStore, a, null, Outgoing("a2"))).Value);

        // Per-conversation ordering: the second entry of "oa" waits for the first.
        var lease = TimeSpan.FromMinutes(1);
        var first = await primary.ClaimAsync("realtime", 10, lease);
        CollectionAssert.AreEqual(new[] { "oa", "ob" }, first.Select(claim => claim.Entry.Change.Conversation.Value).ToArray());
        Assert.IsTrue(first.All(claim => claim.Fence == 1 && claim.Attempt == 1 && string.Equals(claim.Consumer, "realtime", StringComparison.Ordinal)));
        Assert.AreEqual(ConversationChangeKind.Started, first[0].Entry.Change.Kind);
        Assert.AreEqual(scope, first[0].Entry.Change.Scope);
        Assert.AreEqual(0, (await competing.ClaimAsync("realtime", 10, lease)).Count, "leased entries and their successors are not claimable");

        Assert.IsTrue(await competing.AcknowledgeAsync("realtime", first[0].Entry.Sequence, first[0].Fence));
        Assert.IsFalse(await competing.AcknowledgeAsync("realtime", first[0].Entry.Sequence, first[0].Fence), "acknowledged once");
        var next = await competing.ClaimAsync("realtime", 10, lease);
        Assert.AreEqual(a2.Entry!.Sequence, next.Single().Entry.Sequence);
        Assert.AreEqual(a2.Entry.Change, next.Single().Entry.Change);

        // Release schedules a retry; the retry is a new fence and attempt.
        Assert.IsTrue(await primary.ReleaseAsync("realtime", next[0].Entry.Sequence, next[0].Fence, clock.GetUtcNow().AddSeconds(30)));
        Assert.AreEqual(0, (await primary.ClaimAsync("realtime", 10, lease)).Count);
        clock.Advance(TimeSpan.FromSeconds(31));
        var retried = await primary.ClaimAsync("realtime", 10, lease);
        Assert.AreEqual(2L, retried.Single().Fence);
        Assert.AreEqual(2, retried.Single().Attempt);
        Assert.IsFalse(await primary.AcknowledgeAsync("realtime", retried[0].Entry.Sequence, next[0].Fence), "stale fence");

        // An expired lease is reclaimable and the old fence is dead.
        clock.Advance(lease + TimeSpan.FromSeconds(1));
        Assert.IsFalse(await primary.AcknowledgeAsync("realtime", retried[0].Entry.Sequence, retried[0].Fence), "expired lease");
        var reclaimed = await competing.ClaimAsync("realtime", 10, lease);
        Assert.AreEqual(3L, reclaimed.Single(claim => claim.Entry.Sequence == retried[0].Entry.Sequence).Fence);
        var obClaim = first[1];
        Assert.IsTrue(reclaimed.Any(claim => claim.Entry.Sequence == obClaim.Entry.Sequence), "the expired 'ob' lease is reclaimed too");

        // Dead-lettering parks the entry and unblocks the conversation.
        var c3 = Assert.IsInstanceOfType<ConversationWriteApplied>((await AppendAsync(primaryStore, (await primaryStore.GetAsync(scope, a.Id))!, null, Outgoing("a3"))).Value);
        Assert.IsTrue(await competing.DeadLetterAsync("realtime", retried[0].Entry.Sequence, 3));
        var afterDead = await primary.ClaimAsync("realtime", 10, lease);
        Assert.AreEqual(c3.Entry!.Sequence, afterDead.Single().Entry.Sequence);

        // Other consumers are independent; entries before registration are not delivered.
        var sla = await competing.ClaimAsync("sla", 100, lease);
        Assert.IsFalse(sla.Any(claim => string.Equals(claim.Entry.Change.Conversation.Value, "before-registration", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(new[] { "oa", "ob" }, sla.Select(claim => claim.Entry.Change.Conversation.Value).ToArray());

        // Pruning removes only fully completed entries.
        foreach (var claim in afterDead.Concat(reclaimed.Where(item => item.Entry.Sequence == obClaim.Entry.Sequence)))
            Assert.IsTrue(await primary.AcknowledgeAsync("realtime", claim.Entry.Sequence, claim.Fence));
        Assert.AreEqual(1, await primary.PruneAsync(clock.GetUtcNow().AddDays(1), 100), "only the entry no consumer still owes is pruned");
        Assert.AreEqual(0, await primary.PruneAsync(clock.GetUtcNow().AddDays(1), 100));
        foreach (var claim in sla)
            Assert.IsTrue(await primary.AcknowledgeAsync("sla", claim.Entry.Sequence, claim.Fence));
    }

    public static async Task VerifyDefinitionsAsync(IConversationAttributeDefinitionStore primary, IConversationAttributeDefinitionStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "definitions-contract");
        var plan = new ConversationAttributeDefinition("plan", ConversationAttributeType.Text, displayName: "Plan");
        Assert.AreEqual("applied:1", Describe(await primary.CreateAsync(scope, plan)));
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(scope, plan)));
        Assert.AreEqual("applied:2", Describe(await competing.UpdateAsync(scope, new ConversationAttributeDefinition("plan", ConversationAttributeType.Text, sensitive: true), 1)));
        Assert.AreEqual("conflict:2", Describe(await primary.UpdateAsync(scope, plan, 1)));
        Assert.AreEqual("rejected:TypeChanged", Describe(await primary.UpdateAsync(scope, new ConversationAttributeDefinition("plan", ConversationAttributeType.Number), 2)));
        Assert.IsTrue((await primary.GetAsync(scope, "plan"))!.Definition.Sensitive);
        Assert.AreEqual("applied:1", Describe(await primary.CreateAsync(scope, new ConversationAttributeDefinition("tier", ConversationAttributeType.List))));
        CollectionAssert.AreEqual(new[] { "plan", "tier" }, (await competing.ListAsync(scope)).Select(stored => stored.Definition.Key).ToArray());
        Assert.AreEqual(0, (await competing.ListAsync(TestScopes.Create(workspaceId: "definitions-other"))).Count);
        Assert.AreEqual("conflict:2", Describe(await primary.DeleteAsync(scope, "plan", 1)));
        Assert.AreEqual("applied:2", Describe(await competing.DeleteAsync(scope, "plan", 2)));
        Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(scope, "plan", 2)));
        Assert.AreEqual("not-found", Describe(await primary.UpdateAsync(scope, plan, 1)));
    }

    private static List<string> Expected(IEnumerable<Conversation> rows, TicketScope scope, ConversationListQuery query, AccessFilter filter) =>
        [.. rows.Where(conversation => query.Matches(conversation) && AccessFilterEvaluator.Matches(filter, ConversationAccess.Resource(scope, conversation)))
            .OrderByDescending(conversation => conversation.LastActivityAt)
            .ThenByDescending(conversation => conversation.Id.Value, StringComparer.Ordinal)
            .Select(conversation => conversation.Id.Value)];

    private static async Task<List<string>> PagedAsync(IConversationStore store, TicketScope scope, ConversationListQuery query, AccessFilter filter)
    {
        var ids = new List<string>();
        for (var pages = 0; pages < 100; pages++)
        {
            var page = await store.ListAsync(scope, query, filter);
            Assert.IsTrue(page.Conversations.Count <= query.Limit);
            Assert.IsTrue(page.Conversations.All(stored => stored.Scope == scope));
            ids.AddRange(page.Conversations.Select(stored => stored.Id.Value));
            if (page.Next is null)
                return ids;
            query = query.WithAfter(page.Next);
        }
        throw new AssertFailedException("Paging did not terminate.");
    }
}
