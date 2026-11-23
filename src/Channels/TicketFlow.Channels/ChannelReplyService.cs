using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>
/// Default <see cref="IChannelReplyService"/>: existence, then <c>conversation:reply</c> authorization, then channel rules (reply
/// window measured from the channel's last incoming time, falling back to the conversation's; attachment support), then
/// <see cref="IConversationService.ReplyAsync"/>. Conversations whose inbox kind has no registered channel are not restricted
/// (another component, for example live chat, delivers them).
/// </summary>
public sealed class ChannelReplyService : IChannelReplyService
{
    private readonly IConversationService conversations;
    private readonly IConversationStore conversationStore;
    private readonly IChannelConversationStateStore states;
    private readonly IInboxStore inboxes;
    private readonly IChannelRegistry registry;
    private readonly IAccessEvaluator evaluator;
    private readonly TimeProvider time;

    public ChannelReplyService(IConversationService conversations, IConversationStore conversationStore, IChannelConversationStateStore states, IInboxStore inboxes,
        IChannelRegistry registry, IAccessEvaluator evaluator, TimeProvider time)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        this.states = states ?? throw new ArgumentNullException(nameof(states));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<ChannelReplyResult> ReplyAsync(PrincipalRef actor, ConversationId conversation, OutgoingMessageDraft message, long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (stored, refusal) = await AuthorizeAsync(actor, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return refusal is null ? ConversationNotFound.Instance : refusal;
        var channel = await ChannelAsync(stored, cancellationToken).ConfigureAwait(false);
        if (channel is not null)
        {
            var attachments = message.Attachments?.Count ?? 0;
            if (attachments > channel.Capabilities.MaximumAttachments)
                return ConversationRefused.Invalid(channel.Capabilities.Has(ChannelFeatures.Attachments)
                    ? $"The channel sends at most {channel.Capabilities.MaximumAttachments} attachments."
                    : "The channel cannot send attachments.");
            if (message.Body.Length > channel.Capabilities.MaximumBodyLength)
                return ConversationRefused.Invalid($"The channel accepts bodies of at most {channel.Capabilities.MaximumBodyLength} characters.");
            if (channel.Capabilities.ReplyWindow is not null
                && await WindowAsync(stored, channel, cancellationToken).ConfigureAwait(false) is { Value: ReplyWindowClosed closed })
                return closed;
        }
        return await conversations.ReplyAsync(actor, conversation, message, expectedRevision, cancellationToken).ConfigureAwait(false) switch
        {
            ConversationApplied applied => applied,
            ConversationNotFound notFound => notFound,
            ConversationConflict conflict => conflict,
            ConversationRefused refused => refused,
        };
    }

    public async Task<ReplyWindowResult> GetReplyWindowAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        var (stored, refusal) = await AuthorizeAsync(actor, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return refusal is null ? ConversationNotFound.Instance : refusal;
        var channel = await ChannelAsync(stored, cancellationToken).ConfigureAwait(false);
        if (channel is null)
            return new ReplyWindowOpen(null);
        return await WindowAsync(stored, channel, cancellationToken).ConfigureAwait(false) switch
        {
            ReplyWindowOpen open => open,
            ReplyWindowClosed closed => closed,
        };
    }

    private async Task<(StoredConversation? Stored, ConversationRefused? Refusal)> AuthorizeAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken)
    {
        if (actor is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        var stored = await conversationStore.GetAsync(actor.Scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return (null, null);
        if (!await evaluator.PermittedAsync(actor, ConversationPermissions.Reply, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
            return (null, ConversationRefused.Forbidden);
        return (stored, null);
    }

    private async Task<ITicketChannel?> ChannelAsync(StoredConversation stored, CancellationToken cancellationToken)
    {
        var inbox = await inboxes.GetAsync(stored.Scope, stored.Conversation.Inbox, cancellationToken).ConfigureAwait(false);
        return inbox is null ? null : registry.Find(inbox.Inbox.Channel.Kind);
    }

    private async Task<ReplyWindowState> WindowAsync(StoredConversation stored, ITicketChannel channel, CancellationToken cancellationToken)
    {
        if (channel.Capabilities.ReplyWindow is null)
            return new ReplyWindowOpen(null);
        var state = await states.GetAsync(stored.Scope, stored.Id, cancellationToken).ConfigureAwait(false);
        return ChannelReplyWindow.Evaluate(channel.Capabilities, state?.State.LastIncomingAt ?? stored.Conversation.LastIncomingAt, time.GetUtcNow());
    }
}
