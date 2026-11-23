using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Realtime;
using TicketFlow.Workspaces;
using static TicketFlow.Channels.LiveChat.Tests.LiveChatHost;

namespace TicketFlow.Channels.LiveChat.Tests;

[TestClass]
public sealed class VisitorFlowTests
{
    [TestMethod]
    public async Task VisitorMessageBecomesAConversationAndTheAgentReplyReachesTheVisitor()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync();
        Assert.AreEqual("Hi! How can we help?", grant.Widget.GreetingMessage);
        Assert.IsTrue(grant.Widget.Online);
        var visitor = host.ConnectVisitor(grant.Context, "visitor-1");

        var accepted = Value(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "My order is late")));
        Assert.IsTrue(accepted.StartedConversation);
        var echo = visitor.Events<VisitorMessageEvent>().Single();
        Assert.AreEqual(MessageDirection.Incoming, echo.Direction);
        Assert.AreEqual(accepted.Sequence, echo.Sequence);
        var conversation = accepted.Conversation;
        var agent = await host.ConnectAgentAsync(host.Host.Agent, conversation, "agent-1");

        var replay = Value(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "My order is late")));
        Assert.IsTrue(replay.Replayed, "Client message ids make retries idempotent.");
        Assert.AreEqual(accepted.Message, replay.Message);
        var second = Value(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c2", "Any news?")));
        Assert.AreEqual(conversation, second.Conversation);
        Assert.IsFalse(second.StartedConversation);
        await host.Host.DispatchAsync();
        Assert.IsNotEmpty(agent.Events<ConversationChangedEvent>());

        var stored = (await host.Host.Services.GetRequiredService<IConversationStore>().GetAsync(host.Scope, conversation))!;
        var contact = (await host.Host.Services.GetRequiredService<IContactStore>().GetAsync(host.Scope, stored.Conversation.Contact))!;
        Assert.AreEqual("Ada", contact.Contact.Profile.DisplayName);
        Assert.AreEqual(new InboxId("chat-inbox"), stored.Conversation.Inbox);

        Assert.IsTrue(Value(await host.Visitors.SetTypingAsync(grant.Context, conversation, true)));
        Assert.IsInstanceOfType<VisitorActor>(agent.Events<TypingEvent>().Single().Actor.Value);
        Assert.IsInstanceOfType<RealtimeActionApplied>((await host.Host.Services.GetRequiredService<IRealtimeAgentActions>().SetTypingAsync(host.Host.Agent, conversation, true)).Value);
        Assert.IsInstanceOfType<SupportActor>(visitor.Events<TypingEvent>().Single().Actor.Value, "Visitors see an anonymous support actor.");

        Assert.IsInstanceOfType<ConversationApplied>((await host.Host.Conversations.AddNoteAsync(host.Host.Agent, conversation, new NoteDraft("internal only"))).Value);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Host.Replies.ReplyAsync(host.Host.Agent, conversation, new OutgoingMessageDraft("It ships today"))).Value);
        visitor.Clear();
        var run = await host.Host.DispatchAsync();
        Assert.AreEqual(0, run.Retried + run.DeadLettered);
        var pushed = visitor.Events<VisitorMessageEvent>().Single();
        Assert.AreEqual(MessageDirection.Outgoing, pushed.Direction);
        Assert.AreEqual("It ships today", pushed.Body);
        Assert.HasCount(1, visitor.Received, "Notes and agent notifications never reach the visitor.");
        var delivered = await host.Host.Services.GetRequiredService<IConversationStore>().GetMessageAsync(host.Scope, conversation, pushed.Message);
        Assert.AreEqual(MessageDeliveryState.Sent, delivered!.Delivery.State);

        // Reconnect: fetch everything after the last seen sequence.
        var page = Value(await host.Visitors.ReadMessagesAsync(grant.Context, conversation, afterSequence: echo.Sequence));
        CollectionAssert.AreEqual(new[] { "Any news?", "It ships today" }, page.Messages.Select(message => message.Body).ToArray());
        var all = Value(await host.Visitors.ReadMessagesAsync(grant.Context, conversation));
        Assert.HasCount(3, all.Messages);
        Assert.IsFalse(all.Messages.Any(message => message.Body.Contains("internal", StringComparison.Ordinal)));
        Assert.IsNull(all.NextAfterSequence);
        var paged = Value(await host.Visitors.ReadMessagesAsync(grant.Context, conversation, limit: 1));
        Assert.AreEqual(echo.Sequence, paged.NextAfterSequence);

        Assert.AreEqual(pushed.Sequence, Value(await host.Visitors.MarkReadAsync(grant.Context, conversation, pushed.Sequence)));
        Assert.AreEqual(pushed.Sequence, agent.Events<ReadReceiptEvent>().Single().UpToSequence);
        Assert.AreEqual(pushed.Sequence, Value(await host.Visitors.MarkReadAsync(grant.Context, conversation, 1)), "Cursors never move back.");
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.MarkReadAsync(grant.Context, conversation, 999)));
        var listed = Value(await host.Visitors.ListConversationsAsync(grant.Context)).Single();
        Assert.AreEqual(pushed.Sequence, listed.ReadSequence);
        Assert.IsFalse(listed.Closed);
        Assert.AreEqual(pushed.Sequence, Value(await host.Visitors.ReadMessagesAsync(grant.Context, conversation, pushed.Sequence)).ReadSequence);
    }

    [TestMethod]
    public async Task VisitorsCannotReachOtherSessionsOrConversations()
    {
        await using var host = await CreateAsync();
        var a = await host.StartAsync();
        var b = await host.StartAsync();
        var visitorB = host.ConnectVisitor(b.Context, "visitor-b");
        var accepted = Value(await host.Visitors.SendMessageAsync(a.Context, new VisitorMessageDraft("c1", "secret order details")));

        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Visitors.ReadMessagesAsync(b.Context, accepted.Conversation)));
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Visitors.SetTypingAsync(b.Context, accepted.Conversation, true)));
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Visitors.MarkReadAsync(b.Context, accepted.Conversation, 1)));
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Visitors.ReadMessagesAsync(b.Context, new ConversationId("missing"))));
        Assert.IsEmpty(Value(await host.Visitors.ListConversationsAsync(b.Context)));
        Assert.IsInstanceOfType<RealtimeSubscriptionRefused>((await host.Registry.SubscribeAsync("visitor-b", new VisitorSessionTopic(host.Scope, a.Context.Session.Session.Id))).Value);

        var other = Value(await host.Visitors.SendMessageAsync(b.Context, new VisitorMessageDraft("c1", "hello")));
        Assert.AreNotEqual(accepted.Conversation, other.Conversation, "Same client id in another session is a different message and visitor.");
        visitorB.Clear();
        Assert.IsInstanceOfType<ConversationApplied>((await host.Host.Replies.ReplyAsync(host.Host.Agent, accepted.Conversation, new OutgoingMessageDraft("for A only"))).Value);
        await host.Host.DispatchAsync();
        Assert.IsEmpty(visitorB.Received);
    }

    [TestMethod]
    public async Task PrechatIsRequiredValidatedAndNormalized()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync(prechat: false);
        Assert.AreEqual(LiveChatRefusal.PrechatRequired, Refusal(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "Hi"))));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SubmitPrechatAsync(grant.Context, new Dictionary<string, string> { ["name"] = "Ada", ["shoe"] = "42" })));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SubmitPrechatAsync(grant.Context, new Dictionary<string, string> { ["name"] = "Ada", ["email"] = "not-an-email" })));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SubmitPrechatAsync(grant.Context, new Dictionary<string, string> { ["name"] = "Ada", ["order"] = new string('9', 33) })));
        Assert.AreEqual(LiveChatRefusal.PrechatRequired, Refusal(await host.Visitors.SubmitPrechatAsync(grant.Context, new Dictionary<string, string> { ["email"] = "a@example.com" })));
        var context = Value(await host.Visitors.SubmitPrechatAsync(grant.Context, new Dictionary<string, string> { ["name"] = " Ada ", ["email"] = "Ada@Example.COM" }));
        Assert.AreEqual("ada@example.com", context.Session.Session.Prechat["email"]);
        Assert.AreEqual("Ada", context.Session.Session.DisplayName);
        Assert.IsNotNull(Value(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c1", "Hi"))));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("bad id", "Hi"))));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c2", "   "))));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c3", "\ud800 broken"))));
    }

    [TestMethod]
    public async Task AttachmentsRespectTypeSizeAndSessionOwnership()
    {
        var widget = new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin], attachments: new LiveChatAttachmentPolicy(16, ["image/png"]));
        await using var host = await CreateAsync(widget);
        var a = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        var b = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        Assert.IsNotNull(a.Widget.Attachments);

        Assert.AreEqual(LiveChatRefusal.UnsupportedMediaType, Refusal(await host.Visitors.UploadAttachmentAsync(a.Context, "x.exe", "application/x-msdownload", new MemoryStream([1]))));
        Assert.AreEqual(LiveChatRefusal.TooLarge, Refusal(await host.Visitors.UploadAttachmentAsync(a.Context, "big.png", "image/png", new MemoryStream(new byte[17]))));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.UploadAttachmentAsync(a.Context, "..", "image/png", new MemoryStream([1]))));
        var uploaded = Value(await host.Visitors.UploadAttachmentAsync(a.Context, "../../etc/photo.png", "Image/PNG; charset=binary", new MemoryStream(new byte[8])));
        Assert.AreEqual("photo.png", uploaded.FileName);
        Assert.AreEqual("image/png", uploaded.MediaType);
        Assert.AreEqual(8, uploaded.SizeBytes);

        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SendMessageAsync(b.Context, new VisitorMessageDraft("c1", "stolen", [uploaded.StorageRef]))),
            "A visitor can attach only its own uploads.");
        var accepted = Value(await host.Visitors.SendMessageAsync(a.Context, new VisitorMessageDraft("c1", null, [uploaded.StorageRef])));
        var message = Value(await host.Visitors.ReadMessagesAsync(a.Context, accepted.Conversation)).Messages.Single();
        Assert.AreEqual(uploaded, message.Attachments.Single());

        await using var plain = await CreateAsync(new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin]));
        var grant = Value(await plain.Visitors.StartSessionAsync(plain.Partition, WidgetKey, Origin));
        Assert.AreEqual(LiveChatRefusal.Unavailable, Refusal(await plain.Visitors.UploadAttachmentAsync(grant.Context, "a.png", "image/png", new MemoryStream([1]))));
        Assert.AreEqual(LiveChatRefusal.Unavailable, Refusal(await plain.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "x", ["mem:abc"]))));
    }

    [TestMethod]
    public async Task DeflectionSearchesThePublicPortal()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync();
        Assert.IsTrue(grant.Widget.DeflectionEnabled);
        var hits = Value(await host.Visitors.SearchArticlesAsync(grant.Context, "reset password"));
        Assert.AreEqual("Reset your password", hits.Single().Title);
        var query = host.Search.Queries.Single();
        Assert.AreEqual("help", query.Portal.Value);
        Assert.AreEqual(3, query.Limit);
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.SearchArticlesAsync(grant.Context, new string('x', 300))));

        await using var plain = await CreateAsync(new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin]));
        var other = Value(await plain.Visitors.StartSessionAsync(plain.Partition, WidgetKey, Origin));
        Assert.IsFalse(other.Widget.DeflectionEnabled);
        Assert.AreEqual(LiveChatRefusal.Unavailable, Refusal(await plain.Visitors.SearchArticlesAsync(other.Context, "reset")));
    }

    [TestMethod]
    public async Task AwayMessageFollowsTheInboxBusinessCalendar()
    {
        await using var host = await CreateAsync(new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin]));
        var open = Value(await host.Visitors.GetWidgetAsync(host.Partition, WidgetKey, Origin));
        Assert.IsTrue(open.Online);
        Assert.IsNull(open.AwayMessage);
        Assert.AreEqual("Inbox greeting", open.GreetingMessage);

        var calendars = host.Host.Services.GetRequiredService<IBusinessCalendarStore>();
        Assert.IsInstanceOfType<StoreApplied<StoredBusinessCalendar>>((await calendars.CreateAsync(host.Scope, new BusinessCalendar(new BusinessCalendarId("closed"), "Closed", "UTC"))).Value);
        var inboxes = host.Host.Services.GetRequiredService<IInboxStore>();
        Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await inboxes.UpdateAsync(host.Scope, new Inbox(new InboxId("chat-inbox"), "Chat",
            new ChannelBinding(new ChannelKind("livechat"), "cfg-chat"), new BusinessCalendarId("closed"), greetingMessage: "Inbox greeting", awayMessage: "Inbox away"), 1)).Value);
        var closed = Value(await host.Visitors.GetWidgetAsync(host.Partition, WidgetKey, Origin));
        Assert.IsFalse(closed.Online);
        Assert.AreEqual("Inbox away", closed.AwayMessage);

        await using var overridden = await CreateAsync();
        Assert.IsInstanceOfType<StoreApplied<StoredBusinessCalendar>>((await overridden.Host.Services.GetRequiredService<IBusinessCalendarStore>()
            .CreateAsync(overridden.Scope, new BusinessCalendar(new BusinessCalendarId("closed"), "Closed", "UTC"))).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await overridden.Host.Services.GetRequiredService<IInboxStore>().UpdateAsync(overridden.Scope,
            new Inbox(new InboxId("chat-inbox"), "Chat", new ChannelBinding(new ChannelKind("livechat"), "cfg-chat"), new BusinessCalendarId("closed"), awayMessage: "Inbox away"), 1)).Value);
        Assert.AreEqual("We are away", Value(await overridden.Visitors.GetWidgetAsync(overridden.Partition, WidgetKey, Origin)).AwayMessage);
    }

    [TestMethod]
    public async Task DisabledInboxesRefuseNewSessionsAndMessages()
    {
        await using var host = await CreateAsync(new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin]));
        var grant = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await host.Host.Services.GetRequiredService<IInboxStore>().UpdateAsync(host.Scope,
            new Inbox(new InboxId("chat-inbox"), "Chat", new ChannelBinding(new ChannelKind("livechat"), "cfg-chat"), enabled: false), 1)).Value);
        Assert.AreEqual(LiveChatRefusal.Unavailable, Refusal(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin)));
        Assert.AreEqual(LiveChatRefusal.Unavailable, Refusal(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "Hi"))));
    }

    [TestMethod]
    public async Task RevokedSessionsReceiveNoPushesAndNoAgentTyping()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync();
        var visitor = host.ConnectVisitor(grant.Context, "visitor-1");
        var accepted = Value(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "Hi")));
        await host.Host.DispatchAsync();
        visitor.Clear();

        var sessions = host.Host.Services.GetRequiredService<ILiveChatSessionStore>();
        var current = (await sessions.GetAsync(host.Partition, grant.Context.Session.Session.Id))!;
        Assert.IsInstanceOfType<StoreApplied<StoredVisitorSession>>((await sessions.UpdateAsync(host.Scope, current.Session.With(revoked: true), current.Revision)).Value);
        Assert.IsEmpty(await host.Host.Services.GetServices<IVisitorSessionDirectory>().Single().ListSessionsAsync(host.Scope, accepted.Conversation));

        Assert.IsInstanceOfType<RealtimeActionApplied>((await host.Host.Services.GetRequiredService<IRealtimeAgentActions>().SetTypingAsync(host.Host.Agent, accepted.Conversation, true)).Value);
        Assert.IsInstanceOfType<ConversationApplied>((await host.Host.Replies.ReplyAsync(host.Host.Agent, accepted.Conversation, new OutgoingMessageDraft("after revoke"))).Value);
        var run = await host.Host.DispatchAsync();
        Assert.AreEqual(0, run.Retried + run.DeadLettered, "The reply is still accepted (stored for the record).");
        Assert.IsEmpty(visitor.Received, "Revoked sessions get no pushes.");
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.ReadMessagesAsync(grant.Context, accepted.Conversation)));
    }

    [TestMethod]
    public async Task EndingASessionClosesAllItsConnections()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync();
        var tabA = host.ConnectVisitor(grant.Context, "tab-a");
        var tabB = host.ConnectVisitor(grant.Context, "tab-b");
        var other = await host.StartAsync();
        var otherTab = host.ConnectVisitor(other.Context, "other");

        Assert.IsTrue(Value(await host.Visitors.EndSessionAsync(grant.Context)));
        Assert.IsNotNull(tabA.Events<VisitorSessionEndedEvent>().SingleOrDefault());
        Assert.IsNotNull(tabB.Events<VisitorSessionEndedEvent>().SingleOrDefault());
        Assert.IsTrue(tabA.ClosedByServer && tabB.ClosedByServer);
        Assert.IsEmpty(host.Registry.Topics("tab-a"));
        Assert.IsEmpty(host.Registry.Topics("tab-b"));
        Assert.IsFalse(otherTab.ClosedByServer);
        Assert.HasCount(1, host.Registry.Topics("other"));
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c9", "still there?"))));
    }

    [TestMethod]
    public void UnsafeFileNamesAreRejected()
    {
        Assert.AreEqual("a.png", LiveChatVisitorService.SafeFileName("C:\\Users\\x\\a.png"));
        Assert.IsNull(LiveChatVisitorService.SafeFileName("dir/"));
        Assert.IsNull(LiveChatVisitorService.SafeFileName("bad\u0001name.png"));
        Assert.IsNull(LiveChatVisitorService.SafeFileName(null));
    }
}
