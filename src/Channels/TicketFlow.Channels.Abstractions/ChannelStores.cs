using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>
/// Binds a channel configuration (the inbox <see cref="ChannelBinding"/>: <see cref="Kind"/> + <see cref="ConfigurationId"/>) to an
/// inbox. <see cref="Address"/> is an optional channel-defined lookup key (for example <c>twilio:AC123</c> or
/// <c>slack:T123</c>) that adapters use to find the endpoint from provider routing data; it is unique per
/// <see cref="AccessPartition"/> (tenant, project, environment, app) and kind. Addresses can contain phone numbers: never logged.
/// </summary>
public sealed record ChannelEndpoint
{
    public const int MaximumAddressLength = 512;

    public ChannelEndpoint(ChannelKind kind, string configurationId, InboxId inbox, string? address = null)
    {
        if (!kind.IsInitialized)
            throw new ArgumentException("A channel kind is required.", nameof(kind));
        if (inbox == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(inbox));
        Kind = kind;
        ConfigurationId = TicketScope.RequireIdentifier(configurationId, nameof(configurationId));
        Inbox = inbox;
        Address = ChannelText.OptionalText(address, MaximumAddressLength, nameof(address));
    }

    public ChannelKind Kind { get; }
    public string ConfigurationId { get; }
    public InboxId Inbox { get; }
    public string? Address { get; }

    public ChannelBinding Binding => new(Kind, ConfigurationId);

    public override string ToString() => $"ChannelEndpoint {{ Kind = {Kind}, Inbox = {Inbox} }}";
}

public sealed record StoredChannelEndpoint(TicketScope Scope, ChannelEndpoint Endpoint, long Revision);

/// <summary>
/// Per-conversation channel state kept by the channel layer: the recipient (the latest inbound sender address), the channel thread
/// key, the thread subject and the newest provider receive time and message id of incoming traffic (reply windows and
/// <c>In-Reply-To</c>). Correlation data: never logged or returned through agent views.
/// </summary>
public sealed record ChannelConversationState
{
    public ChannelConversationState(ConversationId conversation, InboxId inbox, ChannelKind channel, ChannelRecipient recipient, string? externalThreadId = null,
        string? subject = null, DateTimeOffset? lastIncomingAt = null, string? lastIncomingExternalId = null, ContactId? contact = null)
    {
        if (conversation == default || inbox == default || contact == default(ContactId))
            throw new ArgumentException("Conversation and inbox identifiers are required; the contact must be initialized when supplied.");
        Contact = contact;
        if (!channel.IsInitialized)
            throw new ArgumentException("A channel kind is required.", nameof(channel));
        Conversation = conversation;
        Inbox = inbox;
        Channel = channel;
        Recipient = recipient ?? throw new ArgumentNullException(nameof(recipient));
        ExternalThreadId = ChannelText.OptionalText(externalThreadId, ChannelThreadRef.MaximumThreadIdLength, nameof(externalThreadId));
        Subject = ChannelText.OptionalText(subject, InboundChannelMessage.MaximumSubjectLength, nameof(subject));
        LastIncomingAt = lastIncomingAt is { } at ? Microseconds(at) : null;
        LastIncomingExternalId = ChannelText.OptionalText(lastIncomingExternalId, ChannelText.MaximumExternalIdLength, nameof(lastIncomingExternalId));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public ChannelKind Channel { get; }
    public ChannelRecipient Recipient { get; }
    public string? ExternalThreadId { get; }
    public string? Subject { get; }
    public DateTimeOffset? LastIncomingAt { get; }
    public string? LastIncomingExternalId { get; }

    /// <summary>The contact whose traffic this state tracks (thread lookups are per contact, so a shared channel thread keeps one conversation per speaker).</summary>
    public ContactId? Contact { get; }

    /// <summary>UTC with microsecond precision (PostgreSQL <c>timestamptz</c>).</summary>
    public static DateTimeOffset Microseconds(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public override string ToString() => $"ChannelConversationState {{ Conversation = {Conversation}, Channel = {Channel} }}";
}

public sealed record StoredChannelConversationState(TicketScope Scope, ChannelConversationState State, long Revision);

/// <summary>A provider message reference (for example an email Message-Id) seen in a conversation, inbound or outbound.</summary>
public sealed record ChannelMessageReference
{
    public ChannelMessageReference(string reference, MessageDirection direction, MessageId? message = null)
    {
        Reference = ChannelText.RequireText(reference, ChannelText.MaximumExternalIdLength, nameof(reference));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (message == default(MessageId))
            throw new ArgumentException("The message must be initialized when supplied.", nameof(message));
        Direction = direction;
        Message = message;
    }

    public string Reference { get; }
    public MessageDirection Direction { get; }
    public MessageId? Message { get; }

    public override string ToString() => $"ChannelMessageReference {{ Direction = {Direction} }}";
}

/// <summary>A stored reference with its per-store increasing <see cref="Sequence"/> (insertion order).</summary>
public sealed record StoredChannelMessageReference(ConversationId Conversation, InboxId Inbox, ChannelMessageReference Reference, long Sequence, DateTimeOffset RecordedAt);

/// <summary>Persisted dispatch states. Never renumber.</summary>
public enum ChannelDispatchState
{
    /// <summary>A send attempt started; finding it again means the previous attempt's outcome is unknown.</summary>
    Sending = 1,

    /// <summary>A retryable rejection; the next attempt is due at <see cref="ChannelDispatch.NextAttemptAt"/>.</summary>
    RetryScheduled = 2,

    Accepted = 3,

    /// <summary>Rejected permanently or after the last allowed attempt (dead-lettered).</summary>
    Rejected = 4,

    /// <summary>The outcome is unknown; never retried automatically.</summary>
    Unknown = 5,

    /// <summary>Not sent by channel rules (reply window closed, no recipient, unsupported content).</summary>
    Refused = 6,
}

/// <summary>Dispatch progress of one outgoing message. <see cref="Code"/> is the last failure code token.</summary>
public sealed record ChannelDispatch
{
    public ChannelDispatch(ConversationId conversation, MessageId message, InboxId inbox, ChannelKind channel, ChannelDispatchState state, int attempts,
        DateTimeOffset updatedAt, DateTimeOffset? nextAttemptAt = null, string? externalMessageId = null, string? code = null)
    {
        if (conversation == default || message == default || inbox == default)
            throw new ArgumentException("Conversation, message and inbox identifiers are required.");
        if (!channel.IsInitialized)
            throw new ArgumentException("A channel kind is required.", nameof(channel));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attempts, 1000);
        if ((state == ChannelDispatchState.RetryScheduled) != nextAttemptAt.HasValue)
            throw new ArgumentException("Exactly retry-scheduled dispatches carry a next attempt time.", nameof(nextAttemptAt));
        Conversation = conversation;
        Message = message;
        Inbox = inbox;
        Channel = channel;
        State = state;
        Attempts = attempts;
        UpdatedAt = ChannelConversationState.Microseconds(updatedAt);
        NextAttemptAt = nextAttemptAt is { } next ? ChannelConversationState.Microseconds(next) : null;
        ExternalMessageId = ChannelText.OptionalText(externalMessageId, ChannelText.MaximumExternalIdLength, nameof(externalMessageId));
        Code = code is null ? null : ChannelText.RequireToken(code, MessageDelivery.MaximumFailureCodeLength, nameof(code));
    }

    public ConversationId Conversation { get; }
    public MessageId Message { get; }
    public InboxId Inbox { get; }
    public ChannelKind Channel { get; }
    public ChannelDispatchState State { get; }
    public int Attempts { get; }
    public DateTimeOffset UpdatedAt { get; }
    public DateTimeOffset? NextAttemptAt { get; }
    public string? ExternalMessageId { get; }
    public string? Code { get; }

    public bool IsTerminal => State is ChannelDispatchState.Accepted or ChannelDispatchState.Rejected or ChannelDispatchState.Unknown or ChannelDispatchState.Refused;

    public override string ToString() => $"ChannelDispatch {{ Conversation = {Conversation}, Message = {Message}, State = {State} }}";
}

public sealed record StoredChannelDispatch(TicketScope Scope, ChannelDispatch Dispatch, long Revision);

public sealed record ChannelWriteApplied<T>(T Value);

public sealed record ChannelWriteNotFound
{
    private ChannelWriteNotFound()
    {
    }

    public static ChannelWriteNotFound Instance { get; } = new();
}

/// <summary>The revision did not match, an immutable field differed, or a create found an existing record at <see cref="CurrentRevision"/>.</summary>
public sealed record ChannelWriteConflict(long CurrentRevision);

public enum ChannelWriteRejection
{
    /// <summary>The endpoint address is already bound in the partition for the kind.</summary>
    AddressTaken = 1,

    CapacityExceeded = 2,
}

public sealed record ChannelWriteRejected(ChannelWriteRejection Reason);

/// <summary>Outcome of a channel store write. Switch over every case without a discard arm.</summary>
public union ChannelWriteResult<T>(ChannelWriteApplied<T>, ChannelWriteNotFound, ChannelWriteConflict, ChannelWriteRejected);

/// <summary>
/// Channel endpoints keyed by (scope, kind, configuration id). Contract: scope-confined reads and writes; creates start at revision 1
/// and conflict when present; updates and deletes compare-and-set on revision; <see cref="ChannelEndpoint.Address"/> is unique per
/// (partition, kind) across workspaces (<see cref="ChannelWriteRejection.AddressTaken"/>) and checked atomically; at most
/// <see cref="MaximumEndpointsPerScope"/> endpoints per scope. <see cref="FindByAddressAsync"/> is the only partition-wide read and
/// returns the endpoint with its full scope.
/// </summary>
public interface IChannelEndpointStore
{
    public const int MaximumPrefixMatches = 100;

    public const int MaximumEndpointsPerScope = 1000;

    Task<StoredChannelEndpoint?> GetAsync(TicketScope scope, ChannelKind kind, string configurationId, CancellationToken cancellationToken = default);

    Task<StoredChannelEndpoint?> FindByAddressAsync(AccessPartition partition, ChannelKind kind, string address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Partition-wide: endpoints of <paramref name="kind"/> whose address starts with <paramref name="prefix"/> (ordinal), at most
    /// <paramref name="limit"/> (1-100), ordered by address. Used to detect addresses nested under an account owned elsewhere.
    /// </summary>
    Task<IReadOnlyList<StoredChannelEndpoint>> ListByAddressPrefixAsync(AccessPartition partition, ChannelKind kind, string prefix, int limit, CancellationToken cancellationToken = default);

    Task<ChannelWriteResult<StoredChannelEndpoint>> CreateAsync(TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken = default);

    Task<ChannelWriteResult<StoredChannelEndpoint>> UpdateAsync(TicketScope scope, ChannelEndpoint endpoint, long expectedRevision, CancellationToken cancellationToken = default);

    Task<ChannelWriteResult<StoredChannelEndpoint>> DeleteAsync(TicketScope scope, ChannelKind kind, string configurationId, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-conversation channel state. Contract: scope-confined; <see cref="WriteAsync"/> creates (expected revision
/// <see langword="null"/>, conflict when present) at revision 1 or compare-and-sets (not found when absent); the conversation, inbox
/// and channel of a stored state never change (a write that changes them is a conflict). <see cref="FindByThreadAsync"/> returns the
/// conversations whose state has exactly this inbox, channel, thread id and contact.
/// </summary>
public interface IChannelConversationStateStore
{
    public const int MaximumThreadMatches = 16;

    /// <summary>At most <see cref="MaximumThreadMatches"/> conversations of <paramref name="contact"/> on this channel thread, in identifier order (descending).</summary>
    Task<IReadOnlyList<ConversationId>> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelKind channel, string externalThreadId, ContactId contact,
        CancellationToken cancellationToken = default);

    Task<StoredChannelConversationState?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<ChannelWriteResult<StoredChannelConversationState>> WriteAsync(TicketScope scope, ChannelConversationState state, long? expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Removes the state (for example after the conversation was deleted); <see langword="false"/> when absent.</summary>
    Task<bool> DeleteAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Message reference chains for threading. Contract: scope-confined; a reference is unique per (scope, inbox) — adding one that
/// already exists (for any conversation) is a no-op, so concurrent adds are idempotent; each conversation keeps at most
/// <see cref="MaximumReferencesPerConversation"/> references (the oldest are pruned atomically with the insert); sequences increase
/// in insertion order.
/// </summary>
public interface IChannelThreadStore
{
    public const int MaximumReferencesPerConversation = 128;
    public const int MaximumReferencesPerCall = 64;

    /// <summary>Adds references (≤ <see cref="MaximumReferencesPerCall"/>) in order; returns the number newly stored.</summary>
    Task<int> AddAsync(TicketScope scope, InboxId inbox, ConversationId conversation, IReadOnlyList<ChannelMessageReference> references, DateTimeOffset recordedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// The conversation holding the first of <paramref name="references"/> (≤ <see cref="MaximumReferencesPerCall"/>, in priority
    /// order) that is stored in the inbox, or <see langword="null"/>.
    /// </summary>
    Task<ConversationId?> FindConversationAsync(TicketScope scope, InboxId inbox, IReadOnlyList<string> references, CancellationToken cancellationToken = default);

    /// <summary>The conversation's references, oldest first.</summary>
    Task<IReadOnlyList<StoredChannelMessageReference>> ListAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatch records keyed by (scope, conversation, message). Contract: scope-confined; <see cref="WriteAsync"/> creates at revision 1
/// (conflict when present) or compare-and-sets (not found when absent); the inbox and channel of a record never change.
/// </summary>
public interface IChannelDispatchStore
{
    Task<StoredChannelDispatch?> GetAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default);

    Task<ChannelWriteResult<StoredChannelDispatch>> WriteAsync(TicketScope scope, ChannelDispatch dispatch, long? expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides whether a scope may bind a provider address (an endpoint <see cref="ChannelEndpoint.Address"/>). Addresses route provider
/// traffic for a whole partition, so binding one is a claim over a provider account, not a workspace setting. Applications should
/// supply a validator that checks provider-account ownership; the default (<c>CrossScopeAddressOwnershipValidator</c>) denies any
/// address that nests under, or encloses, an address bound by another scope.
/// </summary>
public interface IChannelAddressOwnershipValidator
{
    /// <summary>True when <paramref name="scope"/> may bind <paramref name="endpoint"/>'s address (which is non-null).</summary>
    Task<bool> MayBindAsync(PrincipalRef actor, TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the grant-holding principal channel workers act as in a scope (an API client or user; never automation or system).</summary>
public interface IChannelWorkerPrincipalResolver
{
    /// <summary>Throws <see cref="InvalidOperationException"/> when no worker is configured for the scope.</summary>
    PrincipalRef Resolve(TicketScope scope);
}
