using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>
/// Default <see cref="IInboundChannelPipeline"/>. Every read and write of support data goes through the Access-checked contact and
/// conversation services as the calling principal; trusted store reads are used only for correlation (endpoint, inbox, reference
/// chain, existing message) and are followed by an authorization check before anything is returned. A reference-chain or thread
/// match is used only when the conversation belongs to the resolved contact and is not closed, so a forged Message-Id cannot inject
/// messages into another contact's conversation. Channel state (recipient, last incoming time, references) is updated after the
/// conversation write and again on replays, so at-least-once redelivery converges. Never logs bodies or addresses.
/// </summary>
public sealed class InboundChannelPipeline : IInboundChannelPipeline
{
    private readonly IChannelEndpointStore endpoints;
    private readonly IChannelConversationStateStore states;
    private readonly IChannelThreadStore threads;
    private readonly IInboxStore inboxes;
    private readonly IContactService contacts;
    private readonly IConversationService conversations;
    private readonly IConversationStore conversationStore;
    private readonly IAccessEvaluator evaluator;
    private readonly ChannelsOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public InboundChannelPipeline(
        IChannelEndpointStore endpoints,
        IChannelConversationStateStore states,
        IChannelThreadStore threads,
        IInboxStore inboxes,
        IContactService contacts,
        IConversationService conversations,
        IConversationStore conversationStore,
        IAccessEvaluator evaluator,
        IOptions<ChannelsOptions> options,
        TimeProvider time,
        ILogger<InboundChannelPipeline>? logger = null)
    {
        this.endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        this.states = states ?? throw new ArgumentNullException(nameof(states));
        this.threads = threads ?? throw new ArgumentNullException(nameof(threads));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.logger = logger ?? NullLogger<InboundChannelPipeline>.Instance;
    }

    public async Task<InboundResult> ReceiveAsync(PrincipalRef actor, InboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        if (actor.Value is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
        ArgumentNullException.ThrowIfNull(message);
        if (actor.Scope != message.Scope)
            return InboundRejected.Of(InboundRejection.Forbidden, "The principal acts in a different scope.");
        var scope = message.Scope;
        var receivedAt = ReceivedAt(message);

        var endpoint = await endpoints.GetAsync(scope, message.Channel, message.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
            return InboundRejected.Of(InboundRejection.InboxNotFound, "No channel endpoint matches the binding.");
        var inbox = await inboxes.GetAsync(scope, endpoint.Endpoint.Inbox, cancellationToken).ConfigureAwait(false);
        if (inbox is null)
            return InboundRejected.Of(InboundRejection.InboxNotFound, "The endpoint's inbox does not exist.");
        if (inbox.Inbox.Channel.Kind != message.Channel || !string.Equals(inbox.Inbox.Channel.ConfigurationId, message.ConfigurationId, StringComparison.Ordinal))
            return InboundRejected.Of(InboundRejection.ChannelMismatch, "The inbox is bound to a different channel configuration.");
        if (!inbox.Inbox.Enabled)
            return InboundRejected.Of(InboundRejection.InboxDisabled, "The inbox is disabled.");
        var inboxId = inbox.Inbox.Id;

        ContactIdentityAddress address;
        switch (ContactIdentityAddress.Normalize(message.Sender.Kind, message.Sender.Value, message.Sender.Namespace, options.DefaultCallingCode))
        {
            case ContactIdentityAddress normalized:
                address = normalized;
                break;
            case ContactAddressInvalid invalid:
                return InboundRejected.Of(InboundRejection.InvalidSender, $"The sender address is invalid ({invalid.Error}).");
            default:
                throw new InvalidOperationException("Unknown normalization outcome.");
        }
        var recipient = ChannelRecipient.From(address);

        IncomingMessageDraft draft;
        try
        {
            draft = Draft(message);
        }
        catch (ArgumentException)
        {
            return InboundRejected.Of(InboundRejection.Invalid, "The message is invalid or exceeds message limits.");
        }

        if (await conversationStore.FindMessageByExternalIdAsync(scope, inboxId, message.ExternalMessageId, cancellationToken).ConfigureAwait(false) is { } existing
            && await conversationStore.GetAsync(scope, existing.Conversation, cancellationToken).ConfigureAwait(false) is { } owner)
        {
            if (!await evaluator.PermittedAsync(actor, ConversationPermissions.Ingest, ConversationAccess.Resource(owner), cancellationToken).ConfigureAwait(false))
                return InboundRejected.Of(InboundRejection.Forbidden);
            await RecordStateAsync(scope, inboxId, existing.Conversation, existing.Message, message, receivedAt, recipient, contact: null, replay: true, cancellationToken).ConfigureAwait(false);
            return new InboundRecorded(existing.Conversation, existing.Message, false, true);
        }

        var source = VerificationSource(message.Channel);
        var evidence = new ContactIdentityEvidence(address, message.Sender.Verified
            ? new IdentityVerified(source, receivedAt)
            : new IdentityClaimed(source, receivedAt));
        ContactView contact;
        switch (await contacts.ResolveAsync(actor, new ContactResolveRequest([evidence], Profile(message), options.ClaimedIdentityMatching), cancellationToken).ConfigureAwait(false))
        {
            case ContactMatched matched:
                contact = matched.Contact;
                break;
            case ContactCreated created:
                contact = created.Contact;
                break;
            case ContactAmbiguous:
                return InboundRejected.Of(InboundRejection.ContactAmbiguous, "The sender matches several contacts.");
            case ContactRefused refused:
                return refused.Reason == ContactRefusal.Forbidden
                    ? InboundRejected.Of(InboundRejection.Forbidden)
                    : InboundRejected.Of(InboundRejection.Invalid, "The contact could not be resolved.");
            default:
                throw new InvalidOperationException("Unknown resolution outcome.");
        }
        if (contact.Blocked)
        {
            logger.LogInformation("Dropped an inbound message from a blocked contact in inbox {Inbox}.", inboxId);
            return InboundRejected.Of(InboundRejection.ContactBlocked, "The contact is blocked.");
        }

        if (await CorrelateAsync(actor, scope, inboxId, contact.Id, message, cancellationToken).ConfigureAwait(false) is { } conversationId)
        {
            var appended = await conversations.AppendIncomingAsync(actor, conversationId, draft, cancellationToken).ConfigureAwait(false);
            switch (appended)
            {
                case ConversationApplied applied:
                    return await RecordedAsync(scope, inboxId, applied, started: false, message, receivedAt, recipient, contact.Id, cancellationToken).ConfigureAwait(false);
                case ConversationNotFound:
                    break;
                case ConversationConflict:
                    return InboundRejected.Of(InboundRejection.Rejected, "Concurrent writers kept winning; retry.");
                case ConversationRefused { Reason: ConversationRefusal.Closed }:
                    break;
                case ConversationRefused refused:
                    return Rejected(refused);
            }
        }

        ChannelThreadRef? thread = null;
        if (message.ExternalThreadId is { } threadId)
        {
            try
            {
                thread = new ChannelThreadRef(message.Channel.Value, threadId);
            }
            catch (ArgumentException)
            {
                thread = null;
            }
        }
        var start = await conversations.StartAsync(actor, new StartConversationRequest(inboxId, contact.Id, thread, InitialMessage: draft), cancellationToken).ConfigureAwait(false);
        return start switch
        {
            ConversationApplied applied => await RecordedAsync(scope, inboxId, applied, started: !applied.Replayed, message, receivedAt, recipient, contact.Id, cancellationToken).ConfigureAwait(false),
            ConversationNotFound => InboundRejected.Of(InboundRejection.Invalid, "The conversation could not be started."),
            ConversationConflict => InboundRejected.Of(InboundRejection.Rejected, "The conversation identifier is already used."),
            ConversationRefused refused => Rejected(refused),
        };
    }

    private async Task<InboundResult> RecordedAsync(TicketScope scope, InboxId inbox, ConversationApplied applied, bool started, InboundChannelMessage message,
        DateTimeOffset receivedAt, ChannelRecipient recipient, ContactId contact, CancellationToken cancellationToken)
    {
        var incoming = applied.Events.Select(item => item.Event.Value).OfType<MessageEvent>().FirstOrDefault(item => item.Direction == MessageDirection.Incoming)
            ?? throw new InvalidOperationException("The conversation service returned no incoming message event.");
        await RecordStateAsync(scope, inbox, applied.Conversation, incoming.Id, message, receivedAt, recipient, contact, applied.Replayed, cancellationToken).ConfigureAwait(false);
        return new InboundRecorded(applied.Conversation, incoming.Id, started, applied.Replayed);
    }

    /// <summary>Reference chain first (In-Reply-To, then References newest first), then the channel thread; same contact, not closed.</summary>
    private async Task<ConversationId?> CorrelateAsync(PrincipalRef actor, TicketScope scope, InboxId inbox, ContactId contact, InboundChannelMessage message, CancellationToken cancellationToken)
    {
        var references = new List<string>();
        foreach (var candidate in new[] { message.InReplyTo }.Concat(message.References.Reverse()))
            if (ChannelText.NormalizeReference(candidate) is { } reference && !references.Contains(reference, StringComparer.Ordinal) && references.Count < IChannelThreadStore.MaximumReferencesPerCall)
                references.Add(reference);
        if (references.Count > 0
            && await threads.FindConversationAsync(scope, inbox, references, cancellationToken).ConfigureAwait(false) is { } referenced
            && await conversationStore.GetAsync(scope, referenced, cancellationToken).ConfigureAwait(false) is { } stored
            && stored.Conversation.Contact == contact && stored.Conversation.Status.Value is not ClosedStatus)
            return referenced;

        if (message.ExternalThreadId is not { } threadId)
            return null;
        ChannelThreadRef thread;
        try
        {
            thread = new ChannelThreadRef(message.Channel.Value, threadId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        // Per-contact lookup first: a shared thread (a Slack thread with several speakers) keeps one conversation per contact.
        StoredConversation? newest = null;
        foreach (var candidate in await states.FindByThreadAsync(scope, inbox, message.Channel, threadId, contact, cancellationToken).ConfigureAwait(false))
            if (await conversationStore.GetAsync(scope, candidate, cancellationToken).ConfigureAwait(false) is { } owned
                && owned.Conversation.Contact == contact && owned.Conversation.Status.Value is not ClosedStatus
                && (newest is null || owned.Conversation.CreatedAt > newest.Conversation.CreatedAt))
                newest = owned;
        if (newest is not null)
            return newest.Id;
        var found = await conversations.FindByThreadAsync(actor, inbox, thread, cancellationToken).ConfigureAwait(false);
        return found.Value is ConversationView view && view.Contact == contact ? view.Id : null;
    }

    /// <summary>
    /// Records channel state for a recorded message. A replay (a redelivered external id, whose sender fields are not trusted to
    /// match the original) only creates missing state and never changes an existing recipient.
    /// </summary>
    private async Task RecordStateAsync(TicketScope scope, InboxId inbox, ConversationId conversation, MessageId messageId, InboundChannelMessage message,
        DateTimeOffset receivedAt, ChannelRecipient recipient, ContactId? contact, bool replay, CancellationToken cancellationToken)
    {
        var ownReference = ChannelText.NormalizeReference(message.ExternalMessageId);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await states.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
            if (current is not null && (replay || current.State.Inbox != inbox || current.State.Channel != message.Channel))
                break;
            var newer = current?.State.LastIncomingAt is not { } last || receivedAt >= last;
            var next = new ChannelConversationState(conversation, inbox, message.Channel,
                newer ? recipient : current!.State.Recipient,
                current?.State.ExternalThreadId ?? message.ExternalThreadId,
                current?.State.Subject ?? ThreadSubject(message.Subject),
                newer ? receivedAt : current!.State.LastIncomingAt,
                newer ? ownReference : current!.State.LastIncomingExternalId,
                contact ?? current?.State.Contact);
            if (current is not null && Same(current.State, next))
                break;
            var written = await states.WriteAsync(scope, next, current?.Revision, cancellationToken).ConfigureAwait(false);
            if (written.Value is not ChannelWriteConflict)
                break;
        }

        var references = new List<ChannelMessageReference>();
        if (ownReference is not null)
            references.Add(new ChannelMessageReference(ownReference, MessageDirection.Incoming, messageId));
        foreach (var candidate in message.References.Append(message.InReplyTo))
            if (ChannelText.NormalizeReference(candidate) is { } reference && references.All(item => item.Reference != reference)
                && references.Count < IChannelThreadStore.MaximumReferencesPerCall)
                references.Add(new ChannelMessageReference(reference, MessageDirection.Incoming));
        if (references.Count > 0)
            await threads.AddAsync(scope, inbox, conversation, references, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private static bool Same(ChannelConversationState left, ChannelConversationState right) =>
        left.Recipient == right.Recipient && left.Contact == right.Contact && left.ExternalThreadId == right.ExternalThreadId && left.Subject == right.Subject
        && left.LastIncomingAt == right.LastIncomingAt && left.LastIncomingExternalId == right.LastIncomingExternalId;

    /// <summary>Provider receive times later than now plus <see cref="MaximumFutureSkew"/> are clamped, so a skewed or forged timestamp cannot hold a reply window open.</summary>
    private DateTimeOffset ReceivedAt(InboundChannelMessage message)
    {
        var latest = time.GetUtcNow() + MaximumFutureSkew;
        return message.ReceivedAt > latest ? latest : message.ReceivedAt;
    }

    /// <summary>Allowed clock skew for provider receive times (2 minutes).</summary>
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(2);

    private static IncomingMessageDraft Draft(InboundChannelMessage message)
    {
        var (raw, type) = !string.IsNullOrWhiteSpace(message.BodyText)
            ? (message.BodyText!, MessageContentType.PlainText)
            : !string.IsNullOrWhiteSpace(message.BodyHtml) ? (message.BodyHtml!, MessageContentType.Html) : (string.Empty, MessageContentType.PlainText);
        var body = ChannelText.SanitizeBody(raw, ConversationMessageLimits.MaximumBodyBytes);
        return new IncomingMessageDraft(body, type, message.Attachments, message.ExternalMessageId);
    }

    private static ContactProfile? Profile(InboundChannelMessage message)
    {
        if (message.SenderDisplayName is null)
            return null;
        try
        {
            return new ContactProfile(displayName: message.SenderDisplayName.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The thread subject without leading reply/forward prefixes.</summary>
    internal static string? ThreadSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return null;
        var value = subject.Trim();
        while (true)
        {
            var stripped = false;
            foreach (var prefix in new[] { "re:", "fw:", "fwd:", "aw:", "sv:" })
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[prefix.Length..].TrimStart();
                    stripped = true;
                }
            if (!stripped)
                break;
        }
        return value.Length == 0 || !ChannelText.IsClean(value) ? null : value.Length > InboundChannelMessage.MaximumSubjectLength ? value[..InboundChannelMessage.MaximumSubjectLength] : value;
    }

    private static string VerificationSource(ChannelKind kind)
    {
        var value = kind.Value.Replace('.', '-');
        return value.Length > IdentityVerification.MaximumSourceLength ? value[..IdentityVerification.MaximumSourceLength] : value;
    }

    private static InboundRejected Rejected(ConversationRefused refused) => refused.Reason switch
    {
        ConversationRefusal.Forbidden => InboundRejected.Of(InboundRejection.Forbidden),
        ConversationRefusal.Invalid => new InboundRejected(InboundRejection.Invalid, refused.Errors),
        ConversationRefusal.Closed => InboundRejected.Of(InboundRejection.Invalid, "The conversation is closed."),
        ConversationRefusal.Rejected => new InboundRejected(InboundRejection.Rejected, refused.Errors),
        _ => new InboundRejected(InboundRejection.Rejected, refused.Errors),
    };
}
