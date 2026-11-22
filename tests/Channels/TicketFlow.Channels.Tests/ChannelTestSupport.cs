using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Tests;

internal static class ChannelTestSupport
{
    public static FakeChannel EmailChannel() => new(new ChannelKind("email"),
        new ChannelCapabilities(ChannelFeatures.Threading | ChannelFeatures.RichContent | ChannelFeatures.DeliveryReceipts), ContactIdentityKind.Email);

    public static FakeChannel WhatsAppChannel() => new(new ChannelKind("whatsapp"),
        new ChannelCapabilities(ChannelFeatures.ReplyWindow | ChannelFeatures.Templates | ChannelFeatures.DeliveryReceipts, TimeSpan.FromHours(24)), ContactIdentityKind.WhatsApp);

    public static Task<ChannelHost> HostAsync(params FakeChannel[] channels) => ChannelHost.CreateAsync(services =>
    {
        foreach (var channel in channels)
            services.AddSingleton<ITicketChannel>(channel);
    });

    public static InboundRecorded Recorded(InboundResult result) => result switch
    {
        InboundRecorded recorded => recorded,
        InboundRejected rejected => throw new AssertFailedException($"Expected recorded but was rejected: {rejected.Reason} {string.Join("; ", rejected.Errors)}"),
    };

    public static InboundRejection Rejection(InboundResult result) => result switch
    {
        InboundRecorded => throw new AssertFailedException("Expected a rejection but the message was recorded."),
        InboundRejected rejected => rejected.Reason,
    };

    public static ConversationApplied Applied(ChannelReplyResult result) => result switch
    {
        ConversationApplied applied => applied,
        ConversationNotFound => throw new AssertFailedException("Expected applied but was not found."),
        ConversationConflict conflict => throw new AssertFailedException($"Expected applied but was conflict {conflict.CurrentRevision}."),
        ConversationRefused refused => throw new AssertFailedException($"Expected applied but was refused: {refused.Reason}."),
        ReplyWindowClosed => throw new AssertFailedException("Expected applied but the reply window was closed."),
    };

    public static MessageId OutgoingId(ConversationApplied applied) =>
        applied.Events.Select(item => item.Event.Value).OfType<MessageEvent>().Single(message => message.Direction == MessageDirection.Outgoing).Id;

    public static async Task<MessageEvent> MessageAsync(ChannelHost host, ConversationId conversation, MessageId message)
    {
        var page = await host.Conversations.ReadTimelineAsync(host.Owner, conversation, 0, 200) switch
        {
            TimelinePage value => value,
            ConversationNotFound => throw new AssertFailedException("Conversation not found."),
            ConversationRefused refused => throw new AssertFailedException($"Timeline refused: {refused.Reason}."),
        };
        return page.Events.Select(item => item.Event.Event.Value).OfType<MessageEvent>().Single(item => item.Id == message);
    }

    public static async Task<IReadOnlyList<MessageEvent>> MessagesAsync(ChannelHost host, ConversationId conversation)
    {
        var page = await host.Conversations.ReadTimelineAsync(host.Owner, conversation, 0, 200) switch
        {
            TimelinePage value => value,
            ConversationNotFound => throw new AssertFailedException("Conversation not found."),
            ConversationRefused refused => throw new AssertFailedException($"Timeline refused: {refused.Reason}."),
        };
        return [.. page.Events.Select(item => item.Event.Event.Value).OfType<MessageEvent>()];
    }

    public static async Task<ConversationView> ViewAsync(ChannelHost host, ConversationId conversation) => await host.Conversations.GetAsync(host.Owner, conversation) switch
    {
        ConversationView view => view,
        ConversationNotFound => throw new AssertFailedException("Conversation not found."),
        ConversationRefused refused => throw new AssertFailedException($"Get refused: {refused.Reason}."),
    };

    public static Task<ChannelReplyResult> ReplyAsync(ChannelHost host, ConversationId conversation, string body = "On it", PrincipalRef? actor = null,
        IReadOnlyList<AttachmentRef>? attachments = null) =>
        host.Replies.ReplyAsync(actor ?? host.Agent, conversation, new OutgoingMessageDraft(body, Attachments: attachments));
}
