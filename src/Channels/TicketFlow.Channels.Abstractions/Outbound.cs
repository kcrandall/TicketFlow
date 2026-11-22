using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>The normalized address a channel delivers to. Personal data: <see cref="ToString"/> redacts it; never log it.</summary>
public sealed record ChannelRecipient
{
    public ChannelRecipient(ContactIdentityKind kind, string value, string? @namespace = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Value = ChannelText.RequireText(value, ContactIdentityAddress.MaximumValueLength, nameof(value));
        Namespace = string.IsNullOrEmpty(@namespace) ? null : ChannelText.RequireToken(@namespace, ContactIdentityAddress.MaximumNamespaceLength, nameof(@namespace));
    }

    public ContactIdentityKind Kind { get; }
    public string Value { get; }
    public string? Namespace { get; }

    public static ChannelRecipient From(ContactIdentityAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new(address.Kind, address.Value, address.Namespace);
    }

    public override string ToString() => $"ChannelRecipient {{ Kind = {Kind}, <redacted> }}";
}

/// <summary>
/// Threading context for a reply: the channel thread key, the message being answered (<see cref="InReplyTo"/>), the reference chain
/// oldest first (<see cref="References"/>, at most 32) and the thread subject. Correlation data only.
/// </summary>
public sealed record ChannelThreadContext
{
    public ChannelThreadContext(string? externalThreadId = null, string? inReplyTo = null, IEnumerable<string>? references = null, string? subject = null)
    {
        ExternalThreadId = ChannelText.OptionalText(externalThreadId, ChannelThreadRef.MaximumThreadIdLength, nameof(externalThreadId));
        InReplyTo = ChannelText.OptionalText(inReplyTo, ChannelText.MaximumExternalIdLength, nameof(inReplyTo));
        References = [.. (references ?? []).Select(reference => ChannelText.RequireText(reference, ChannelText.MaximumExternalIdLength, nameof(references)))];
        if (References.Count > InboundChannelMessage.MaximumReferences)
            throw new ArgumentException($"At most {InboundChannelMessage.MaximumReferences} references are allowed.", nameof(references));
        Subject = ChannelText.OptionalText(subject, InboundChannelMessage.MaximumSubjectLength, nameof(subject));
    }

    public static ChannelThreadContext None { get; } = new();

    public string? ExternalThreadId { get; }
    public string? InReplyTo { get; }
    public IReadOnlyList<string> References { get; }
    public string? Subject { get; }

    public override string ToString() => "ChannelThreadContext { <redacted> }";
}

/// <summary>A pre-approved provider template (for example a WhatsApp template) with positional body parameters.</summary>
public sealed record ChannelTemplate
{
    public const int MaximumParameters = 20;

    public ChannelTemplate(string name, string locale, IEnumerable<string>? parameters = null)
    {
        Name = ChannelText.RequireText(name, 256, nameof(name));
        Locale = ChannelText.RequireText(locale, 35, nameof(locale));
        Parameters = [.. (parameters ?? []).Select(parameter => ChannelText.RequireText(parameter, 1024, nameof(parameters)))];
        if (Parameters.Count > MaximumParameters)
            throw new ArgumentException($"At most {MaximumParameters} parameters are allowed.", nameof(parameters));
    }

    public string Name { get; }
    public string Locale { get; }
    public IReadOnlyList<string> Parameters { get; }

    public override string ToString() => $"ChannelTemplate {{ Name = {Name} }}";
}

/// <summary>
/// One send request. <see cref="Message"/> is the conversation message being dispatched (<see langword="null"/> for notifications
/// such as survey invitations). <see cref="IdempotencyKey"/> is stable for one immutable submission (printable ASCII, ≤ 200) and may
/// be forwarded to providers that deduplicate. <see cref="Template"/>, when set, asks template-capable channels to send it instead
/// of free text (<see cref="Body"/> remains the fallback). Bodies and recipients are customer data: never logged.
/// </summary>
public sealed record OutboundChannelMessage
{
    public const int MaximumIdempotencyKeyLength = 200;

    public OutboundChannelMessage(
        TicketScope scope,
        InboxId inbox,
        ChannelBinding binding,
        ConversationId conversation,
        MessageId? message,
        ContactId contact,
        ChannelRecipient recipient,
        string body,
        MessageContentType contentType,
        string idempotencyKey,
        IEnumerable<AttachmentRef>? attachments = null,
        ChannelThreadContext? thread = null,
        DateTimeOffset? lastIncomingAt = null,
        ChannelTemplate? template = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (inbox == default || conversation == default || contact == default || message == default(MessageId))
            throw new ArgumentException("Inbox, conversation, contact and (when supplied) message identifiers are required.");
        Inbox = inbox;
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Conversation = conversation;
        Message = message;
        Contact = contact;
        Recipient = recipient ?? throw new ArgumentNullException(nameof(recipient));
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length > ConversationMessageLimits.MaximumBodyBytes || !System.Text.Unicode.Utf16.IsValid(body))
            throw new ArgumentException("Bodies must be well-formed Unicode within message limits.", nameof(body));
        Body = body;
        if (!Enum.IsDefined(contentType))
            throw new ArgumentOutOfRangeException(nameof(contentType));
        ContentType = contentType;
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        if (idempotencyKey.Length is 0 or > MaximumIdempotencyKeyLength || idempotencyKey.Any(character => character is < '!' or > '~'))
            throw new ArgumentException($"Idempotency keys are 1-{MaximumIdempotencyKeyLength} printable ASCII characters.", nameof(idempotencyKey));
        IdempotencyKey = idempotencyKey;
        Attachments = [.. attachments ?? []];
        if (Attachments.Count > ConversationMessageLimits.MaximumAttachments || Attachments.Any(attachment => attachment is null))
            throw new ArgumentException($"At most {ConversationMessageLimits.MaximumAttachments} attachments are allowed.", nameof(attachments));
        Thread = thread ?? ChannelThreadContext.None;
        LastIncomingAt = lastIncomingAt?.ToUniversalTime();
        Template = template;
    }

    public TicketScope Scope { get; }
    public InboxId Inbox { get; }
    public ChannelBinding Binding { get; }
    public ConversationId Conversation { get; }
    public MessageId? Message { get; }
    public ContactId Contact { get; }
    public ChannelRecipient Recipient { get; }
    public string Body { get; }
    public MessageContentType ContentType { get; }
    public string IdempotencyKey { get; }
    public IReadOnlyList<AttachmentRef> Attachments { get; }
    public ChannelThreadContext Thread { get; }

    /// <summary>The contact's last message time as tracked by the channel layer (for reply windows the provider enforces).</summary>
    public DateTimeOffset? LastIncomingAt { get; }

    public ChannelTemplate? Template { get; }

    public override string ToString() => $"OutboundChannelMessage {{ Channel = {Binding.Kind}, Conversation = {Conversation} }}";
}

/// <summary>
/// The provider accepted the submission. <see cref="ExternalMessageId"/> is the provider message id used to correlate delivery
/// receipts; <see cref="ThreadReference"/> is the reference replies will cite when it differs or is known only to the channel (for
/// example the RFC 5322 Message-Id of an email).
/// </summary>
public sealed record ChannelSendAccepted
{
    public ChannelSendAccepted(string? externalMessageId = null, string? threadReference = null)
    {
        ExternalMessageId = ChannelText.OptionalText(externalMessageId, ChannelText.MaximumExternalIdLength, nameof(externalMessageId));
        ThreadReference = ChannelText.OptionalText(threadReference, ChannelText.MaximumExternalIdLength, nameof(threadReference));
    }

    public string? ExternalMessageId { get; }
    public string? ThreadReference { get; }
}

/// <summary>
/// The provider or channel refused the submission without sending it. <see cref="Code"/> is a failure code token;
/// <see cref="Retryable"/> rejections are retried with backoff, others fail the message.
/// </summary>
public sealed record ChannelSendRejected
{
    public ChannelSendRejected(string code, bool retryable, TimeSpan? retryAfter = null)
    {
        Code = ChannelText.RequireToken(code, MessageDelivery.MaximumFailureCodeLength, nameof(code));
        Retryable = retryable;
        if (retryAfter is { } delay && (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1)))
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        RetryAfter = retryAfter;
    }

    public string Code { get; }
    public bool Retryable { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The outcome is uncertain (for example a timeout after the request was written): the message may have been sent. Never retried automatically.</summary>
public sealed record ChannelSendUnknown
{
    public ChannelSendUnknown(string code = "outcome-unknown") => Code = ChannelText.RequireToken(code, MessageDelivery.MaximumFailureCodeLength, nameof(code));

    public string Code { get; }
}

/// <summary>Outcome of one channel submission. Switch over every case without a discard arm.</summary>
public union ChannelSendResult(ChannelSendAccepted, ChannelSendRejected, ChannelSendUnknown);

/// <summary>
/// A delivery channel. Implementations must not throw for submission outcomes: an exception means nothing was submitted and the
/// dispatcher may retry. They must never log bodies, addresses or credentials.
/// </summary>
public interface ITicketChannel
{
    /// <summary>The channel kind this implementation serves (the inbox <see cref="ChannelBinding.Kind"/>).</summary>
    ChannelKind Kind { get; }

    ChannelCapabilities Capabilities { get; }

    /// <summary>Identity kinds the channel delivers to, in preference order (used when no recipient was recorded from inbound traffic).</summary>
    IReadOnlyList<ContactIdentityKind> RecipientKinds { get; }

    Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Registered channels keyed by <see cref="ChannelKind"/> (one implementation per kind).</summary>
public interface IChannelRegistry
{
    IReadOnlyCollection<ITicketChannel> Channels { get; }

    ITicketChannel? Find(ChannelKind kind);
}
