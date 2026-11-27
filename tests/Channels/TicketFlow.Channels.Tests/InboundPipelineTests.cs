using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;
using static TicketFlow.Channels.Tests.ChannelTestSupport;

namespace TicketFlow.Channels.Tests;

[TestClass]
public sealed class InboundPipelineTests
{
    [TestMethod]
    public async Task NewEmailStartsConversationResolvesContactAndRecordsState()
    {
        await using var host = await HostAsync(EmailChannel());
        var recorded = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m1@mail.example>", subject: "Re: Order 42")));
        Assert.IsTrue(recorded.StartedConversation);
        Assert.IsFalse(recorded.Replayed);

        var view = await ViewAsync(host, recorded.Conversation);
        Assert.AreEqual(new InboxId("email-inbox"), view.Inbox);
        var messages = await MessagesAsync(host, recorded.Conversation);
        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual("Hello", messages[0].Body);
        Assert.AreEqual("<m1@mail.example>", messages[0].ExternalId);
        Assert.AreEqual(MessageDeliveryState.Received, messages[0].Delivery.State);

        var contact = await host.Contacts.GetAsync(host.Owner, view.Contact) switch
        {
            TicketValue<ContactView> applied => applied.Value,
            ContactRefused refused => throw new AssertFailedException(refused.Reason.ToString()),
        };
        Assert.AreEqual("Ada", contact.Profile.DisplayName);
        Assert.IsFalse(contact.Identities.Single().Verification.IsVerified);

        var state = (await host.States.GetAsync(host.Scope, recorded.Conversation))!.State;
        Assert.AreEqual(new ChannelRecipient(ContactIdentityKind.Email, "ada@example.com"), state.Recipient);
        Assert.AreEqual("Order 42", state.Subject);
        Assert.AreEqual("m1@mail.example", state.LastIncomingExternalId);
        Assert.AreEqual(host.Clock.GetUtcNow(), state.LastIncomingAt);
        var references = await host.Threads.ListAsync(host.Scope, recorded.Conversation);
        Assert.AreEqual("m1@mail.example", references.Single().Reference.Reference);
        Assert.AreEqual(recorded.Message, references[0].Reference.Message);
    }

    [TestMethod]
    public async Task DuplicatesReplayWithoutNewMessagesOrContacts()
    {
        await using var host = await HostAsync(EmailChannel());
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m1@x>")));
        var again = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m1@x>", from: "someone-else@example.com")));
        Assert.IsTrue(again.Replayed);
        Assert.AreEqual(first.Conversation, again.Conversation);
        Assert.AreEqual(first.Message, again.Message);
        Assert.AreEqual(1, (await MessagesAsync(host, first.Conversation)).Count);
        Assert.AreEqual("ada@example.com", (await host.States.GetAsync(host.Scope, first.Conversation))!.State.Recipient.Value,
            "A replayed external id cannot redirect replies to another address.");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m2@x>", inReplyTo: "<m1@x>"))));
        Assert.AreEqual(1, concurrent.Select(Recorded).Select(item => item.Message).Distinct().Count());
        Assert.AreEqual(2, (await MessagesAsync(host, first.Conversation)).Count);
    }

    [TestMethod]
    public async Task EmailRepliesThreadByInReplyToAndReferences()
    {
        await using var host = await HostAsync(EmailChannel());
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var byInReplyTo = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<second@x>", inReplyTo: "<root@x>")));
        Assert.AreEqual(first.Conversation, byInReplyTo.Conversation);
        Assert.IsFalse(byInReplyTo.StartedConversation);
        var byReferences = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<third@x>", references: ["<root@x>", "<unknown@x>"])));
        Assert.AreEqual(first.Conversation, byReferences.Conversation);
        var fresh = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<other@x>")));
        Assert.AreNotEqual(first.Conversation, fresh.Conversation);
        Assert.IsTrue(fresh.StartedConversation);
    }

    [TestMethod]
    public async Task ForgedReferencesFromAnotherContactStartANewConversation()
    {
        await using var host = await HostAsync(EmailChannel());
        var victim = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var attacker = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<evil@x>", from: "mallory@example.com", inReplyTo: "<root@x>")));
        Assert.AreNotEqual(victim.Conversation, attacker.Conversation);
        Assert.AreEqual(1, (await MessagesAsync(host, victim.Conversation)).Count);
    }

    [TestMethod]
    public async Task ClosedConversationsAreNotReused()
    {
        await using var host = await HostAsync(EmailChannel());
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, first.Conversation, ConversationStatus.Closed)).Value);
        var next = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<reply@x>", inReplyTo: "<root@x>")));
        Assert.AreNotEqual(first.Conversation, next.Conversation);
        Assert.IsTrue(next.StartedConversation);
    }

    [TestMethod]
    public async Task ResolvedConversationsReopenOnReply()
    {
        await using var host = await HostAsync(EmailChannel());
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, first.Conversation, ConversationStatus.Resolved)).Value);
        var next = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<reply@x>", inReplyTo: "<root@x>")));
        Assert.AreEqual(first.Conversation, next.Conversation);
        Assert.AreEqual(ConversationStatusKind.Open, (await ViewAsync(host, first.Conversation)).Status.Kind);
    }

    [TestMethod]
    public async Task WhatsAppThreadsBySenderNumber()
    {
        await using var host = await HostAsync(WhatsAppChannel());
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.1")));
        var second = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.2", body: "More")));
        Assert.AreEqual(first.Conversation, second.Conversation);
        var other = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.3", from: "+15550002222")));
        Assert.AreNotEqual(first.Conversation, other.Conversation);
        var view = await ViewAsync(host, first.Conversation);
        Assert.AreEqual("+15550001111", view.ChannelThread!.ExternalThreadId);
        var contact = await host.Contacts.GetAsync(host.Owner, view.Contact) switch
        {
            TicketValue<ContactView> applied => applied.Value,
            ContactRefused refused => throw new AssertFailedException(refused.Reason.ToString()),
        };
        Assert.IsTrue(contact.Identities.Single().Verification.IsVerified);
    }

    [TestMethod]
    public async Task RejectsUnknownMismatchedDisabledAndInvalidInput()
    {
        await using var host = await HostAsync(EmailChannel());
        var message = host.Email("<m1@x>");
        Assert.AreEqual(InboundRejection.InboxNotFound, Rejection(await host.Pipeline.ReceiveAsync(host.Worker,
            new InboundChannelMessage(host.Scope, new ChannelKind("email"), "cfg-unknown", "m", message.Sender, message.ReceivedAt, "x"))));
        Assert.AreEqual(InboundRejection.InvalidSender, Rejection(await host.Pipeline.ReceiveAsync(host.Worker,
            new InboundChannelMessage(host.Scope, new ChannelKind("email"), "cfg-email", "m", new ChannelSenderEvidence(ContactIdentityKind.Email, "not-an-email", false),
                message.ReceivedAt, "x"))));
        Assert.AreEqual(InboundRejection.Forbidden, Rejection(await host.Pipeline.ReceiveAsync(host.Viewer, message)));
        Assert.AreEqual(InboundRejection.Forbidden, Rejection(await host.Pipeline.ReceiveAsync(TestPrincipals.Automation(host.Scope), message)));
        Assert.AreEqual(InboundRejection.Forbidden, Rejection(await host.Pipeline.ReceiveAsync(TestPrincipals.ApiClient(TestScopes.Create(workspaceId: "other"), "channels"), message)));

        var inboxes = host.Services.GetService(typeof(IInboxStore)) as IInboxStore;
        var stored = (await inboxes!.GetAsync(host.Scope, new InboxId("email-inbox")))!;
        await inboxes.UpdateAsync(host.Scope, new Inbox(stored.Inbox.Id, stored.Inbox.Name, stored.Inbox.Channel, enabled: false), stored.Revision);
        Assert.AreEqual(InboundRejection.InboxDisabled, Rejection(await host.Pipeline.ReceiveAsync(host.Worker, message)));
        stored = (await inboxes.GetAsync(host.Scope, new InboxId("email-inbox")))!;
        await inboxes.UpdateAsync(host.Scope, new Inbox(stored.Inbox.Id, stored.Inbox.Name, new ChannelBinding(new ChannelKind("email"), "cfg-moved")), stored.Revision);
        Assert.AreEqual(InboundRejection.ChannelMismatch, Rejection(await host.Pipeline.ReceiveAsync(host.Worker, message)));
    }

    [TestMethod]
    public async Task BlockedContactsAreDropped()
    {
        await using var host = await HostAsync(EmailChannel());
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m1@x>")));
        var view = await ViewAsync(host, first.Conversation);
        var contact = (TicketValue<ContactView>)(await host.Contacts.GetAsync(host.Owner, view.Contact)).Value!;
        Assert.IsInstanceOfType<TicketValue<ContactView>>((await host.Contacts.UpdateAsync(host.Owner, view.Contact, contact.Value.Revision, [new SetContactBlocked(true)])).Value);
        Assert.AreEqual(InboundRejection.ContactBlocked, Rejection(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m2@x>"))));
    }

    [TestMethod]
    public async Task OlderMessagesDoNotRewindChannelState()
    {
        await using var host = await HostAsync(EmailChannel());
        var now = host.Clock.GetUtcNow();
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<new@x>", receivedAt: now)));
        Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<old@x>", inReplyTo: "<new@x>", receivedAt: now.AddHours(-1))));
        var state = (await host.States.GetAsync(host.Scope, first.Conversation))!.State;
        Assert.AreEqual(now, state.LastIncomingAt);
        Assert.AreEqual("new@x", state.LastIncomingExternalId);
    }

    [TestMethod]
    public async Task EmailRepliesFallBackToSubjectTicketTagAndEnforceContactAndClosedBoundaries()
    {
        await using var host = await HostAsync(EmailChannel());
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>", subject: "Order 42")));
        var taggedSubject = ChannelTicketTag.FormatSubject("Order 42", first.Conversation, includeReplyPrefix: true);
        Assert.AreEqual($"Re: Order 42 [Ticket #{first.Conversation.Value}]", taggedSubject);

        // No In-Reply-To / References headers: correlates by [Ticket #<id>] for the same contact.
        var byTag = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<reply-no-headers@x>", subject: taggedSubject)));
        Assert.AreEqual(first.Conversation, byTag.Conversation);
        Assert.IsFalse(byTag.StartedConversation);
        Assert.AreEqual("Order 42", (await host.States.GetAsync(host.Scope, first.Conversation))!.State.Subject);

        // Another contact cannot inject into the conversation by copying the subject ticket tag.
        var spoofed = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<spoof@x>", from: "mallory@example.com", subject: taggedSubject)));
        Assert.AreNotEqual(first.Conversation, spoofed.Conversation);
        Assert.IsTrue(spoofed.StartedConversation);
        Assert.AreEqual(2, (await MessagesAsync(host, first.Conversation)).Count);

        // Closed conversations are never reopened via subject tag either.
        Assert.IsInstanceOfType<ConversationApplied>((await host.Conversations.ChangeStatusAsync(host.Owner, first.Conversation, ConversationStatus.Closed)).Value);
        var afterClose = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<after-close@x>", subject: taggedSubject)));
        Assert.AreNotEqual(first.Conversation, afterClose.Conversation);
        Assert.IsTrue(afterClose.StartedConversation);
    }

    [TestMethod]
    public async Task ReplayedExternalMessageIdDoesNotInjectOrEvictThreadReferences()
    {
        await using var host = await HostAsync(EmailChannel());
        var inbox = new InboxId("email-inbox");
        var first = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var originalRefs = (await host.Threads.ListAsync(host.Scope, first.Conversation))
            .Select(r => r.Reference.Reference)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "root@x" }, originalRefs);

        // Replay the same ExternalMessageId repeatedly with fabricated In-Reply-To and a full batch of fabricated References.
        for (var round = 0; round < 20; round++)
        {
            var fabricatedRefs = Enumerable.Range(1, InboundChannelMessage.MaximumReferences)
                .Select(i => $"<forged-{round}-{i}@evil.example>")
                .ToArray();
            var replayed = Recorded(await host.Pipeline.ReceiveAsync(host.Worker,
                host.Email("<root@x>", from: "mallory@evil.example", inReplyTo: $"<forged-reply-{round}@evil.example>", references: fabricatedRefs)));
            Assert.IsTrue(replayed.Replayed);
            Assert.AreEqual(first.Conversation, replayed.Conversation);
        }

        var afterRefs = (await host.Threads.ListAsync(host.Scope, first.Conversation))
            .Select(r => r.Reference.Reference)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "root@x" }, afterRefs,
            "Replayed ExternalMessageId must not insert untrusted References/In-Reply-To or evict existing references.");
        Assert.IsNull(await host.Threads.FindConversationAsync(host.Scope, inbox, ["forged-reply-0@evil.example", "forged-0-1@evil.example"]));
        Assert.AreEqual(first.Conversation, await host.Threads.FindConversationAsync(host.Scope, inbox, ["root@x"]));
    }
}
