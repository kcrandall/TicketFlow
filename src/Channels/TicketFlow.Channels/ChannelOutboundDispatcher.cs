using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>A retryable channel rejection; the outbox dispatcher releases the entry for a backoff retry.</summary>
public sealed class ChannelDispatchRetryException(string code) : Exception("The channel asked to retry the submission later.")
{
    public string Code { get; } = code;
}

/// <summary>
/// Dispatches outgoing replies from the conversation outbox (consumer <see cref="ChannelsOptions.DispatchConsumer"/>). For every
/// outgoing message of a change it keeps a <see cref="ChannelDispatch"/> record: <c>Sending</c> is written before the channel is
/// called, so a redelivered entry that still finds <c>Sending</c> treats the earlier attempt as an unknown outcome (failed with
/// <c>outcome-unknown</c>, never resent). Accepted submissions move the message to Sent with the provider id and record the email
/// thread reference; retryable rejections throw <see cref="ChannelDispatchRetryException"/> so the outbox dispatcher backs off
/// (its exponential schedule is the retry schedule) until <see cref="ChannelsOptions.MaximumSendAttempts"/>, then the message
/// fails (dead-letter); permanent rejections, unknown outcomes and channel-rule refusals (reply window closed, no recipient) fail
/// it immediately. Delivery updates run as <see cref="ChannelsOptions.WorkerPrincipal"/>. Bodies are read through trusted store
/// access and never logged. Messages of inboxes whose kind has no registered channel are left to other dispatchers.
/// </summary>
public sealed class ChannelOutboundDispatcher : IConversationOutboxHandler
{
    private readonly IConversationStore conversationStore;
    private readonly IConversationService conversations;
    private readonly IInboxStore inboxes;
    private readonly IContactStore contacts;
    private readonly IChannelRegistry registry;
    private readonly IChannelConversationStateStore states;
    private readonly IChannelThreadStore threads;
    private readonly IChannelDispatchStore dispatches;
    private readonly ChannelsOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public ChannelOutboundDispatcher(
        IConversationStore conversationStore,
        IConversationService conversations,
        IInboxStore inboxes,
        IContactStore contacts,
        IChannelRegistry registry,
        IChannelConversationStateStore states,
        IChannelThreadStore threads,
        IChannelDispatchStore dispatches,
        IOptions<ChannelsOptions> options,
        IOptions<ConversationOutboxDispatcherOptions> outboxOptions,
        TimeProvider time,
        ILogger<ChannelOutboundDispatcher>? logger = null)
    {
        this.conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.states = states ?? throw new ArgumentNullException(nameof(states));
        this.threads = threads ?? throw new ArgumentNullException(nameof(threads));
        this.dispatches = dispatches ?? throw new ArgumentNullException(nameof(dispatches));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        ArgumentNullException.ThrowIfNull(outboxOptions);
        if (this.options.MaximumSendAttempts >= outboxOptions.Value.MaximumAttempts)
            throw new InvalidOperationException("ChannelsOptions.MaximumSendAttempts must be lower than the outbox dispatcher's MaximumAttempts.");
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.logger = logger ?? NullLogger<ChannelOutboundDispatcher>.Instance;
    }

    public string Consumer => ChannelsOptions.DispatchConsumer;

    public async Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var change = entry.Change;
        if (change.Kind is not (ConversationChangeKind.Started or ConversationChangeKind.EventsAppended))
            return;
        foreach (var message in change.OutgoingMessages)
            await DispatchAsync(change.Scope, change.Conversation, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchAsync(TicketScope scope, ConversationId conversationId, MessageId messageId, CancellationToken cancellationToken)
    {
        var row = await conversationStore.GetMessageAsync(scope, conversationId, messageId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.Direction != MessageDirection.Outgoing)
            return;
        var stored = await conversationStore.GetAsync(scope, conversationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return;
        var inbox = await inboxes.GetAsync(scope, stored.Conversation.Inbox, cancellationToken).ConfigureAwait(false);
        if (inbox is null)
            return;
        var binding = inbox.Inbox.Channel;
        var channel = registry.Find(binding.Kind);
        if (channel is null)
            return;

        var existing = await dispatches.GetAsync(scope, conversationId, messageId, cancellationToken).ConfigureAwait(false);
        if (existing is { Dispatch.IsTerminal: true })
        {
            await ApplyAsync(scope, existing.Dispatch, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (existing is { Dispatch.State: ChannelDispatchState.Sending })
        {
            logger.LogWarning("A previous send attempt for message {Message} in conversation {Conversation} ended without an outcome; it is not retried.", messageId, conversationId);
            await FinishAsync(scope, existing, existing.Dispatch.Attempts, ChannelDispatchState.Unknown, null, "outcome-unknown", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (row.Delivery.State != MessageDeliveryState.Pending)
            return;

        var now = time.GetUtcNow();
        var attempts = existing?.Dispatch.Attempts ?? 0;
        var events = await conversationStore.ReadTimelineAsync(scope, conversationId, row.Sequence - 1, 1, includeNotes: false, cancellationToken).ConfigureAwait(false);
        if (events.Count != 1 || events[0].Event.Value is not MessageEvent { Direction: MessageDirection.Outgoing } outgoing || outgoing.Id != messageId)
            return;

        var state = await states.GetAsync(scope, conversationId, cancellationToken).ConfigureAwait(false);
        var recipient = await RecipientAsync(scope, stored.Conversation, state?.State, channel, cancellationToken).ConfigureAwait(false);
        string? refusal = null;
        if (recipient is null)
            refusal = "no-recipient";
        else if (ChannelReplyWindow.Evaluate(channel.Capabilities, state?.State.LastIncomingAt ?? stored.Conversation.LastIncomingAt, now) is { Value: ReplyWindowClosed })
            refusal = ReplyWindowClosed.FailureCode;
        else if (outgoing.Attachments.Count > channel.Capabilities.MaximumAttachments)
            refusal = "attachments-unsupported";
        if (refusal is not null)
        {
            var refused = new ChannelDispatch(conversationId, messageId, stored.Conversation.Inbox, binding.Kind, ChannelDispatchState.Refused, attempts, now, code: refusal);
            await WriteAndApplyAsync(scope, refused, existing?.Revision, cancellationToken).ConfigureAwait(false);
            return;
        }

        var references = await threads.ListAsync(scope, conversationId, cancellationToken).ConfigureAwait(false);
        var chain = references.Select(reference => reference.Reference.Reference).ToList();
        if (chain.Count > options.MaximumReplyReferences)
            chain = [chain[0], .. chain[^(options.MaximumReplyReferences - 1)..]];
        var inReplyTo = state?.State.LastIncomingExternalId ?? (chain.Count > 0 ? chain[^1] : null);
        var request = new OutboundChannelMessage(scope, stored.Conversation.Inbox, binding, conversationId, messageId, stored.Conversation.Contact, recipient!,
            outgoing.Body, outgoing.ContentType, ChannelAuthorization.IdempotencyKey(scope, binding.Kind.Value, conversationId.Value, messageId.Value),
            outgoing.Attachments, new ChannelThreadContext(state?.State.ExternalThreadId ?? stored.Conversation.ChannelThread?.ExternalThreadId, inReplyTo, chain, state?.State.Subject),
            state?.State.LastIncomingAt ?? stored.Conversation.LastIncomingAt);

        attempts++;
        var sending = new ChannelDispatch(conversationId, messageId, stored.Conversation.Inbox, binding.Kind, ChannelDispatchState.Sending, attempts, now,
            code: existing?.Dispatch.Code);
        var claimed = await dispatches.WriteAsync(scope, sending, existing?.Revision, cancellationToken).ConfigureAwait(false);
        if (claimed.Value is not ChannelWriteApplied<StoredChannelDispatch> { Value: var current })
            throw new InvalidOperationException("The dispatch record changed concurrently; the entry will be retried.");

        ChannelSendResult result;
        try
        {
            result = await channel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Channels throw only when nothing was submitted, so the attempt may be retried.
            result = new ChannelSendRejected("channel-error", retryable: true);
        }

        switch (result)
        {
            case ChannelSendAccepted accepted:
                if (ChannelText.NormalizeReference(accepted.ThreadReference ?? accepted.ExternalMessageId) is { } reference)
                    await threads.AddAsync(scope, stored.Conversation.Inbox, conversationId, [new ChannelMessageReference(reference, MessageDirection.Outgoing, messageId)],
                        time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                await FinishAsync(scope, current, attempts, ChannelDispatchState.Accepted, accepted.ExternalMessageId, null, cancellationToken).ConfigureAwait(false);
                break;
            case ChannelSendRejected { Retryable: true } rejected when attempts < options.MaximumSendAttempts:
                var retry = new ChannelDispatch(conversationId, messageId, stored.Conversation.Inbox, binding.Kind, ChannelDispatchState.RetryScheduled, attempts,
                    time.GetUtcNow(), time.GetUtcNow() + (rejected.RetryAfter ?? TimeSpan.Zero), code: rejected.Code);
                if (await dispatches.WriteAsync(scope, retry, current.Revision, cancellationToken).ConfigureAwait(false) is { Value: not ChannelWriteApplied<StoredChannelDispatch> })
                    throw new InvalidOperationException("The dispatch record changed concurrently; the entry will be retried.");
                throw new ChannelDispatchRetryException(rejected.Code);
            case ChannelSendRejected rejected:
                logger.LogWarning("Channel {Channel} rejected message {Message} with {Code} after {Attempts} attempt(s).", binding.Kind, messageId, rejected.Code, attempts);
                await FinishAsync(scope, current, attempts, ChannelDispatchState.Rejected, null, rejected.Code, cancellationToken).ConfigureAwait(false);
                break;
            case ChannelSendUnknown unknown:
                logger.LogWarning("Channel {Channel} returned an unknown outcome for message {Message}; it is not retried.", binding.Kind, messageId);
                await FinishAsync(scope, current, attempts, ChannelDispatchState.Unknown, null, unknown.Code, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task FinishAsync(TicketScope scope, StoredChannelDispatch current, int attempts, ChannelDispatchState state, string? externalId, string? code, CancellationToken cancellationToken)
    {
        var dispatch = new ChannelDispatch(current.Dispatch.Conversation, current.Dispatch.Message, current.Dispatch.Inbox, current.Dispatch.Channel, state, attempts,
            time.GetUtcNow(), externalMessageId: externalId, code: code);
        await WriteAndApplyAsync(scope, dispatch, current.Revision, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAndApplyAsync(TicketScope scope, ChannelDispatch dispatch, long? expectedRevision, CancellationToken cancellationToken)
    {
        if (await dispatches.WriteAsync(scope, dispatch, expectedRevision, cancellationToken).ConfigureAwait(false) is { Value: not ChannelWriteApplied<StoredChannelDispatch> })
            throw new InvalidOperationException("The dispatch record changed concurrently; the entry will be retried.");
        await ApplyAsync(scope, dispatch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Mirrors a terminal dispatch into the message delivery state (idempotent: E5 replays repeated or stale updates).</summary>
    private async Task ApplyAsync(TicketScope scope, ChannelDispatch dispatch, CancellationToken cancellationToken)
    {
        var worker = options.ResolveWorker(scope);
        var update = dispatch.State == ChannelDispatchState.Accepted
            ? DeliveryStatusUpdate.ByMessage(dispatch.Conversation, dispatch.Message, MessageDeliveryState.Sent, providerMessageId: dispatch.ExternalMessageId)
            : DeliveryStatusUpdate.ByMessage(dispatch.Conversation, dispatch.Message, MessageDeliveryState.Failed, dispatch.Code ?? "failed");
        var result = await conversations.UpdateDeliveryStatusAsync(worker, update, cancellationToken).ConfigureAwait(false);
        if (result.Value is ConversationRefused { Reason: ConversationRefusal.Invalid } && update.ProviderMessageId is not null)
            result = await conversations.UpdateDeliveryStatusAsync(worker, DeliveryStatusUpdate.ByMessage(dispatch.Conversation, dispatch.Message, MessageDeliveryState.Sent),
                cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case ConversationApplied:
            case ConversationNotFound:
                return;
            case ConversationConflict:
                throw new InvalidOperationException("The delivery update conflicted; the entry will be retried.");
            case ConversationRefused { Reason: ConversationRefusal.Forbidden }:
                throw new InvalidOperationException("The channel worker principal lacks conversation:ingest.");
            case ConversationRefused { Reason: ConversationRefusal.Rejected }:
                throw new InvalidOperationException("The delivery update was rejected; the entry will be retried.");
            case ConversationRefused:
                logger.LogWarning("The delivery update for message {Message} was refused as invalid.", dispatch.Message);
                return;
        }
    }

    private async Task<ChannelRecipient?> RecipientAsync(TicketScope scope, Conversation conversation, ChannelConversationState? state, ITicketChannel channel, CancellationToken cancellationToken)
    {
        if (state is not null && channel.RecipientKinds.Contains(state.Recipient.Kind))
            return state.Recipient;
        var contact = await contacts.GetAsync(scope, conversation.Contact, cancellationToken).ConfigureAwait(false);
        if (contact is null || contact.Contact.Blocked)
            return null;
        foreach (var kind in channel.RecipientKinds)
        {
            var identity = contact.Contact.Identities.Where(item => item.Address.Kind == kind)
                .OrderByDescending(item => item.Verification.IsVerified).FirstOrDefault();
            if (identity is not null)
                return ChannelRecipient.From(identity.Address);
        }
        return null;
    }
}
