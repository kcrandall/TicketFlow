using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Realtime;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.LiveChat;

/// <summary>
/// Default <see cref="ILiveChatVisitorService"/>. Widget lookups go through the inbox's channel endpoint (address = widget key) within
/// the host-supplied partition; every visitor operation re-loads the session and refuses revoked or expired sessions; conversations are
/// reachable only through the session's links, which are created only from the session's own recorded messages. Visitor messages run
/// through <see cref="IInboundChannelPipeline"/> as <see cref="ChannelsOptions.WorkerPrincipal"/> with a verified <c>ExternalUser</c>
/// identity (after identification) or a claimed <c>WidgetVisitor</c> identity, threaded by the visitor id.
/// </summary>
public sealed class LiveChatVisitorService : ILiveChatVisitorService
{
    private readonly IChannelEndpointStore endpoints;
    private readonly ILiveChatWidgetStore widgets;
    private readonly ILiveChatSessionStore sessions;
    private readonly IInboxStore inboxes;
    private readonly IBusinessCalendarStore calendars;
    private readonly IInboundChannelPipeline pipeline;
    private readonly IConversationStore conversations;
    private readonly IRealtimePublisher publisher;
    private readonly IAttachmentStore? attachments;
    private readonly IHelpCenterSearch? helpCenter;
    private readonly ILiveChatIdentitySecretResolver? secrets;
    private readonly IVisitorTokenValidator? tokenValidator;
    private readonly IContactLinkingService? linking;
    private readonly LiveChatOptions options;
    private readonly ChannelsOptions channelOptions;
    private readonly ChannelKind kind;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public LiveChatVisitorService(
        IChannelEndpointStore endpoints,
        ILiveChatWidgetStore widgets,
        ILiveChatSessionStore sessions,
        IInboxStore inboxes,
        IBusinessCalendarStore calendars,
        IInboundChannelPipeline pipeline,
        IConversationStore conversations,
        IRealtimePublisher publisher,
        IOptions<LiveChatOptions> options,
        IOptions<ChannelsOptions> channelOptions,
        TimeProvider time,
        IAttachmentStore? attachments = null,
        IHelpCenterSearch? helpCenter = null,
        ILiveChatIdentitySecretResolver? secrets = null,
        IVisitorTokenValidator? tokenValidator = null,
        ILogger<LiveChatVisitorService>? logger = null,
        IContactLinkingService? linking = null)
    {
        this.endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        this.widgets = widgets ?? throw new ArgumentNullException(nameof(widgets));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        ArgumentNullException.ThrowIfNull(channelOptions);
        this.channelOptions = channelOptions.Value;
        kind = new ChannelKind(this.options.Kind);
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.attachments = attachments;
        this.helpCenter = helpCenter;
        this.secrets = secrets;
        this.tokenValidator = tokenValidator;
        this.logger = logger ?? NullLogger<LiveChatVisitorService>.Instance;
        this.linking = linking;
    }

    // ---- Widgets and sessions ----

    public async Task<LiveChatResult<LiveChatWidgetState>> GetWidgetAsync(AccessPartition partition, string widgetKey, string? origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (await FindWidgetAsync(partition, widgetKey, cancellationToken).ConfigureAwait(false) is not { } found)
            return Refused<LiveChatWidgetState>(LiveChatRefusal.NotFound);
        if (!OriginAllowed(found.Widget.Widget, origin))
            return Refused<LiveChatWidgetState>(LiveChatRefusal.OriginNotAllowed);
        return Value(await StateAsync(found.Widget, found.Inbox, cancellationToken).ConfigureAwait(false));
    }

    public async Task<LiveChatResult<VisitorSessionGrant>> StartSessionAsync(AccessPartition partition, string widgetKey, string? origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (await FindWidgetAsync(partition, widgetKey, cancellationToken).ConfigureAwait(false) is not { } found)
            return Refused<VisitorSessionGrant>(LiveChatRefusal.NotFound);
        if (!OriginAllowed(found.Widget.Widget, origin))
            return Refused<VisitorSessionGrant>(LiveChatRefusal.OriginNotAllowed);
        if (!found.Inbox.Inbox.Enabled)
            return Refused<VisitorSessionGrant>(LiveChatRefusal.Unavailable);
        var scope = found.Widget.Scope;
        var now = time.GetUtcNow();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var id = VisitorSessionId.New();
            var token = VisitorSessionTokens.Issue(id);
            var session = new VisitorSession(id, found.Widget.Widget.ConfigurationId, found.Inbox.Inbox.Id, "v_" + RealtimeText.RandomToken(16), token.Hash, now, now,
                now + options.SessionLifetime);
            switch (await sessions.CreateAsync(scope, session, cancellationToken).ConfigureAwait(false))
            {
                case LiveChatWriteApplied<StoredVisitorSession> created:
                    var state = await StateAsync(found.Widget, found.Inbox, cancellationToken).ConfigureAwait(false);
                    return Value(new VisitorSessionGrant(new VisitorSessionContext(created.Value, false), token.Token, state));
                case LiveChatWriteConflict:
                    continue;
                case LiveChatWriteRejected:
                    return Refused<VisitorSessionGrant>(LiveChatRefusal.Rejected, "The session store is at capacity.");
                case LiveChatWriteNotFound:
                    return Refused<VisitorSessionGrant>(LiveChatRefusal.Rejected);
            }
        }
        return Refused<VisitorSessionGrant>(LiveChatRefusal.Rejected);
    }

    public async Task<LiveChatResult<VisitorSessionContext>> AuthenticateAsync(AccessPartition partition, string token, string? origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (VisitorSessionTokens.Parse(token) is not { } parsed)
            return Refused<VisitorSessionContext>(LiveChatRefusal.Unauthenticated);
        var stored = await sessions.GetAsync(partition, parsed.Session, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        // The hash comparison runs for unknown sessions too (against a fixed hash) so timing does not reveal which ids exist.
        var verified = VisitorSessionTokens.Verify(stored?.Session ?? Placeholder, parsed.SecretHash, now);
        if (stored is null || !verified || !stored.Session.IsLive(now))
            return Refused<VisitorSessionContext>(LiveChatRefusal.Unauthenticated);
        var widget = await widgets.GetAsync(stored.Scope, stored.Session.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (widget is null)
            return Refused<VisitorSessionContext>(LiveChatRefusal.Unauthenticated);
        if (!OriginAllowed(widget.Widget, origin))
            return Refused<VisitorSessionContext>(LiveChatRefusal.OriginNotAllowed);
        return Value(new VisitorSessionContext(stored, now - stored.Session.TokenIssuedAt >= options.TokenRotationInterval));
    }

    public async Task<LiveChatResult<LiveChatWidgetState>> GetSessionWidgetAsync(VisitorSessionContext context, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<LiveChatWidgetState>(LiveChatRefusal.Unauthenticated);
        if (await LoadWidgetAsync(session.Scope, session.Session.ConfigurationId, cancellationToken).ConfigureAwait(false) is not { } found)
            return Refused<LiveChatWidgetState>(LiveChatRefusal.Unavailable);
        return Value(await StateAsync(found.Widget, found.Inbox, cancellationToken).ConfigureAwait(false));
    }

    public async Task<LiveChatResult<VisitorTokenRotated>> RotateTokenAsync(VisitorSessionContext context, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<VisitorTokenRotated>(LiveChatRefusal.Unauthenticated);
        var now = time.GetUtcNow();
        if (context.RotationRecommended && session.Session.TokenIssuedAt > now - options.TokenRotationInterval)
            return Value(new VisitorTokenRotated(new VisitorSessionContext(session, false), null));
        var token = VisitorSessionTokens.Issue(session.Session.Id);
        var rotated = session.Session.With(tokenHash: token.Hash, tokenIssuedAt: now, expiresAt: now + options.SessionLifetime,
            previous: (session.Session.TokenHash, now + options.TokenRotationGrace));
        return await UpdateAsync(session, rotated, cancellationToken).ConfigureAwait(false) switch
        {
            LiveChatValue<StoredVisitorSession> updated => Value(new VisitorTokenRotated(new VisitorSessionContext(updated.Value, false), token.Token)),
            LiveChatRefused refused => refused,
        };
    }

    public async Task<LiveChatResult<VisitorSessionContext>> IdentifyAsync(VisitorSessionContext context, VisitorIdentification identification, CancellationToken cancellationToken = default)
    {
        if (identification.Value is null)
            throw new ArgumentException("An identification is required.", nameof(identification));
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<VisitorSessionContext>(LiveChatRefusal.Unauthenticated);
        if (await LoadWidgetAsync(session.Scope, session.Session.ConfigurationId, cancellationToken).ConfigureAwait(false) is not { } found)
            return Refused<VisitorSessionContext>(LiveChatRefusal.Unavailable);
        var policy = found.Widget.Widget.Identity;
        var now = time.GetUtcNow();
        VerifiedVisitorIdentity identity;
        string? displayName;
        switch (identification)
        {
            case HmacVisitorIdentification hmac:
                if (!policy.AllowHmac || secrets is null || !IsIdentityValue(policy.Namespace, hmac.UserId))
                    return Refused<VisitorSessionContext>(LiveChatRefusal.IdentityRejected);
                var secret = await secrets.ResolveAsync(session.Scope, session.Session.ConfigurationId, cancellationToken).ConfigureAwait(false);
                if (secret is not { Length: >= 32 and <= 256 } key || !VisitorIdentityHmac.Verify(key.Span, hmac.UserId, hmac.Signature))
                    return Refused<VisitorSessionContext>(LiveChatRefusal.IdentityRejected);
                identity = new VerifiedVisitorIdentity(policy.Namespace, hmac.UserId, VisitorVerificationMethod.Hmac, now);
                displayName = CleanName(hmac.DisplayName);
                break;
            case SignedTokenVisitorIdentification signed:
                if (!policy.AllowSignedToken || tokenValidator is null || string.IsNullOrEmpty(signed.Token) || signed.Token.Length > 16 * 1024)
                    return Refused<VisitorSessionContext>(LiveChatRefusal.IdentityRejected);
                switch (await tokenValidator.ValidateAsync(session.Scope, session.Session.ConfigurationId, signed.Token, cancellationToken).ConfigureAwait(false))
                {
                    case VisitorTokenAccepted accepted when IsIdentityValue(accepted.Namespace, accepted.Subject):
                        identity = new VerifiedVisitorIdentity(accepted.Namespace, accepted.Subject, VisitorVerificationMethod.SignedToken, now);
                        displayName = accepted.DisplayName;
                        break;
                    case VisitorTokenAccepted:
                    case VisitorTokenRejected:
                        return Refused<VisitorSessionContext>(LiveChatRefusal.IdentityRejected);
                    default:
                        throw new InvalidOperationException("Unknown token validation outcome.");
                }
                break;
            default:
                throw new InvalidOperationException("Unknown identification.");
        }
        if (session.Session.Identity is { } existing)
        {
            if (existing.Namespace != identity.Namespace || existing.Value != identity.Value)
                return Refused<VisitorSessionContext>(LiveChatRefusal.Invalid, "The session is already verified as another identity; start a new session.");
            return Value(new VisitorSessionContext(session, context.RotationRecommended));
        }
        switch (await UpdateAsync(session, session.Session.With(identity: identity, displayName: session.Session.DisplayName ?? displayName), cancellationToken).ConfigureAwait(false))
        {
            case LiveChatValue<StoredVisitorSession> updated:
                await LinkVisitorContactAsync(updated.Value, identity, cancellationToken).ConfigureAwait(false);
                return Value(new VisitorSessionContext(updated.Value, context.RotationRecommended));
            case LiveChatRefused refused:
                return refused;
        }
        throw new InvalidOperationException("Unknown session update outcome.");
    }

    /// <summary>
    /// E16 hook: after a session is verified, links the anonymous visitor contact (the claimed <c>WidgetVisitor</c> identity of this
    /// session's earlier messages) to the verified account through <see cref="IContactLinkingService"/> as the channel worker, so it
    /// is merged into an existing account contact (its conversations follow the merge) or becomes the account contact. Best effort:
    /// failures are logged without identifiers and never fail the identification.
    /// </summary>
    private async Task LinkVisitorContactAsync(StoredVisitorSession session, VerifiedVisitorIdentity identity, CancellationToken cancellationToken)
    {
        if (linking is null || !options.LinkVisitorContactOnIdentify
            || ContactIdentityAddress.Normalize(ContactIdentityKind.WidgetVisitor, session.Session.VisitorId) is not { Value: ContactIdentityAddress visitor })
            return;
        try
        {
            var source = identity.Method == VisitorVerificationMethod.Hmac ? "livechat-hmac" : "livechat-token";
            var request = new ContactLinkRequest(new ExternalAccountRef(identity.Namespace, identity.Value), source, evidence: visitor);
            switch (await linking.LinkAccountAsync(channelOptions.ResolveWorker(session.Scope), request, cancellationToken).ConfigureAwait(false))
            {
                case ContactRefused { Reason: ContactRefusal.Forbidden }:
                    logger.LogWarning("The channel worker principal was refused while linking a verified visitor contact; grant contact:link and contact:merge to enable it.");
                    break;
                case ContactLinkConflict conflict:
                    logger.LogInformation("A verified visitor contact was not linked ({Reason}).", conflict.Reason);
                    break;
                case ContactLinked:
                case ContactMergedInto:
                case ContactAlreadyLinked:
                case ContactRefused:
                    break;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Linking a verified visitor contact failed ({Error}).", error.GetType().Name);
        }
    }

    public async Task<LiveChatResult<VisitorSessionContext>> SubmitPrechatAsync(VisitorSessionContext context, IReadOnlyDictionary<string, string> answers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<VisitorSessionContext>(LiveChatRefusal.Unauthenticated);
        if (await LoadWidgetAsync(session.Scope, session.Session.ConfigurationId, cancellationToken).ConfigureAwait(false) is not { } found)
            return Refused<VisitorSessionContext>(LiveChatRefusal.Unavailable);
        var fields = found.Widget.Widget.PrechatFields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        if (answers.Count > fields.Count)
            return Refused<VisitorSessionContext>(LiveChatRefusal.Invalid, "Unknown pre-chat fields.");
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        string? displayName = null;
        foreach (var (key, raw) in answers)
        {
            if (key is null || !fields.TryGetValue(key, out var field))
                return Refused<VisitorSessionContext>(LiveChatRefusal.Invalid, "Unknown pre-chat fields.");
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            if (value.Length > field.MaximumLength || !LiveChatText.IsClean(value, allowNewLines: false))
                return Refused<VisitorSessionContext>(LiveChatRefusal.Invalid, $"The answer for '{field.Key}' is too long or contains control characters.");
            if (field.Kind == PrechatFieldKind.Email)
            {
                if (ContactIdentityAddress.Normalize(ContactIdentityKind.Email, value) is not { Value: ContactIdentityAddress email })
                    return Refused<VisitorSessionContext>(LiveChatRefusal.Invalid, $"The answer for '{field.Key}' is not a valid email address.");
                value = email.Value;
            }
            if (field.Kind == PrechatFieldKind.Name)
                displayName = value;
            normalized[key] = value;
        }
        if (fields.Values.FirstOrDefault(field => field.Required && !normalized.ContainsKey(field.Key)) is { } missing)
            return Refused<VisitorSessionContext>(LiveChatRefusal.PrechatRequired, $"'{missing.Key}' is required.");
        switch (await UpdateAsync(session, session.Session.With(prechat: normalized, displayName: displayName), cancellationToken).ConfigureAwait(false))
        {
            case LiveChatValue<StoredVisitorSession> updated:
                return Value(new VisitorSessionContext(updated.Value, context.RotationRecommended));
            case LiveChatRefused refused:
                return refused;
        }
        throw new InvalidOperationException("Unknown session update outcome.");
    }

    // ---- Messages ----

    public async Task<LiveChatResult<VisitorMessageAccepted>> SendMessageAsync(VisitorSessionContext context, VisitorMessageDraft message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<VisitorMessageAccepted>(LiveChatRefusal.Unauthenticated);
        var scope = session.Scope;
        var visitor = session.Session;
        if (await LoadWidgetAsync(scope, visitor.ConfigurationId, cancellationToken).ConfigureAwait(false) is not { } found || !found.Inbox.Inbox.Enabled)
            return Refused<VisitorMessageAccepted>(LiveChatRefusal.Unavailable);
        var widget = found.Widget.Widget;
        if (widget.Identity.RequireVerified && visitor.Identity is null)
            return Refused<VisitorMessageAccepted>(LiveChatRefusal.IdentityRequired);
        if (widget.PrechatFields.FirstOrDefault(field => field.Required && !visitor.Prechat.ContainsKey(field.Key)) is { } missing)
            return Refused<VisitorMessageAccepted>(LiveChatRefusal.PrechatRequired, $"'{missing.Key}' is required.");
        if (!LiveChatText.IsPrintableAscii(message.ClientMessageId, 64))
            return Refused<VisitorMessageAccepted>(LiveChatRefusal.Invalid, "Client message ids are 1-64 printable ASCII characters.");

        var refs = message.AttachmentRefs ?? [];
        var resolved = new List<AttachmentRef>();
        if (refs.Count > 0)
        {
            if (widget.Attachments is null || attachments is null)
                return Refused<VisitorMessageAccepted>(LiveChatRefusal.Unavailable, "Attachments are disabled.");
            if (refs.Count > LiveChatChannel.MaximumAttachments)
                return Refused<VisitorMessageAccepted>(LiveChatRefusal.Invalid, $"At most {LiveChatChannel.MaximumAttachments} attachments are allowed.");
            foreach (var reference in refs.Distinct(StringComparer.Ordinal))
            {
                if (reference is null || reference.Length > AttachmentRef.MaximumStorageRefLength
                    || await attachments.FindAsync(scope, visitor.Id, reference, cancellationToken).ConfigureAwait(false) is not { } attachment)
                    return Refused<VisitorMessageAccepted>(LiveChatRefusal.Invalid, "Unknown attachment.");
                resolved.Add(attachment);
            }
        }

        var sender = visitor.Identity is { } identity
            ? new ChannelSenderEvidence(ContactIdentityKind.ExternalUser, identity.Value, verified: true, identity.Namespace)
            : new ChannelSenderEvidence(ContactIdentityKind.WidgetVisitor, visitor.VisitorId, verified: false);
        InboundChannelMessage inbound;
        try
        {
            inbound = new InboundChannelMessage(scope, kind, visitor.ConfigurationId, $"lc:{visitor.Id.Value}:{message.ClientMessageId}", sender, time.GetUtcNow(),
                bodyText: message.Text, attachments: resolved, externalThreadId: visitor.VisitorId, senderDisplayName: visitor.DisplayName);
        }
        catch (ArgumentException)
        {
            return Refused<VisitorMessageAccepted>(LiveChatRefusal.Invalid, "The message is empty, too long or not well-formed text.");
        }

        var worker = channelOptions.ResolveWorker(scope);
        switch (await pipeline.ReceiveAsync(worker, inbound, cancellationToken).ConfigureAwait(false))
        {
            case InboundRecorded recorded:
                switch (await sessions.LinkAsync(scope, visitor.Id, recorded.Conversation, time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
                {
                    case LiveChatWriteApplied<VisitorConversationLink>:
                        break;
                    case LiveChatWriteRejected:
                    case LiveChatWriteConflict:
                        return Refused<VisitorMessageAccepted>(LiveChatRefusal.Rejected, "The session has too many conversations; start a new session.");
                    case LiveChatWriteNotFound:
                        return Refused<VisitorMessageAccepted>(LiveChatRefusal.Unauthenticated);
                }
                var row = await conversations.GetMessageAsync(scope, recorded.Conversation, recorded.Message, cancellationToken).ConfigureAwait(false);
                var sequence = row?.Sequence ?? 0;
                if (!recorded.Replayed && sequence > 0)
                    await PublishAsync(new RealtimePublication(new VisitorSessionTopic(scope, visitor.Id), new VisitorMessageEvent(scope, visitor.Id, recorded.Conversation,
                        recorded.Message, sequence, MessageDirection.Incoming, inbound.BodyText ?? string.Empty, MessageContentType.PlainText, resolved, time.GetUtcNow())),
                        cancellationToken).ConfigureAwait(false);
                return Value(new VisitorMessageAccepted(recorded.Conversation, recorded.Message, sequence, recorded.StartedConversation, recorded.Replayed));
            case InboundRejected rejected:
                return rejected.Reason switch
                {
                    InboundRejection.InboxNotFound or InboundRejection.InboxDisabled or InboundRejection.ChannelMismatch => Refused<VisitorMessageAccepted>(LiveChatRefusal.Unavailable),
                    InboundRejection.InvalidSender or InboundRejection.Invalid => Refused<VisitorMessageAccepted>(LiveChatRefusal.Invalid, "The message is invalid."),
                    InboundRejection.ContactBlocked => Refused<VisitorMessageAccepted>(LiveChatRefusal.Forbidden),
                    InboundRejection.Forbidden => WorkerRefused(),
                    InboundRejection.ContactAmbiguous or InboundRejection.Rejected => Refused<VisitorMessageAccepted>(LiveChatRefusal.Rejected),
                    _ => Refused<VisitorMessageAccepted>(LiveChatRefusal.Rejected),
                };
            default:
                throw new InvalidOperationException("Unknown inbound outcome.");
        }
    }

    public async Task<LiveChatResult<IReadOnlyList<VisitorConversationView>>> ListConversationsAsync(VisitorSessionContext context, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<IReadOnlyList<VisitorConversationView>>(LiveChatRefusal.Unauthenticated);
        var links = await sessions.ListLinksAsync(session.Scope, session.Session.Id, cancellationToken).ConfigureAwait(false);
        if (links.Count == 0)
            return Value<IReadOnlyList<VisitorConversationView>>([]);
        var ids = new ConversationId[links.Count];
        for (var i = 0; i < links.Count; i++)
            ids[i] = links[i].Conversation;
        var loaded = await conversations.GetManyAsync(session.Scope, ids, cancellationToken).ConfigureAwait(false);
        var views = new List<VisitorConversationView>(loaded.Count);
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (loaded.TryGetValue(link.Conversation, out var stored))
                views.Add(new VisitorConversationView(link.Conversation, stored.Conversation.Status.Value is ClosedStatus or ResolvedStatus, stored.Conversation.LastSequence,
                    link.ReadSequence, link.LinkedAt, stored.Conversation.LastActivityAt));
        }
        return Value<IReadOnlyList<VisitorConversationView>>(views);
    }

    public async Task<LiveChatResult<VisitorMessagePage>> ReadMessagesAsync(VisitorSessionContext context, ConversationId conversation, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<VisitorMessagePage>(LiveChatRefusal.Unauthenticated);
        if (afterSequence < 0 || limit < 1)
            return Refused<VisitorMessagePage>(LiveChatRefusal.Invalid);
        if (await LinkAsync(session, conversation, cancellationToken).ConfigureAwait(false) is not { } link)
            return Refused<VisitorMessagePage>(LiveChatRefusal.NotFound);
        var take = Math.Min(limit, options.MaximumMessagesPerRead);
        var events = await conversations.ReadTimelineAsync(session.Scope, conversation, afterSequence, take, includeNotes: false, cancellationToken).ConfigureAwait(false);
        var messages = new List<VisitorMessageView>();
        foreach (var item in events)
            if (item.Event.Value is MessageEvent message && !(message.Direction == MessageDirection.Outgoing && message.Delivery.State == MessageDeliveryState.Failed))
                messages.Add(new VisitorMessageView(conversation, message.Id, item.Sequence, message.Direction, message.Body, message.ContentType, message.Attachments,
                    item.OccurredAt, message.Delivery.State));
        return Value(new VisitorMessagePage(conversation, messages, events.Count == take ? events[^1].Sequence : null, link.ReadSequence));
    }

    public async Task<LiveChatResult<bool>> SetTypingAsync(VisitorSessionContext context, ConversationId conversation, bool typing, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<bool>(LiveChatRefusal.Unauthenticated);
        if (await LinkAsync(session, conversation, cancellationToken).ConfigureAwait(false) is null)
            return Refused<bool>(LiveChatRefusal.NotFound);
        await PublishAsync(new RealtimePublication(new ConversationTopic(session.Scope, conversation),
            new TypingEvent(session.Scope, conversation, VisitorActor.Instance, typing, time.GetUtcNow())), cancellationToken).ConfigureAwait(false);
        return Value(true);
    }

    public async Task<LiveChatResult<long>> MarkReadAsync(VisitorSessionContext context, ConversationId conversation, long upToSequence, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<long>(LiveChatRefusal.Unauthenticated);
        if (await LinkAsync(session, conversation, cancellationToken).ConfigureAwait(false) is null
            || await conversations.GetAsync(session.Scope, conversation, cancellationToken).ConfigureAwait(false) is not { } stored)
            return Refused<long>(LiveChatRefusal.NotFound);
        if (upToSequence < 1 || upToSequence > stored.Conversation.LastSequence)
            return Refused<long>(LiveChatRefusal.Invalid);
        switch (await sessions.AdvanceCursorAsync(session.Scope, session.Session.Id, conversation, upToSequence, cancellationToken).ConfigureAwait(false))
        {
            case LiveChatWriteApplied<VisitorConversationLink> advanced:
                await PublishAsync(new RealtimePublication(new ConversationTopic(session.Scope, conversation),
                    new ReadReceiptEvent(session.Scope, conversation, VisitorActor.Instance, advanced.Value.ReadSequence, time.GetUtcNow())), cancellationToken).ConfigureAwait(false);
                return Value(advanced.Value.ReadSequence);
            case LiveChatWriteNotFound:
                return Refused<long>(LiveChatRefusal.NotFound);
            case LiveChatWriteConflict:
            case LiveChatWriteRejected:
                return Refused<long>(LiveChatRefusal.Rejected);
        }
        throw new InvalidOperationException("Unknown cursor outcome.");
    }

    public async Task<LiveChatResult<AttachmentRef>> UploadAttachmentAsync(VisitorSessionContext context, string fileName, string mediaType, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<AttachmentRef>(LiveChatRefusal.Unauthenticated);
        if (await LoadWidgetAsync(session.Scope, session.Session.ConfigurationId, cancellationToken).ConfigureAwait(false) is not { Widget.Widget.Attachments: { } policy }
            || attachments is null)
            return Refused<AttachmentRef>(LiveChatRefusal.Unavailable, "Attachments are disabled.");
        var type = mediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (type is null || !LiveChatAttachmentPolicy.IsMediaType(type) || !policy.Allows(type))
            return Refused<AttachmentRef>(LiveChatRefusal.UnsupportedMediaType);
        if (SafeFileName(fileName) is not { } name)
            return Refused<AttachmentRef>(LiveChatRefusal.Invalid, "The file name is invalid.");
        var limited = new LimitedReadStream(content, policy.MaximumBytes);
        StoredAttachment stored;
        try
        {
            stored = await attachments.StoreAsync(new AttachmentUpload(session.Scope, session.Session.Inbox, session.Session.Id, name, type, policy.MaximumBytes, limited),
                cancellationToken).ConfigureAwait(false);
        }
        catch (AttachmentTooLargeException)
        {
            return Refused<AttachmentRef>(LiveChatRefusal.TooLarge);
        }
        if (stored.SizeBytes > policy.MaximumBytes || limited.BytesRead > policy.MaximumBytes)
            return Refused<AttachmentRef>(LiveChatRefusal.TooLarge);
        try
        {
            return Value(new AttachmentRef(stored.StorageRef, name, type, stored.SizeBytes));
        }
        catch (ArgumentException)
        {
            return Refused<AttachmentRef>(LiveChatRefusal.Rejected, "The attachment store returned an invalid reference.");
        }
    }

    public async Task<LiveChatResult<IReadOnlyList<ArticleSnippet>>> SearchArticlesAsync(VisitorSessionContext context, string text, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<IReadOnlyList<ArticleSnippet>>(LiveChatRefusal.Unauthenticated);
        if (await LoadWidgetAsync(session.Scope, session.Session.ConfigurationId, cancellationToken).ConfigureAwait(false) is not { Widget.Widget.Deflection: { } deflection }
            || helpCenter is null)
            return Refused<IReadOnlyList<ArticleSnippet>>(LiveChatRefusal.Unavailable, "Deflection is disabled.");
        HelpCenterSearchQuery query;
        try
        {
            query = new HelpCenterSearchQuery(deflection.Portal, deflection.Locale, text, deflection.MaximumResults);
        }
        catch (ArgumentException)
        {
            return Refused<IReadOnlyList<ArticleSnippet>>(LiveChatRefusal.Invalid, "The search text is invalid.");
        }
        return Value(await helpCenter.SearchPublicAsync(session.Scope, query, cancellationToken).ConfigureAwait(false));
    }

    public async Task<LiveChatResult<bool>> EndSessionAsync(VisitorSessionContext context, CancellationToken cancellationToken = default)
    {
        if (await RequireLiveAsync(context, cancellationToken).ConfigureAwait(false) is not { } session)
            return Refused<bool>(LiveChatRefusal.Unauthenticated);
        switch (await UpdateAsync(session, session.Session.With(revoked: true), cancellationToken).ConfigureAwait(false))
        {
            case LiveChatValue<StoredVisitorSession>:
                await PublishAsync(new RealtimePublication(new VisitorSessionTopic(session.Scope, session.Session.Id),
                    new VisitorSessionEndedEvent(session.Scope, session.Session.Id, VisitorSessionEnd.Revoked, time.GetUtcNow())), cancellationToken).ConfigureAwait(false);
                return Value(true);
            case LiveChatRefused refused:
                return refused;
        }
        throw new InvalidOperationException("Unknown session update outcome.");
    }

    // ---- Helpers ----

    private static readonly VisitorSession Placeholder = new(new VisitorSessionId("vs_placeholder"), "placeholder", new InboxId("placeholder"), "placeholder",
        new byte[VisitorSession.TokenHashLength], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1));

    private async Task<(StoredLiveChatWidget Widget, StoredInbox Inbox)?> FindWidgetAsync(AccessPartition partition, string? widgetKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(widgetKey) || widgetKey.Length > ChannelEndpoint.MaximumAddressLength || !LiveChatText.IsClean(widgetKey, allowNewLines: false))
            return null;
        var endpoint = await endpoints.FindByAddressAsync(partition, kind, widgetKey, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
            return null;
        var found = await LoadWidgetAsync(endpoint.Scope, endpoint.Endpoint.ConfigurationId, cancellationToken).ConfigureAwait(false);
        return found is { } value && value.Inbox.Inbox.Id == endpoint.Endpoint.Inbox ? found : null;
    }

    private async Task<(StoredLiveChatWidget Widget, StoredInbox Inbox)?> LoadWidgetAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken)
    {
        var widget = await widgets.GetAsync(scope, configurationId, cancellationToken).ConfigureAwait(false);
        if (widget is null)
            return null;
        var inbox = await inboxes.GetAsync(scope, widget.Widget.Inbox, cancellationToken).ConfigureAwait(false);
        if (inbox is null || inbox.Inbox.Channel.Kind != kind || !string.Equals(inbox.Inbox.Channel.ConfigurationId, configurationId, StringComparison.Ordinal))
            return null;
        return (widget, inbox);
    }

    private async Task<LiveChatWidgetState> StateAsync(StoredLiveChatWidget stored, StoredInbox inbox, CancellationToken cancellationToken)
    {
        var widget = stored.Widget;
        var online = true;
        if (inbox.Inbox.Calendar is { } calendarId && await calendars.GetAsync(stored.Scope, calendarId, cancellationToken).ConfigureAwait(false) is { } calendar)
        {
            try
            {
                online = new BusinessCalendarCalculator(calendar.Calendar).IsOpen(time.GetUtcNow());
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                logger.LogWarning("The business calendar of inbox {Inbox} uses an unknown time zone; the widget is treated as online.", inbox.Inbox.Id);
            }
        }
        return new LiveChatWidgetState(widget.ConfigurationId, widget.ThemeRef, widget.PrechatFields, widget.GreetingMessage ?? inbox.Inbox.GreetingMessage, online,
            online ? null : widget.AwayMessage ?? inbox.Inbox.AwayMessage, widget.Deflection is not null && helpCenter is not null,
            attachments is null ? null : widget.Attachments, widget.Identity.RequireVerified, widget.Identity.AllowHmac && secrets is not null,
            widget.Identity.AllowSignedToken && tokenValidator is not null);
    }

    private bool OriginAllowed(LiveChatWidget widget, string? origin) =>
        origin is null ? options.AllowMissingOrigin : LiveChatOrigin.IsAllowed(widget, origin);

    private async Task<StoredVisitorSession?> RequireLiveAsync(VisitorSessionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var current = await sessions.GetAsync(AccessPartition.From(context.Scope), context.Session.Session.Id, cancellationToken).ConfigureAwait(false);
        return current is not null && current.Scope == context.Scope && current.Session.IsLive(time.GetUtcNow()) ? current : null;
    }

    private Task<VisitorConversationLink?> LinkAsync(StoredVisitorSession session, ConversationId conversation, CancellationToken cancellationToken) =>
        conversation == default ? Task.FromResult<VisitorConversationLink?>(null) : sessions.GetLinkAsync(session.Scope, session.Session.Id, conversation, cancellationToken);

    private async Task<LiveChatResult<StoredVisitorSession>> UpdateAsync(StoredVisitorSession current, VisitorSession next, CancellationToken cancellationToken) =>
        await sessions.UpdateAsync(current.Scope, next, current.Revision, cancellationToken).ConfigureAwait(false) switch
        {
            LiveChatWriteApplied<StoredVisitorSession> applied => Value(applied.Value),
            LiveChatWriteNotFound => Refused<StoredVisitorSession>(LiveChatRefusal.Unauthenticated),
            LiveChatWriteConflict => Refused<StoredVisitorSession>(LiveChatRefusal.Conflict, "The session changed concurrently; retry."),
            LiveChatWriteRejected => Refused<StoredVisitorSession>(LiveChatRefusal.Rejected),
        };

    private async Task PublishAsync(RealtimePublication publication, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(publication, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Realtime is a hint layer: the stored message and cursors are the source of truth.
            logger.LogWarning("A live-chat realtime publication failed ({Error}).", error.GetType().Name);
        }
    }

    private LiveChatResult<VisitorMessageAccepted> WorkerRefused()
    {
        logger.LogWarning("The channel worker principal was refused while recording a live-chat message; check its grants.");
        return Refused<VisitorMessageAccepted>(LiveChatRefusal.Unavailable);
    }

    private static bool IsIdentityValue(string @namespace, string? value) =>
        value is not null && ContactIdentityAddress.Normalize(ContactIdentityKind.ExternalUser, value, @namespace) is { Value: ContactIdentityAddress };

    private static string? CleanName(string? name)
    {
        var value = name?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 256 || !LiveChatText.IsClean(value, allowNewLines: false) ? null : value;
    }

    private static readonly char[] PathSeparators = ['/', '\\'];

    internal static string? SafeFileName(string? fileName)
    {
        if (fileName is null)
            return null;
        var name = fileName[(fileName.LastIndexOfAny(PathSeparators) + 1)..].Trim();
        return name.Length is 0 or > AttachmentRef.MaximumFileNameLength || name is "." or ".." || !LiveChatText.IsClean(name, allowNewLines: false) ? null : name;
    }

    private static LiveChatResult<T> Value<T>(T value) => new LiveChatValue<T>(value);

    private static LiveChatResult<T> Refused<T>(LiveChatRefusal reason, string? error = null) => LiveChatRefused.Of(reason, error);
}
