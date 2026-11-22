using TicketFlow.Conversations;
using TicketFlow.Core;
using static TicketFlow.Channels.Tests.ChannelTestSupport;

namespace TicketFlow.Channels.Tests;

[TestClass]
public sealed class ReplyWindowTests
{
    [TestMethod]
    public async Task RepliesInsideTheWindowAreRecordedAndOutsideAreRefused()
    {
        var whatsapp = WhatsAppChannel();
        await using var host = await HostAsync(whatsapp);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.1")));
        var open = Testing.ChannelHost.Is<ReplyWindowOpen>((await host.Replies.GetReplyWindowAsync(host.Agent, inbound.Conversation)).Value);
        Assert.AreEqual(host.Clock.GetUtcNow().AddHours(24), open.Until);
        Applied(await ReplyAsync(host, inbound.Conversation, "inside"));

        host.Clock.Advance(TimeSpan.FromHours(24));
        var refused = await ReplyAsync(host, inbound.Conversation, "outside");
        Assert.AreEqual(open.Until, Testing.ChannelHost.Is<ReplyWindowClosed>(refused.Value).ClosedAt);
        Assert.IsInstanceOfType<ReplyWindowClosed>((await host.Replies.GetReplyWindowAsync(host.Agent, inbound.Conversation)).Value);
        Assert.AreEqual(1, (await MessagesAsync(host, inbound.Conversation)).Count(message => message.Direction == MessageDirection.Outgoing));

        // A new customer message reopens the window.
        Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.2")));
        Applied(await ReplyAsync(host, inbound.Conversation, "again"));
    }

    [TestMethod]
    public async Task DispatcherRefusesRepliesWhoseWindowClosedBeforeSending()
    {
        var whatsapp = WhatsAppChannel();
        await using var host = await HostAsync(whatsapp);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.1")));
        host.Clock.Advance(TimeSpan.FromHours(23));
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation, "late")));
        host.Clock.Advance(TimeSpan.FromHours(2));
        await host.DispatchAsync();
        Assert.AreEqual(0, whatsapp.Requests.Count);
        var delivery = (await MessageAsync(host, inbound.Conversation, messageId)).Delivery;
        Assert.AreEqual(MessageDeliveryState.Failed, delivery.State);
        Assert.AreEqual(ReplyWindowClosed.FailureCode, delivery.FailureCode);
    }

    [TestMethod]
    public async Task WindowChecksRequireReplyPermissionAndChannelSupport()
    {
        var whatsapp = WhatsAppChannel();
        await using var host = await HostAsync(whatsapp);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.1")));
        Assert.AreEqual(ConversationRefusal.Forbidden, Testing.ChannelHost.Is<ConversationRefused>((await ReplyAsync(host, inbound.Conversation, actor: host.Viewer)).Value).Reason);
        Assert.IsInstanceOfType<ConversationRefused>((await host.Replies.GetReplyWindowAsync(host.Viewer, inbound.Conversation)).Value);
        Assert.IsInstanceOfType<ConversationNotFound>((await ReplyAsync(host, new ConversationId("missing"))).Value);
        var attachment = new AttachmentRef("storage/1", "a.pdf", "application/pdf", 10);
        Assert.AreEqual(ConversationRefusal.Invalid, Testing.ChannelHost.Is<ConversationRefused>((await ReplyAsync(host, inbound.Conversation, attachments: [attachment])).Value).Reason);
    }

    [TestMethod]
    public async Task ChannelsWithoutWindowsAreAlwaysOpen()
    {
        await using var host = await HostAsync(EmailChannel());
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<m@x>")));
        host.Clock.Advance(TimeSpan.FromDays(60));
        Assert.IsNull(Testing.ChannelHost.Is<ReplyWindowOpen>((await host.Replies.GetReplyWindowAsync(host.Agent, inbound.Conversation)).Value).Until);
        Applied(await ReplyAsync(host, inbound.Conversation));
    }
}
