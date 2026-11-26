using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Events.StoreContracts;

/// <summary>
/// Behavioral contract every <see cref="IEventLog"/> + <see cref="IEventSubscriptionStore"/> must satisfy. <c>primary</c> and
/// <c>competing</c> must be independent clients of the same storage, and their clocks must report <c>now</c> (lease checks).
/// Each verification uses a fresh scope.
/// </summary>
public static class EventStoreContract
{
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    public static TicketScope NewScope() => TestScopes.Create(workspaceId: "ev-" + Guid.NewGuid().ToString("N"));

    public static IntegrationEventDraft Draft(string conversation = "c1", string? source = null, string inbox = "i1", string? team = "t1", DateTimeOffset? at = null)
    {
        var data = new ConversationAssignedData(new ConversationId(conversation), new InboxId(inbox), null, new UserId("ann"));
        return new(new IntegrationEvent(EventText.EventId(source ?? Guid.NewGuid().ToString("N"), data.Type), at ?? Now, new EventActor("user", "owner"), data),
            new EventResource(new ConversationId(conversation), new InboxId(inbox), team is null ? null : new TeamId(team), new UserId("ann"), new ContactId("ada")));
    }

    public static EventSubscription Subscription(string id = "s1", EventPrivacy privacy = EventPrivacy.IdsOnly, bool enabled = true) =>
        new(new EventSubscriptionId(id), "Sub " + id, new EventSinkRef("in-process", "recorder"), EventSubscriptionFilter.Everything, new ApiClientId("hooks"), privacy, enabled);

    public static async Task VerifyAppendAsync(IEventLog primary, IEventLog competing)
    {
        var scope = NewScope();
        var other = NewScope();
        Assert.AreEqual(new EventLogHead(null, 0), await primary.GetHeadAsync(scope));
        var a = Draft(source: "a");
        var b = Draft(source: "b", conversation: "c2", team: null);
        var first = await primary.AppendAsync(scope, [a, b, a], Now);
        Assert.AreEqual(1, first.Duplicates);
        CollectionAssert.AreEqual(new long[] { 1, 2 }, first.Appended.Select(item => item.Sequence).ToArray());
        var again = await competing.AppendAsync(scope, [b, Draft(source: "c")], Now.AddSeconds(1));
        Assert.AreEqual(1, again.Duplicates);
        Assert.AreEqual(3, again.Appended.Single().Sequence);
        Assert.AreEqual(1, (await primary.AppendAsync(other, [a], Now)).Appended.Single().Sequence, "Sequences and ids are per scope.");

        var read = await competing.ReadAsync(scope, 0, 10);
        Assert.AreEqual(3, read.Count);
        Assert.AreEqual(a.Event.Id, read[0].Event.Id);
        Assert.AreEqual(IntegrationEventCodec.EncodeData(a.Event.Data), IntegrationEventCodec.EncodeData(read[0].Event.Data));
        Assert.AreEqual(a.Resource, read[0].Resource);
        Assert.AreEqual(a.Event.Actor, read[0].Event.Actor);
        Assert.AreEqual(Now, read[0].RecordedAt);
        Assert.AreEqual(b.Resource, read[1].Resource);
        Assert.AreEqual(2, (await primary.ReadAsync(scope, 1, 10)).Count);
        Assert.AreEqual(1, (await primary.ReadAsync(scope, 0, 1)).Count);
        Assert.AreEqual(new EventLogHead(1, 3), await competing.GetHeadAsync(scope));

        // Concurrent appenders: gapless, no duplicates.
        var drafts = Enumerable.Range(0, 40).Select(index => Draft(source: "p" + index, conversation: "c" + index % 4)).ToList();
        await Task.WhenAll(drafts.Chunk(4).Select((chunk, index) => (index % 2 == 0 ? primary : competing).AppendAsync(scope, chunk, Now)));
        await Task.WhenAll(drafts.Chunk(5).Select((chunk, index) => (index % 2 == 0 ? competing : primary).AppendAsync(scope, chunk, Now)));
        var all = await primary.ReadAsync(scope, 0, 100);
        CollectionAssert.AreEqual(Enumerable.Range(1, 43).Select(index => (long)index).ToArray(), all.Select(item => item.Sequence).ToArray());
        Assert.AreEqual(43, all.Select(item => item.Event.Id).Distinct().Count());
    }

    public static async Task VerifyPruneAndCheckpointsAsync(IEventLog primary, IEventLog competing)
    {
        var scope = NewScope();
        await primary.AppendAsync(scope, [Draft(), Draft()], Now.AddDays(-10));
        await primary.AppendAsync(scope, [Draft()], Now);
        var pruned = 0;
        while (await competing.PruneAsync(Now.AddDays(-1), 1) is var count && count > 0)
            pruned += count;
        Assert.IsTrue(pruned >= 2);
        Assert.AreEqual(new EventLogHead(3, 3), await primary.GetHeadAsync(scope));
        Assert.AreEqual(4, (await primary.AppendAsync(scope, [Draft()], Now)).Appended.Single().Sequence, "Sequences are never reused after pruning.");

        Assert.IsNull(await primary.GetCheckpointAsync(scope, "contacts"));
        Assert.IsFalse(await primary.SetCheckpointAsync(scope, "contacts", 5, 1));
        Assert.IsTrue(await primary.SetCheckpointAsync(scope, "contacts", 5, null));
        Assert.IsFalse(await competing.SetCheckpointAsync(scope, "contacts", 7, null));
        Assert.IsTrue(await competing.SetCheckpointAsync(scope, "contacts", 7, 5));
        Assert.AreEqual(7, await primary.GetCheckpointAsync(scope, "contacts"));
        Assert.IsNull(await primary.GetCheckpointAsync(NewScope(), "contacts"));
    }

    public static async Task VerifySubscriptionsAsync(IEventSubscriptionStore primary, IEventSubscriptionStore competing)
    {
        var scope = NewScope();
        var created = Applied(await primary.CreateAsync(scope, Subscription(), 4, Now));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(Now, created.CreatedAt);
        Assert.IsInstanceOfType<StoreRejected<EventWriteRejection>>((await competing.CreateAsync(scope, Subscription(), 0, Now)).Value);
        var loaded = (await competing.GetAsync(scope, new EventSubscriptionId("s1")))!;
        Assert.AreEqual(Subscription(), loaded.Subscription);
        Assert.AreEqual(new EventDeliveryState(4, 0, Now, null, null), await competing.GetStateAsync(scope, new EventSubscriptionId("s1")));
        Assert.IsNull(await competing.GetAsync(NewScope(), new EventSubscriptionId("s1")));

        var changed = new EventSubscription(new EventSubscriptionId("s1"), "Renamed", new EventSinkRef("webhook", "https://hooks.example.com/x"),
            new EventSubscriptionFilter([new EventTypePattern("ticketflow.conversation.*")], [new InboxId("i1")], [new TeamId("t1")]), new ApiClientId("hooks"),
            EventPrivacy.Content, enabled: false, batchSize: 7);
        Assert.IsInstanceOfType<StoreConflict>((await competing.UpdateAsync(scope, changed, 2)).Value);
        Assert.AreEqual(2, Applied(await competing.UpdateAsync(scope, changed, 1)).Revision);
        Assert.AreEqual(changed, (await primary.GetAsync(scope, changed.Id))!.Subscription);
        Assert.IsInstanceOfType<StoreNotFound>((await primary.UpdateAsync(scope, Subscription("missing"), 1)).Value);

        await primary.CreateAsync(scope, Subscription("s0"), 0, Now);
        CollectionAssert.AreEqual(new[] { "s0", "s1" }, (await competing.ListAsync(scope)).Select(item => item.Subscription.Id.Value).ToArray());
        Assert.IsInstanceOfType<StoreConflict>((await competing.DeleteAsync(scope, changed.Id, 1)).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredEventSubscription>>((await competing.DeleteAsync(scope, changed.Id, 2)).Value);
        Assert.IsNull(await primary.GetStateAsync(scope, changed.Id));

        var limited = NewScope();
        for (var index = 0; index < IEventSubscriptionStore.MaximumSubscriptionsPerWorkspace; index++)
            Applied(await primary.CreateAsync(limited, Subscription("l" + index), 0, Now));
        Assert.AreEqual(EventWriteRejection.LimitReached, ((StoreRejected<EventWriteRejection>)(await competing.CreateAsync(limited, Subscription("over"), 0, Now)).Value!).Reason);
    }

    /// <summary>Claims, completion, failure, dead letters and replay. Stores must use a clock at <see cref="Now"/>.</summary>
    public static async Task VerifyDeliveryAsync(IEventLog log, IEventSubscriptionStore primary, IEventSubscriptionStore competing)
    {
        var scope = NewScope();
        var id = new EventSubscriptionId("s1");
        Applied(await primary.CreateAsync(scope, Subscription(), 0, Now));
        Applied(await primary.CreateAsync(scope, Subscription("off", enabled: false), 0, Now));
        Assert.AreEqual(0, (await competing.ClaimAsync(Now, 10, TimeSpan.FromMinutes(1))).Count(claim => claim.Subscription.Scope == scope), "Nothing to deliver.");
        await log.AppendAsync(scope, [Draft(), Draft(), Draft()], Now);

        var claim = (await primary.ClaimAsync(Now, 100, TimeSpan.FromMinutes(1))).Single(item => item.Subscription.Scope == scope);
        Assert.AreEqual(id, claim.Subscription.Subscription.Id);
        Assert.AreEqual(0, claim.State.Cursor);
        Assert.AreEqual(0, (await competing.ClaimAsync(Now, 100, TimeSpan.FromMinutes(1))).Count(item => item.Subscription.Scope == scope), "Live leases are exclusive.");
        Assert.IsFalse(await competing.CompleteAsync(scope, id, claim.Fence + 1, 2, Now, Now));
        Assert.IsTrue(await competing.CompleteAsync(scope, id, claim.Fence, 2, Now, Now, "events-pruned-1-1"));
        Assert.IsFalse(await primary.CompleteAsync(scope, id, claim.Fence, 3, Now, Now), "Completion releases the lease.");
        Assert.AreEqual(new EventDeliveryState(2, 0, Now, "events-pruned-1-1", Now), await primary.GetStateAsync(scope, id));

        claim = (await competing.ClaimAsync(Now, 100, TimeSpan.FromMinutes(1))).Single(item => item.Subscription.Scope == scope);
        Assert.IsTrue(await primary.FailAsync(scope, id, claim.Fence, 1, Now.AddSeconds(30), "http-500"));
        Assert.AreEqual(new EventDeliveryState(2, 1, Now.AddSeconds(30), "http-500", Now), await primary.GetStateAsync(scope, id));
        Assert.IsFalse(await competing.CompleteAsync(scope, id, claim.Fence, 2, null, Now.AddSeconds(30)), "Failure released the lease.");
        Assert.AreEqual(0, (await primary.ClaimAsync(Now, 100, TimeSpan.FromMinutes(1))).Count(item => item.Subscription.Scope == scope), "Not before the retry time.");
        claim = (await primary.ClaimAsync(Now.AddSeconds(30), 100, TimeSpan.FromMinutes(1))).Single(item => item.Subscription.Scope == scope);
        Assert.AreEqual(1, claim.State.Attempts);

        var letter = new EventDeadLetter(id, 3, Guid.NewGuid(), IntegrationEventTypes.ConversationAssigned, 2, "http-410", Now);
        Assert.IsTrue(await competing.DeadLetterAsync(scope, id, claim.Fence, 3, [letter], Now.AddSeconds(30)));
        Assert.AreEqual(3, (await primary.GetStateAsync(scope, id))!.Cursor);
        Assert.AreEqual(0, (await primary.GetStateAsync(scope, id))!.Attempts);
        Assert.AreEqual(letter, (await competing.ListDeadLettersAsync(scope, id, 0, 10)).Single());
        Assert.AreEqual(0, (await competing.ListDeadLettersAsync(scope, id, 3, 10)).Count);
        Assert.AreEqual(0, (await primary.ClaimAsync(Now.AddMinutes(5), 100, TimeSpan.FromMinutes(1))).Count(item => item.Subscription.Scope == scope), "Caught up.");

        // Replay rewinds, clears later dead letters and fences out in-flight leases.
        await log.AppendAsync(scope, [Draft()], Now);
        claim = (await primary.ClaimAsync(Now.AddMinutes(1), 100, TimeSpan.FromMinutes(1))).Single(item => item.Subscription.Scope == scope);
        Assert.AreEqual(0, claim.State.Generation);
        Assert.IsTrue(await competing.ReplayAsync(scope, id, 1, Now));
        Assert.IsFalse(await primary.CompleteAsync(scope, id, claim.Fence, 4, Now, Now));
        Assert.AreEqual(1, (await primary.GetStateAsync(scope, id))!.Cursor);
        Assert.AreEqual(1, (await primary.GetStateAsync(scope, id))!.Generation, "Replay bumps the generation (part of delivery ids).");
        Assert.AreEqual(0, (await primary.ListDeadLettersAsync(scope, id, 0, 10)).Count);
        Assert.IsFalse(await competing.ReplayAsync(scope, new EventSubscriptionId("missing"), 0, Now));
        claim = (await primary.ClaimAsync(Now.AddMinutes(1), 100, TimeSpan.FromMinutes(1))).Single(item => item.Subscription.Scope == scope);
        Assert.AreEqual(1, claim.State.Cursor);
        Assert.IsTrue(await primary.CompleteAsync(scope, id, claim.Fence, 0, Now, Now.AddMinutes(1)));
        Assert.AreEqual(1, (await primary.GetStateAsync(scope, id))!.Cursor, "Cursors never move backwards on completion.");

        // Suspension: not claimable until updated or replayed.
        claim = (await primary.ClaimAsync(Now.AddMinutes(1), 100, TimeSpan.FromMinutes(1))).Single(item => item.Subscription.Scope == scope);
        Assert.IsTrue(await competing.SuspendAsync(scope, id, claim.Fence, "principal-forbidden"));
        Assert.AreEqual((true, "principal-forbidden"), ((await primary.GetStateAsync(scope, id))!.Suspended, (await primary.GetStateAsync(scope, id))!.LastError));
        Assert.AreEqual(0, (await primary.ClaimAsync(Now.AddHours(1), 100, TimeSpan.FromMinutes(1))).Count(item => item.Subscription.Scope == scope), "Suspended.");
        Assert.AreEqual(2, Applied(await competing.UpdateAsync(scope, Subscription(), 1)).Revision);
        Assert.IsFalse((await primary.GetStateAsync(scope, id))!.Suspended, "Updating lifts the suspension.");

        // Dead-letter retention.
        var released = (await primary.ClaimAsync(Now.AddMinutes(1), 100, TimeSpan.FromMinutes(1))).Single(item => item.Subscription.Scope == scope);
        Assert.IsTrue(await primary.DeadLetterAsync(scope, id, released.Fence, 4, [letter with { Sequence = 2, DeadLetteredAt = Now.AddDays(-40) }], Now));
        Assert.IsTrue(await competing.PruneDeadLettersAsync(Now.AddDays(-30), 10_000) >= 1);
        Assert.AreEqual(0, (await primary.ListDeadLettersAsync(scope, id, 0, 10)).Count);
    }

    /// <summary>Concurrent claimers never share a live lease.</summary>
    public static async Task VerifyConcurrentClaimsAsync(IEventLog log, IEventSubscriptionStore primary, IEventSubscriptionStore competing)
    {
        var scope = NewScope();
        for (var index = 0; index < 20; index++)
            Applied(await primary.CreateAsync(scope, Subscription("s" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)), 0, Now));
        await log.AppendAsync(scope, [Draft()], Now);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => (index % 2 == 0 ? primary : competing).ClaimAsync(Now, 5, TimeSpan.FromMinutes(1))));
        var mine = claims.SelectMany(batch => batch).Where(claim => claim.Subscription.Scope == scope).Select(claim => claim.Subscription.Subscription.Id.Value).ToList();
        Assert.AreEqual(mine.Count, mine.Distinct().Count(), "No subscription is leased twice.");
        var rest = (await primary.ClaimAsync(Now, 100, TimeSpan.FromMinutes(1))).Where(claim => claim.Subscription.Scope == scope).Select(claim => claim.Subscription.Subscription.Id.Value);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 20).Select(index => "s" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToArray(), mine.Concat(rest).ToArray());
    }

    /// <summary>More busy subscriptions than the claim limit: completion moves a subscription behind the others (round-robin).</summary>
    public static async Task VerifyClaimFairnessAsync(IEventLog log, IEventSubscriptionStore primary, IEventSubscriptionStore competing)
    {
        var scope = NewScope();
        for (var index = 0; index < 6; index++)
            Applied(await primary.CreateAsync(scope, Subscription("f" + index), 0, Now));
        await log.AppendAsync(scope, [Draft(), Draft(), Draft()], Now);
        var seen = new List<string>();
        for (var round = 1; round <= 3; round++)
        {
            var at = Now.AddSeconds(round);
            var claims = (await (round % 2 == 0 ? competing : primary).ClaimAsync(at, 2, TimeSpan.FromMinutes(1))).Where(item => item.Subscription.Scope == scope).ToList();
            Assert.AreEqual(2, claims.Count);
            foreach (var claim in claims)
            {
                seen.Add(claim.Subscription.Subscription.Id.Value);
                // One event delivered, more pending: claimable again now, but behind subscriptions that waited longer.
                Assert.IsTrue(await primary.CompleteAsync(scope, claim.Subscription.Subscription.Id, claim.Fence, 1, at, at));
            }
        }
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 6).Select(index => "f" + index).ToArray(), seen.ToArray(), "Every busy subscription is served before any repeats.");
    }

    public static StoredEventSubscription Applied(StoreWriteResult<StoredEventSubscription, EventWriteRejection> result) => result switch
    {
        StoreApplied<StoredEventSubscription> applied => applied.Value,
        StoreNotFound => throw new AssertFailedException("Not found."),
        StoreConflict conflict => throw new AssertFailedException($"Conflict at {conflict.CurrentRevision}."),
        StoreRejected<EventWriteRejection> rejected => throw new AssertFailedException($"Rejected: {rejected.Reason}."),
    };
}
