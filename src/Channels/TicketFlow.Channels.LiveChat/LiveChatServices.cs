using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Channels.LiveChat;

/// <summary>
/// Widget administration: <c>livechat:manage</c> on the bound inbox to create, update (both inboxes when moving) and delete;
/// <c>livechat:read</c> to read. The inbox must exist and be bound to the live-chat kind with the widget's configuration id.
/// Widgets are reachable by visitors through the inbox's channel endpoint, whose address is the public widget key.
/// </summary>
public interface ILiveChatWidgetService
{
    Task<LiveChatResult<StoredLiveChatWidget>> GetAsync(PrincipalRef actor, string configurationId, CancellationToken cancellationToken = default);

    Task<LiveChatResult<StoredLiveChatWidget>> CreateAsync(PrincipalRef actor, LiveChatWidget widget, CancellationToken cancellationToken = default);

    Task<LiveChatResult<StoredLiveChatWidget>> UpdateAsync(PrincipalRef actor, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default);

    Task<LiveChatResult<StoredLiveChatWidget>> DeleteAsync(PrincipalRef actor, string configurationId, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// The visitor side of live chat. Visitors are not Access principals: they are authenticated by their session token and the
/// request Origin, and can reach only conversations linked to their own session. Support data is written through the E6 inbound
/// pipeline as the channel worker principal. Partitions come from host routing configuration, never from the
/// visitor. Never logs bodies, tokens, identities or answers.
/// </summary>
public interface ILiveChatVisitorService
{
    /// <summary>The widget's public state (theme, pre-chat form, business hours, features) for the widget key (its endpoint address).</summary>
    Task<LiveChatResult<LiveChatWidgetState>> GetWidgetAsync(AccessPartition partition, string widgetKey, string? origin, CancellationToken cancellationToken = default);

    /// <summary>Starts an anonymous session and returns its token (shown once).</summary>
    Task<LiveChatResult<VisitorSessionGrant>> StartSessionAsync(AccessPartition partition, string widgetKey, string? origin, CancellationToken cancellationToken = default);

    /// <summary>Authenticates a session token (constant-time hash comparison) and the Origin against the widget.</summary>
    Task<LiveChatResult<VisitorSessionContext>> AuthenticateAsync(AccessPartition partition, string token, string? origin, CancellationToken cancellationToken = default);

    Task<LiveChatResult<LiveChatWidgetState>> GetSessionWidgetAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);

    /// <summary>Issues a new token, extends the session and keeps the previous token valid for the grace window.</summary>
    Task<LiveChatResult<VisitorTokenRotated>> RotateTokenAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);

    /// <summary>Verifies the visitor's identity (HMAC or signed token); later messages carry a verified <c>ExternalUser</c> identity.</summary>
    Task<LiveChatResult<VisitorSessionContext>> IdentifyAsync(VisitorSessionContext context, VisitorIdentification identification, CancellationToken cancellationToken = default);

    Task<LiveChatResult<VisitorSessionContext>> SubmitPrechatAsync(VisitorSessionContext context, IReadOnlyDictionary<string, string> answers, CancellationToken cancellationToken = default);

    Task<LiveChatResult<VisitorMessageAccepted>> SendMessageAsync(VisitorSessionContext context, VisitorMessageDraft message, CancellationToken cancellationToken = default);

    Task<LiveChatResult<IReadOnlyList<VisitorConversationView>>> ListConversationsAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);

    /// <summary>Visitor-visible messages after <paramref name="afterSequence"/> (fetch-on-reconnect).</summary>
    Task<LiveChatResult<VisitorMessagePage>> ReadMessagesAsync(VisitorSessionContext context, ConversationId conversation, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default);

    Task<LiveChatResult<bool>> SetTypingAsync(VisitorSessionContext context, ConversationId conversation, bool typing, CancellationToken cancellationToken = default);

    /// <summary>Advances the stored read cursor and publishes a read receipt to agents.</summary>
    Task<LiveChatResult<long>> MarkReadAsync(VisitorSessionContext context, ConversationId conversation, long upToSequence, CancellationToken cancellationToken = default);

    Task<LiveChatResult<AttachmentRef>> UploadAttachmentAsync(VisitorSessionContext context, string fileName, string mediaType, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Help-center deflection over the widget's public portal.</summary>
    Task<LiveChatResult<IReadOnlyList<ArticleSnippet>>> SearchArticlesAsync(VisitorSessionContext context, string text, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session (logout); its tokens stop working immediately.</summary>
    Task<LiveChatResult<bool>> EndSessionAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);
}

/// <summary>Agent-facing live-chat reads.</summary>
public interface ILiveChatAgentService
{
    /// <summary>
    /// The most recent visitor session of a conversation: <c>conversation:read</c> on it, then masking from <c>contact:read</c> on its
    /// contact (identity value as <c>contact.external-id</c>, email answers as <c>contact.email</c>, sensitive answers as
    /// <c>contact.attribute.&lt;key&gt;</c>, masked unless a field policy unmasks them).
    /// </summary>
    Task<LiveChatResult<LiveChatVisitorView>> GetVisitorAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default);
}
