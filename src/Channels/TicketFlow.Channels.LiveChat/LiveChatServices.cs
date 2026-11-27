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
    Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> GetAsync(PrincipalRef actor, string configurationId, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> CreateAsync(PrincipalRef actor, LiveChatWidget widget, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> UpdateAsync(PrincipalRef actor, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> DeleteAsync(PrincipalRef actor, string configurationId, long expectedRevision, CancellationToken cancellationToken = default);
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
    Task<TicketResult<LiveChatWidgetState, LiveChatRefused>> GetWidgetAsync(AccessPartition partition, string widgetKey, string? origin, CancellationToken cancellationToken = default);

    /// <summary>Starts an anonymous session and returns its token (shown once).</summary>
    Task<TicketResult<VisitorSessionGrant, LiveChatRefused>> StartSessionAsync(AccessPartition partition, string widgetKey, string? origin, CancellationToken cancellationToken = default);

    /// <summary>Authenticates a session token (constant-time hash comparison) and the Origin against the widget.</summary>
    Task<TicketResult<VisitorSessionContext, LiveChatRefused>> AuthenticateAsync(AccessPartition partition, string token, string? origin, CancellationToken cancellationToken = default);

    /// <summary>Authenticates a session token (constant-time hash comparison), the Origin and that the session belongs to <paramref name="widgetKey"/>.</summary>
    Task<TicketResult<VisitorSessionContext, LiveChatRefused>> AuthenticateForWidgetAsync(AccessPartition partition, string widgetKey, string token, string? origin, CancellationToken cancellationToken = default);

    Task<TicketResult<LiveChatWidgetState, LiveChatRefused>> GetSessionWidgetAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);

    /// <summary>Issues a new token, extends the session and keeps the previous token valid for the grace window.</summary>
    Task<TicketResult<VisitorTokenRotated, LiveChatRefused>> RotateTokenAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);

    /// <summary>Verifies the visitor's identity (HMAC or signed token); later messages carry a verified <c>ExternalUser</c> identity.</summary>
    Task<TicketResult<VisitorSessionContext, LiveChatRefused>> IdentifyAsync(VisitorSessionContext context, VisitorIdentification identification, CancellationToken cancellationToken = default);

    Task<TicketResult<VisitorSessionContext, LiveChatRefused>> SubmitPrechatAsync(VisitorSessionContext context, IReadOnlyDictionary<string, string> answers, CancellationToken cancellationToken = default);

    Task<TicketResult<VisitorMessageAccepted, LiveChatRefused>> SendMessageAsync(VisitorSessionContext context, VisitorMessageDraft message, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<VisitorConversationView>, LiveChatRefused>> ListConversationsAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);

    /// <summary>Visitor-visible messages after <paramref name="afterSequence"/> (fetch-on-reconnect).</summary>
    Task<TicketResult<VisitorMessagePage, LiveChatRefused>> ReadMessagesAsync(VisitorSessionContext context, ConversationId conversation, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default);

    Task<TicketResult<bool, LiveChatRefused>> SetTypingAsync(VisitorSessionContext context, ConversationId conversation, bool typing, CancellationToken cancellationToken = default);

    /// <summary>Advances the stored read cursor and publishes a read receipt to agents.</summary>
    Task<TicketResult<long, LiveChatRefused>> MarkReadAsync(VisitorSessionContext context, ConversationId conversation, long upToSequence, CancellationToken cancellationToken = default);

    Task<TicketResult<AttachmentRef, LiveChatRefused>> UploadAttachmentAsync(VisitorSessionContext context, string fileName, string mediaType, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Help-center deflection over the widget's public portal.</summary>
    Task<TicketResult<IReadOnlyList<ArticleSnippet>, LiveChatRefused>> SearchArticlesAsync(VisitorSessionContext context, string text, CancellationToken cancellationToken = default);

    /// <summary>Revokes the session (logout); its tokens stop working immediately.</summary>
    Task<TicketResult<bool, LiveChatRefused>> EndSessionAsync(VisitorSessionContext context, CancellationToken cancellationToken = default);
}

/// <summary>Agent-facing live-chat reads.</summary>
public interface ILiveChatAgentService
{
    /// <summary>
    /// The most recent visitor session of a conversation: <c>conversation:read</c> on it, then masking from <c>contact:read</c> on its
    /// contact (identity value as <c>contact.external-id</c>, email answers as <c>contact.email</c>, sensitive answers as
    /// <c>contact.attribute.&lt;key&gt;</c>, masked unless a field policy unmasks them).
    /// </summary>
    Task<TicketResult<LiveChatVisitorView, LiveChatRefused>> GetVisitorAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default);
}
