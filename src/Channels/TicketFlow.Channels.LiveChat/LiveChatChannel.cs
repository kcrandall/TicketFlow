using Microsoft.Extensions.Options;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Realtime;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.LiveChat;

/// <summary>
/// The live-chat <see cref="ITicketChannel"/>. Dispatched agent replies are pushed as <see cref="VisitorMessageEvent"/>s to every
/// session linked to the conversation and stay fetchable through <see cref="ILiveChatVisitorService.ReadMessagesAsync"/>, so the
/// submission is accepted even when no visitor is connected (store-and-forward). Notifications without a conversation message are
/// rejected (not supported). Never logs bodies.
/// </summary>
public sealed class LiveChatChannel : ITicketChannel
{
    public const int MaximumAttachments = 10;

    private readonly ILiveChatSessionStore sessions;
    private readonly IConversationStore conversations;
    private readonly IRealtimePublisher publisher;
    private readonly TimeProvider time;

    public LiveChatChannel(ILiveChatSessionStore sessions, IConversationStore conversations, IRealtimePublisher publisher, IOptions<LiveChatOptions> options, TimeProvider time)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentNullException.ThrowIfNull(options);
        options.Value.Validate();
        Kind = new ChannelKind(options.Value.Kind);
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public ChannelKind Kind { get; }

    public ChannelCapabilities Capabilities { get; } = new(ChannelFeatures.Attachments | ChannelFeatures.Typing | ChannelFeatures.ReadReceipts, maximumAttachments: MaximumAttachments);

    public IReadOnlyList<ContactIdentityKind> RecipientKinds { get; } = [ContactIdentityKind.WidgetVisitor, ContactIdentityKind.ExternalUser];

    public async Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Message is not { } messageId)
            return new ChannelSendRejected("unsupported", retryable: false);
        var row = await conversations.GetMessageAsync(message.Scope, message.Conversation, messageId, cancellationToken).ConfigureAwait(false);
        if (row is null)
            return new ChannelSendRejected("message-not-found", retryable: false);
        var linked = await sessions.FindSessionsAsync(message.Scope, message.Conversation, IVisitorSessionDirectory.MaximumSessions, cancellationToken).ConfigureAwait(false);
        foreach (var session in linked)
        {
            var push = new VisitorMessageEvent(message.Scope, session, message.Conversation, messageId, row.Sequence, MessageDirection.Outgoing, message.Body,
                message.ContentType, message.Attachments, time.GetUtcNow());
            try
            {
                await publisher.PublishAsync(new RealtimePublication(new VisitorSessionTopic(message.Scope, session), push), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Best effort: the visitor fetches the stored reply on reconnect.
            }
        }
        return new ChannelSendAccepted($"livechat:{messageId.Value}");
    }
}

/// <summary><see cref="IVisitorSessionDirectory"/> over the live-chat session links.</summary>
public sealed class LiveChatSessionDirectory(ILiveChatSessionStore sessions) : IVisitorSessionDirectory
{
    private readonly ILiveChatSessionStore sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public Task<IReadOnlyList<VisitorSessionId>> ListSessionsAsync(TicketFlow.Core.TicketScope scope, TicketFlow.Core.ConversationId conversation, CancellationToken cancellationToken = default) =>
        sessions.FindSessionsAsync(scope, conversation, IVisitorSessionDirectory.MaximumSessions, cancellationToken);
}
