using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.StoreContracts;

/// <summary>
/// Shared contract checks for the channel state stores. <c>competing</c> is a second, independent client of the same storage (for
/// PostgreSQL a second data source; for in-memory the same instance).
/// </summary>
public static class ChannelStoreContract
{
    private static readonly ChannelKind Email = new("email");
    private static readonly ChannelKind Sms = new("sms");

    public static async Task VerifyEndpointsAsync(IChannelEndpointStore store, IChannelEndpointStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "endpoints");
        var other = TestScopes.Create(workspaceId: "endpoints-other");
        var endpoint = new ChannelEndpoint(Email, "cfg-1", new InboxId("inbox-1"), "mailgun:acct");
        var created = Applied(await store.CreateAsync(scope, endpoint));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(endpoint, created.Endpoint);
        Assert.AreEqual(scope, created.Scope);
        Assert.AreEqual(1L, Is<ChannelWriteConflict>((await competing.CreateAsync(scope, endpoint)).Value).CurrentRevision);

        Assert.AreEqual(created, await competing.GetAsync(scope, Email, "cfg-1"));
        foreach (var variant in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await store.GetAsync(variant, Email, "cfg-1"));
        Assert.IsNull(await store.GetAsync(scope, Sms, "cfg-1"));

        // Address uniqueness is per partition and kind, across workspaces.
        Assert.AreEqual(ChannelWriteRejection.AddressTaken,
            Is<ChannelWriteRejected>((await competing.CreateAsync(other, new ChannelEndpoint(Email, "cfg-2", new InboxId("inbox-2"), "mailgun:acct"))).Value).Reason);
        Applied(await store.CreateAsync(other, new ChannelEndpoint(Sms, "cfg-2", new InboxId("inbox-2"), "mailgun:acct")));
        var foreign = TestScopes.Create(tenantId: "other-tenant", workspaceId: "endpoints");
        Applied(await store.CreateAsync(foreign, new ChannelEndpoint(Email, "cfg-1", new InboxId("inbox-1"), "mailgun:acct")));

        var found = await competing.FindByAddressAsync(AccessPartition.From(scope), Email, "mailgun:acct");
        Assert.IsNotNull(found);
        Assert.AreEqual(scope, found.Scope);
        Assert.IsNull(await store.FindByAddressAsync(AccessPartition.From(scope), Email, "MAILGUN:acct"));
        Assert.AreEqual(foreign, (await store.FindByAddressAsync(AccessPartition.From(foreign), Email, "mailgun:acct"))!.Scope);

        Assert.AreEqual(1L, Is<ChannelWriteConflict>((await store.UpdateAsync(scope, endpoint with { }, 7)).Value).CurrentRevision);
        Assert.IsInstanceOfType<ChannelWriteNotFound>((await store.UpdateAsync(scope, new ChannelEndpoint(Email, "missing", new InboxId("x")), 1)).Value);
        var moved = Applied(await competing.UpdateAsync(scope, new ChannelEndpoint(Email, "cfg-1", new InboxId("inbox-3"), "mailgun:acct2"), 1));
        Assert.AreEqual(2, moved.Revision);
        Assert.IsNull(await store.FindByAddressAsync(AccessPartition.From(scope), Email, "mailgun:acct"));
        Applied(await store.CreateAsync(other, new ChannelEndpoint(Email, "cfg-3", new InboxId("inbox-2"), "mailgun:acct")));

        // Prefix listing is partition-wide, kind-specific and ordinal.
        Applied(await store.CreateAsync(other, new ChannelEndpoint(Email, "cfg-4", new InboxId("inbox-2"), "mailgun:acct:+1555")));
        Applied(await store.CreateAsync(other, new ChannelEndpoint(Email, "cfg-5", new InboxId("inbox-2"), "mailgun:acctx")));
        var nested = await competing.ListByAddressPrefixAsync(AccessPartition.From(scope), Email, "mailgun:acct:", IChannelEndpointStore.MaximumPrefixMatches);
        CollectionAssert.AreEqual(new[] { "mailgun:acct:+1555" }, nested.Select(item => item.Endpoint.Address).ToArray());
        Assert.AreEqual(other, nested[0].Scope);
        Assert.AreEqual(4, (await store.ListByAddressPrefixAsync(AccessPartition.From(scope), Email, "mailgun:acct", IChannelEndpointStore.MaximumPrefixMatches)).Count);
        Assert.AreEqual(1, (await store.ListByAddressPrefixAsync(AccessPartition.From(scope), Email, "mailgun:acct", 1)).Count);
        Assert.AreEqual(0, (await store.ListByAddressPrefixAsync(AccessPartition.From(scope), Sms, "mailgun:acct:", 10)).Count);
        Assert.AreEqual(0, (await store.ListByAddressPrefixAsync(AccessPartition.From(TestScopes.Create(tenantId: "nobody")), Email, "mailgun:", 10)).Count);

        Assert.AreEqual(2L, Is<ChannelWriteConflict>((await store.DeleteAsync(scope, Email, "cfg-1", 1)).Value).CurrentRevision);
        Applied(await store.DeleteAsync(scope, Email, "cfg-1", 2));
        Assert.IsNull(await competing.GetAsync(scope, Email, "cfg-1"));
        Assert.IsInstanceOfType<ChannelWriteNotFound>((await store.DeleteAsync(scope, Email, "cfg-1", 2)).Value);
    }

    public static async Task VerifyConversationStateAsync(IChannelConversationStateStore store, IChannelConversationStateStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "states");
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);
        var state = new ChannelConversationState(new ConversationId("c-1"), new InboxId("inbox-1"), Email,
            new ChannelRecipient(ContactIdentityKind.Email, "ada@example.com"), "thread-1", "Order 42", at, "m1@example.com");
        Assert.AreEqual(at.AddTicks(-7), state.LastIncomingAt);
        var created = Applied(await store.WriteAsync(scope, state, null));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(state, (await competing.GetAsync(scope, state.Conversation))!.State);
        Assert.AreEqual(1L, Is<ChannelWriteConflict>((await competing.WriteAsync(scope, state, null)).Value).CurrentRevision);
        foreach (var variant in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await store.GetAsync(variant, state.Conversation));

        var next = new ChannelConversationState(state.Conversation, state.Inbox, Email, new ChannelRecipient(ContactIdentityKind.Email, "ada2@example.com"), "thread-1",
            "Order 42", at.AddMinutes(1), "m2@example.com");
        Assert.AreEqual(2, Applied(await competing.WriteAsync(scope, next, 1)).Revision);
        Assert.AreEqual(2L, Is<ChannelWriteConflict>((await store.WriteAsync(scope, next, 1)).Value).CurrentRevision);
        var otherChannel = new ChannelConversationState(state.Conversation, state.Inbox, Sms, new ChannelRecipient(ContactIdentityKind.Phone, "+15550001111"));
        Assert.IsInstanceOfType<ChannelWriteConflict>((await store.WriteAsync(scope, otherChannel, 2)).Value);
        Assert.AreEqual(next, (await store.GetAsync(scope, state.Conversation))!.State);
        Assert.IsInstanceOfType<ChannelWriteNotFound>((await store.WriteAsync(scope, new ChannelConversationState(new ConversationId("c-missing"), state.Inbox, Email, state.Recipient), 1)).Value);

        // Thread lookups are per contact.
        var ada = new ContactId("ada");
        var bob = new ContactId("bob");
        foreach (var (id, contact) in new[] { ("t-a1", ada), ("t-b1", bob), ("t-a2", ada) })
            Applied(await store.WriteAsync(scope, new ChannelConversationState(new ConversationId(id), state.Inbox, Email, state.Recipient, "C1:100.1", contact: contact), null));
        CollectionAssert.AreEqual(new[] { new ConversationId("t-a2"), new ConversationId("t-a1") },
            (await competing.FindByThreadAsync(scope, state.Inbox, Email, "C1:100.1", ada)).ToArray());
        CollectionAssert.AreEqual(new[] { new ConversationId("t-b1") }, (await store.FindByThreadAsync(scope, state.Inbox, Email, "C1:100.1", bob)).ToArray());
        Assert.AreEqual(0, (await store.FindByThreadAsync(scope, state.Inbox, Sms, "C1:100.1", ada)).Count);
        Assert.AreEqual(0, (await store.FindByThreadAsync(scope, new InboxId("other"), Email, "C1:100.1", ada)).Count);
        Assert.AreEqual(0, (await store.FindByThreadAsync(TestScopes.LevelVariants(scope)[1], state.Inbox, Email, "C1:100.1", ada)).Count);
        Assert.AreEqual(ada, (await store.GetAsync(scope, new ConversationId("t-a1")))!.State.Contact);

        // Concurrent creates: exactly one wins.
        var racing = new ChannelConversationState(new ConversationId("c-race"), state.Inbox, Email, state.Recipient);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => (index % 2 == 0 ? store : competing).WriteAsync(scope, racing, null)));
        Assert.AreEqual(1, results.Count(result => result.Value is ChannelWriteApplied<StoredChannelConversationState>));

        Assert.IsTrue(await store.DeleteAsync(scope, state.Conversation));
        Assert.IsFalse(await competing.DeleteAsync(scope, state.Conversation));
        Assert.IsNull(await store.GetAsync(scope, state.Conversation));
    }

    public static async Task VerifyThreadsAsync(IChannelThreadStore store, IChannelThreadStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "threads");
        var inbox = new InboxId("inbox-1");
        var first = new ConversationId("c-1");
        var second = new ConversationId("c-2");
        var at = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(2, await store.AddAsync(scope, inbox, first, [new("root@example.com", MessageDirection.Incoming, new MessageId("m1")), new("ref@example.com", MessageDirection.Incoming)], at));
        Assert.AreEqual(1, await competing.AddAsync(scope, inbox, first, [new("root@example.com", MessageDirection.Incoming), new("out@example.com", MessageDirection.Outgoing, new MessageId("m2"))], at));
        // A reference already stored for another conversation stays with its first owner.
        Assert.AreEqual(1, await store.AddAsync(scope, inbox, second, [new("root@example.com", MessageDirection.Incoming), new("c2@example.com", MessageDirection.Incoming)], at));

        var chain = await competing.ListAsync(scope, first);
        CollectionAssert.AreEqual(new[] { "root@example.com", "ref@example.com", "out@example.com" }, chain.Select(item => item.Reference.Reference).ToArray());
        Assert.AreEqual(new MessageId("m1"), chain[0].Reference.Message);
        Assert.AreEqual(MessageDirection.Outgoing, chain[2].Reference.Direction);
        Assert.IsTrue(chain[0].Sequence < chain[1].Sequence && chain[1].Sequence < chain[2].Sequence);
        Assert.AreEqual(at, chain[0].RecordedAt);

        Assert.AreEqual(first, await store.FindConversationAsync(scope, inbox, ["unknown", "out@example.com", "c2@example.com"]));
        Assert.AreEqual(second, await store.FindConversationAsync(scope, inbox, ["c2@example.com", "root@example.com"]));
        Assert.IsNull(await store.FindConversationAsync(scope, new InboxId("inbox-2"), ["root@example.com"]));
        Assert.IsNull(await store.FindConversationAsync(scope, inbox, []));
        foreach (var variant in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await store.FindConversationAsync(variant, inbox, ["root@example.com"]));
            Assert.AreEqual(0, (await store.ListAsync(variant, first)).Count);
        }

        // Bounded: the oldest references are pruned.
        var bulk = new ConversationId("c-bulk");
        for (var batch = 0; batch < 3; batch++)
            await store.AddAsync(scope, inbox, bulk, [.. Enumerable.Range(0, 64).Select(index => new ChannelMessageReference($"bulk-{batch}-{index}", MessageDirection.Incoming))], at);
        var kept = await store.ListAsync(scope, bulk);
        Assert.AreEqual(IChannelThreadStore.MaximumReferencesPerConversation, kept.Count);
        Assert.AreEqual("bulk-2-63", kept[^1].Reference.Reference);
        Assert.AreEqual("bulk-1-0", kept[0].Reference.Reference);
        Assert.IsNull(await store.FindConversationAsync(scope, inbox, ["bulk-0-0"]));

        // Concurrent idempotent adds.
        var racing = new ConversationId("c-race");
        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => (index % 2 == 0 ? store : competing)
            .AddAsync(scope, inbox, racing, [new ChannelMessageReference("race@example.com", MessageDirection.Incoming)], at)));
        Assert.AreEqual(1, counts.Sum());
    }

    public static async Task VerifyDispatchesAsync(IChannelDispatchStore store, IChannelDispatchStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "dispatches");
        var at = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var sending = new ChannelDispatch(new ConversationId("c-1"), new MessageId("m-1"), new InboxId("inbox-1"), Email, ChannelDispatchState.Sending, 1, at);
        Assert.AreEqual(1, Applied(await store.WriteAsync(scope, sending, null)).Revision);
        Assert.AreEqual(1L, Is<ChannelWriteConflict>((await competing.WriteAsync(scope, sending, null)).Value).CurrentRevision);
        var retry = new ChannelDispatch(sending.Conversation, sending.Message, sending.Inbox, Email, ChannelDispatchState.RetryScheduled, 1, at, at.AddSeconds(30), code: "rate-limited");
        Assert.AreEqual(2, Applied(await competing.WriteAsync(scope, retry, 1)).Revision);
        Assert.IsInstanceOfType<ChannelWriteConflict>((await store.WriteAsync(scope, retry, 1)).Value);
        Assert.IsInstanceOfType<ChannelWriteConflict>((await store.WriteAsync(scope,
            new ChannelDispatch(sending.Conversation, sending.Message, new InboxId("inbox-2"), Email, ChannelDispatchState.Accepted, 2, at), 2)).Value);
        var accepted = new ChannelDispatch(sending.Conversation, sending.Message, sending.Inbox, Email, ChannelDispatchState.Accepted, 2, at.AddMinutes(1), externalMessageId: "provider-1");
        Applied(await store.WriteAsync(scope, accepted, 2));
        Assert.IsInstanceOfType<ChannelWriteNotFound>((await store.WriteAsync(scope,
            new ChannelDispatch(sending.Conversation, new MessageId("m-missing"), sending.Inbox, Email, ChannelDispatchState.Sending, 1, at), 1)).Value);
        var stored = await competing.GetAsync(scope, sending.Conversation, sending.Message);
        Assert.IsNotNull(stored);
        Assert.AreEqual(accepted, stored.Dispatch);
        Assert.AreEqual(3, stored.Revision);
        Assert.IsTrue(stored.Dispatch.IsTerminal);
        foreach (var variant in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await store.GetAsync(variant, sending.Conversation, sending.Message));
        Assert.IsNull(await store.GetAsync(scope, sending.Conversation, new MessageId("m-2")));

        var racing = new ChannelDispatch(sending.Conversation, new MessageId("m-race"), sending.Inbox, Email, ChannelDispatchState.Sending, 1, at);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => (index % 2 == 0 ? store : competing).WriteAsync(scope, racing, null)));
        Assert.AreEqual(1, results.Count(result => result.Value is ChannelWriteApplied<StoredChannelDispatch>));
    }

    private static T Applied<T>(ChannelWriteResult<T> result) => result switch
    {
        ChannelWriteApplied<T> applied => applied.Value,
        ChannelWriteNotFound => throw new AssertFailedException("Expected applied but was not found."),
        ChannelWriteConflict conflict => throw new AssertFailedException($"Expected applied but was conflict at {conflict.CurrentRevision}."),
        ChannelWriteRejected rejected => throw new AssertFailedException($"Expected applied but was rejected: {rejected.Reason}."),
    };

    private static T Is<T>(object? value) where T : class
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value!;
    }
}
