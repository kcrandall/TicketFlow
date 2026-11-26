using Microsoft.AspNetCore.SignalR;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Realtime.SignalR;

/// <summary>
/// The live-chat visitor hub. Connections are anonymous until <see cref="Connect"/> authenticates a session token (or starts a session)
/// against the widget key and the request's Origin header; the token travels in the hub call, never in URLs. Afterwards the connection
/// is registered as the session's <see cref="VisitorSubscriber"/> and receives only that session's visitor-facing events. Every call
/// re-checks that the session is still live.
/// </summary>
public sealed class VisitorRealtimeHub(IRealtimeSubscriptions subscriptions, ILiveChatVisitorService visitors, IVisitorPartitionResolver partitions, IHubContext<VisitorRealtimeHub> hub,
    RealtimeTypingThrottle throttle) : Hub
{
    private const string ContextKey = "tf.visitor";

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Disconnect(Context.ConnectionId);
        throttle.Forget(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Authenticates <paramref name="token"/> (or, when it is absent, starts a new anonymous session) for the widget. Returns the
    /// (possibly rotated) token, the visitor id, the widget state and the session's conversations. An invalid token returns
    /// <c>unauthenticated</c>: the client discards it and connects without one.
    /// </summary>
    public async Task<HubResult> Connect(string widgetKey, string? token)
    {
        var http = Context.GetHttpContext();
        if (partitions.Resolve(http) is not { } partition)
            return HubResult.Failure("unavailable");
        var origin = http?.Request.Headers.Origin.ToString() is { Length: > 0 } header ? header : null;
        VisitorSessionContext session;
        string? issued = null;
        if (string.IsNullOrEmpty(token))
        {
            switch (await visitors.StartSessionAsync(partition, widgetKey, origin, Context.ConnectionAborted).ConfigureAwait(false))
            {
                case TicketValue<VisitorSessionGrant> grant:
                    session = grant.Value.Context;
                    issued = grant.Value.Token;
                    break;
                case LiveChatRefused refused:
                    return Failure(refused);
                default:
                    return HubResult.Failure("unavailable");
            }
        }
        else
        {
            switch (await visitors.AuthenticateAsync(partition, token, origin, Context.ConnectionAborted).ConfigureAwait(false))
            {
                case TicketValue<VisitorSessionContext> authenticated:
                    session = authenticated.Value;
                    break;
                case LiveChatRefused refused:
                    return Failure(refused);
                default:
                    return HubResult.Failure("unavailable");
            }
            if (session.RotationRecommended && await visitors.RotateTokenAsync(session, Context.ConnectionAborted).ConfigureAwait(false) is TicketValue<VisitorTokenRotated> rotated)
            {
                session = rotated.Value.Context;
                issued = rotated.Value.Token;
            }
        }

        subscriptions.Disconnect(Context.ConnectionId);
        var subscriber = new VisitorSubscriber(session.Scope, session.Session.Session.Id);
        if (subscriptions.Connect(new SignalRRealtimeConnection(Context.ConnectionId, subscriber, hub.Clients.Client(Context.ConnectionId), Context.Abort)).Value is not RealtimeConnected
            || (await subscriptions.SubscribeAsync(Context.ConnectionId, new VisitorSessionTopic(session.Scope, session.Session.Session.Id), Context.ConnectionAborted).ConfigureAwait(false))
                .Value is not RealtimeSubscribed)
            return HubResult.Failure("limit-reached");
        Context.Items[ContextKey] = session;

        var widgetTask = visitors.GetSessionWidgetAsync(session, Context.ConnectionAborted);
        var conversationsTask = visitors.ListConversationsAsync(session, Context.ConnectionAborted);
        await Task.WhenAll(widgetTask, conversationsTask).ConfigureAwait(false);
        var widget = await widgetTask.ConfigureAwait(false);
        var conversations = await conversationsTask.ConfigureAwait(false);
        return HubResult.Success(new Dictionary<string, object?>(6, StringComparer.Ordinal)
        {
            ["token"] = issued,
            ["visitorId"] = session.Session.Session.VisitorId,
            ["verified"] = session.Session.Session.Identity is not null,
            ["prechatAnswered"] = session.Session.Session.Prechat.Count > 0,
            ["widget"] = widget.Value is TicketValue<LiveChatWidgetState> state ? Widget(state.Value) : null,
            ["conversations"] = conversations.Value is TicketValue<IReadOnlyList<VisitorConversationView>> list ? Conversations(list.Value) : [],
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0016:Prefer using collection abstraction instead of implementation", Justification = "SignalR hub parameter binds concrete Dictionary<string, string> in public contract.")]
    public async Task<HubResult> SubmitPrechat(Dictionary<string, string> answers)
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        return Store(await visitors.SubmitPrechatAsync(session, answers ?? new Dictionary<string, string>(StringComparer.Ordinal), Context.ConnectionAborted).ConfigureAwait(false));
    }

    /// <summary><paramref name="method"/> is <c>hmac</c> (<paramref name="value"/> = user id, <paramref name="signature"/> = hex HMAC) or <c>token</c> (<paramref name="value"/> = signed token).</summary>
    public async Task<HubResult> Identify(string method, string value, string? signature, string? displayName)
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        VisitorIdentification identification = method switch
        {
            "hmac" => new HmacVisitorIdentification(value ?? string.Empty, signature ?? string.Empty, displayName),
            "token" => new SignedTokenVisitorIdentification(value ?? string.Empty),
            _ => default,
        };
        if (identification.Value is null)
            return HubResult.Failure("invalid");
        return Store(await visitors.IdentifyAsync(session, identification, Context.ConnectionAborted).ConfigureAwait(false));
    }

    public async Task<HubResult> SendMessage(string clientMessageId, string? text, string[]? attachments)
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        return await visitors.SendMessageAsync(session, new VisitorMessageDraft(clientMessageId ?? string.Empty, text, attachments), Context.ConnectionAborted).ConfigureAwait(false) switch
        {
            TicketValue<VisitorMessageAccepted> accepted => HubResult.Success(new Dictionary<string, object?>(5, StringComparer.Ordinal)
            {
                ["conversation"] = accepted.Value.Conversation.Value,
                ["message"] = accepted.Value.Message.Value,
                ["sequence"] = accepted.Value.Sequence,
                ["started"] = accepted.Value.StartedConversation,
                ["replayed"] = accepted.Value.Replayed,
            }),
            LiveChatRefused refused => Failure(refused),
        };
    }

    public async Task<HubResult> ListConversations()
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        return await visitors.ListConversationsAsync(session, Context.ConnectionAborted).ConfigureAwait(false) switch
        {
            TicketValue<IReadOnlyList<VisitorConversationView>> list => HubResult.Success(Conversations(list.Value)),
            LiveChatRefused refused => Failure(refused),
        };
    }

    /// <summary>Visitor-visible messages after <paramref name="afterSequence"/> (reconnect with cursor).</summary>
    public async Task<HubResult> FetchMessages(string conversation, long afterSequence, int limit)
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        if (Id(conversation) is not { } id)
            return HubResult.Failure("invalid");
        return await visitors.ReadMessagesAsync(session, id, afterSequence, limit <= 0 ? 50 : limit, Context.ConnectionAborted).ConfigureAwait(false) switch
        {
            TicketValue<VisitorMessagePage> page => HubResult.Success(BuildMessagePage(page.Value)),
            LiveChatRefused refused => Failure(refused),
        };
    }

    public async Task<HubResult> Typing(string conversation, bool typing)
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        if (Id(conversation) is not { } id)
            return HubResult.Failure("invalid");
        if (!throttle.Allow(Context.ConnectionId, id.Value, typing))
            return HubResult.Success();
        return Simple(await visitors.SetTypingAsync(session, id, typing, Context.ConnectionAborted).ConfigureAwait(false));
    }

    public async Task<HubResult> MarkRead(string conversation, long upToSequence)
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        if (Id(conversation) is not { } id)
            return HubResult.Failure("invalid");
        return await visitors.MarkReadAsync(session, id, upToSequence, Context.ConnectionAborted).ConfigureAwait(false) switch
        {
            TicketValue<long> read => HubResult.Success(read.Value),
            LiveChatRefused refused => Failure(refused),
        };
    }

    public async Task<HubResult> SearchArticles(string text)
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        return await visitors.SearchArticlesAsync(session, text ?? string.Empty, Context.ConnectionAborted).ConfigureAwait(false) switch
        {
            TicketValue<IReadOnlyList<TicketFlow.HelpCenter.ArticleSnippet>> hits => HubResult.Success(hits.Value.Select(hit => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["article"] = hit.Article.Value,
                ["title"] = hit.Title,
                ["slug"] = hit.Slug.Value,
                ["snippet"] = hit.Snippet,
                ["locale"] = hit.Locale.Value,
            }).ToList()),
            LiveChatRefused refused => Failure(refused),
        };
    }

    public async Task<HubResult> EndSession()
    {
        if (Session() is not { } session)
            return HubResult.Failure("unauthenticated");
        // Leave fan-out before ending the session: the resulting session-ended event closes the visitor's OTHER connections, and must
        // not abort this one before the invocation result is returned. A failed end leaves the caller to Connect again.
        subscriptions.Disconnect(Context.ConnectionId);
        var result = Simple(await visitors.EndSessionAsync(session, Context.ConnectionAborted).ConfigureAwait(false));
        Context.Items.Remove(ContextKey);
        return result;
    }

    private VisitorSessionContext? Session() => Context.Items.TryGetValue(ContextKey, out var value) && value is VisitorSessionContext session ? session : null;

    private HubResult Store(TicketResult<VisitorSessionContext, LiveChatRefused> result)
    {
        switch (result)
        {
            case TicketValue<VisitorSessionContext> updated:
                Context.Items[ContextKey] = updated.Value;
                return HubResult.Success(new Dictionary<string, object?>(StringComparer.Ordinal) { ["verified"] = updated.Value.Session.Session.Identity is not null });
            case LiveChatRefused refused:
                return Failure(refused);
        }
        return HubResult.Failure("unavailable");
    }

    private static HubResult Simple(TicketResult<bool, LiveChatRefused> result) => result switch
    {
        TicketValue<bool> => HubResult.Success(),
        LiveChatRefused refused => Failure(refused),
    };

    private static HubResult Failure(LiveChatRefused refused) => new(false, RealtimeWireFormat.Token(refused.Reason), refused.Errors.Count > 0 ? refused.Errors : null);

    private static ConversationId? Id(string? value)
    {
        try
        {
            return value is null ? null : new ConversationId(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?>[] Conversations(IReadOnlyList<VisitorConversationView> list)
    {
        if (list.Count == 0)
            return [];
        var result = new Dictionary<string, object?>[list.Count];
        for (var i = 0; i < list.Count; i++)
            result[i] = Conversation(list[i]);
        return result;
    }

    private static Dictionary<string, object?> BuildMessagePage(VisitorMessagePage page)
    {
        var messages = new Dictionary<string, object?>[page.Messages.Count];
        for (var i = 0; i < page.Messages.Count; i++)
        {
            var message = page.Messages[i];
            messages[i] = new Dictionary<string, object?>(8, StringComparer.Ordinal)
            {
                ["message"] = message.Message.Value,
                ["sequence"] = message.Sequence,
                ["direction"] = RealtimeWireFormat.Token(message.Direction),
                ["body"] = message.Body,
                ["contentType"] = RealtimeWireFormat.Token(message.ContentType),
                ["attachments"] = RealtimeWireFormat.BuildAttachments(message.Attachments),
                ["at"] = message.At,
                ["delivery"] = RealtimeWireFormat.Token(message.Delivery),
            };
        }
        return new Dictionary<string, object?>(4, StringComparer.Ordinal)
        {
            ["conversation"] = page.Conversation.Value,
            ["readSequence"] = page.ReadSequence,
            ["next"] = page.NextAfterSequence,
            ["messages"] = messages,
        };
    }

    private static Dictionary<string, object?> Conversation(VisitorConversationView view) => new(5, StringComparer.Ordinal)
    {
        ["conversation"] = view.Conversation.Value,
        ["closed"] = view.Closed,
        ["lastSequence"] = view.LastSequence,
        ["readSequence"] = view.ReadSequence,
        ["lastActivityAt"] = view.LastActivityAt,
    };

    private static Dictionary<string, object?> Widget(LiveChatWidgetState state)
    {
        var prechat = new Dictionary<string, object?>[state.PrechatFields.Count];
        for (var i = 0; i < state.PrechatFields.Count; i++)
        {
            var field = state.PrechatFields[i];
            prechat[i] = new Dictionary<string, object?>(5, StringComparer.Ordinal)
            {
                ["key"] = field.Key,
                ["label"] = field.Label,
                ["kind"] = RealtimeWireFormat.Token(field.Kind),
                ["required"] = field.Required,
                ["maxLength"] = field.MaximumLength,
            };
        }
        return new Dictionary<string, object?>(8, StringComparer.Ordinal)
        {
            ["themeRef"] = state.ThemeRef,
            ["greeting"] = state.GreetingMessage,
            ["online"] = state.Online,
            ["awayMessage"] = state.AwayMessage,
            ["deflection"] = state.DeflectionEnabled,
            ["requireVerified"] = state.RequireVerifiedIdentity,
            ["attachments"] = state.Attachments is { } policy
                ? new Dictionary<string, object?>(2, StringComparer.Ordinal) { ["maxBytes"] = policy.MaximumBytes, ["types"] = policy.AllowedMediaTypes }
                : null,
            ["prechat"] = prechat,
        };
    }
}
