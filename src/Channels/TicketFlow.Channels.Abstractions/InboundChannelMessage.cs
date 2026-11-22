using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>
/// The sender's address as supplied by the channel: identity kind, raw value (normalized by the pipeline through
/// <see cref="ContactIdentityAddress.Normalize"/>), optional namespace and whether the provider authenticated it
/// (<see cref="Verified"/>). Correlation evidence only, never authentication. <see cref="ToString"/> redacts the value.
/// </summary>
public sealed record ChannelSenderEvidence
{
    public const int MaximumValueLength = 1024;

    public ChannelSenderEvidence(ContactIdentityKind kind, string value, bool verified, string? @namespace = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Value = ChannelText.RequireText(value, MaximumValueLength, nameof(value));
        Verified = verified;
        Namespace = @namespace is null ? null : ChannelText.RequireToken(@namespace, ContactIdentityAddress.MaximumNamespaceLength, nameof(@namespace));
    }

    public ContactIdentityKind Kind { get; }
    public string Value { get; }
    public bool Verified { get; }
    public string? Namespace { get; }

    public override string ToString() => $"ChannelSenderEvidence {{ Kind = {Kind}, Verified = {Verified}, <redacted> }}";
}

/// <summary>
/// One normalized incoming channel message. <see cref="Scope"/> is trusted routing data (resolved by the host or adapter from
/// configuration, never from provider callback fields); <see cref="Channel"/> and <see cref="ConfigurationId"/> name the inbox's
/// <see cref="ChannelBinding"/>. <see cref="ExternalMessageId"/> is the provider message id (the inbound idempotency key per inbox).
/// <see cref="ExternalThreadId"/> is the channel's thread key (an SMS number pair, a WhatsApp number, a Slack thread root);
/// <see cref="InReplyTo"/> and <see cref="References"/> carry email-style reference chains. Bodies and addresses are customer
/// content: never logged.
/// </summary>
public sealed record InboundChannelMessage
{
    public const int MaximumReferences = 32;
    public const int MaximumSubjectLength = 998;
    public const int MaximumDisplayNameLength = 256;

    public InboundChannelMessage(
        TicketScope scope,
        ChannelKind channel,
        string configurationId,
        string externalMessageId,
        ChannelSenderEvidence sender,
        DateTimeOffset receivedAt,
        string? bodyText = null,
        string? bodyHtml = null,
        IEnumerable<AttachmentRef>? attachments = null,
        string? externalThreadId = null,
        string? inReplyTo = null,
        IEnumerable<string>? references = null,
        string? subject = null,
        string? senderDisplayName = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (!channel.IsInitialized)
            throw new ArgumentException("A channel kind is required.", nameof(channel));
        Channel = channel;
        ConfigurationId = TicketScope.RequireIdentifier(configurationId, nameof(configurationId));
        ExternalMessageId = ChannelText.RequireText(externalMessageId, ChannelText.MaximumExternalIdLength, nameof(externalMessageId));
        Sender = sender ?? throw new ArgumentNullException(nameof(sender));
        ReceivedAt = receivedAt.ToUniversalTime();
        if (bodyText is not null && (bodyText.Length > ConversationMessageLimits.MaximumBodyBytes * 4 || !System.Text.Unicode.Utf16.IsValid(bodyText)))
            throw new ArgumentException("Text bodies must be well-formed Unicode within bounds.", nameof(bodyText));
        if (bodyHtml is not null && (bodyHtml.Length > ConversationMessageLimits.MaximumBodyBytes * 4 || !System.Text.Unicode.Utf16.IsValid(bodyHtml)))
            throw new ArgumentException("HTML bodies must be well-formed Unicode within bounds.", nameof(bodyHtml));
        BodyText = bodyText;
        BodyHtml = bodyHtml;
        Attachments = [.. attachments ?? []];
        if (Attachments.Count > ConversationMessageLimits.MaximumAttachments || Attachments.Any(attachment => attachment is null))
            throw new ArgumentException($"At most {ConversationMessageLimits.MaximumAttachments} attachments are allowed.", nameof(attachments));
        if (string.IsNullOrWhiteSpace(bodyText) && string.IsNullOrWhiteSpace(bodyHtml) && Attachments.Count == 0)
            throw new ArgumentException("A message needs a body or at least one attachment.", nameof(bodyText));
        ExternalThreadId = ChannelText.OptionalText(externalThreadId, ChannelThreadRef.MaximumThreadIdLength, nameof(externalThreadId));
        InReplyTo = ChannelText.OptionalText(inReplyTo, ChannelText.MaximumExternalIdLength, nameof(inReplyTo));
        References = [.. (references ?? []).Select(reference => ChannelText.RequireText(reference, ChannelText.MaximumExternalIdLength, nameof(references)))];
        if (References.Count > MaximumReferences)
            throw new ArgumentException($"At most {MaximumReferences} references are allowed.", nameof(references));
        Subject = subject is null ? null : ChannelText.RequireText(subject, MaximumSubjectLength, nameof(subject));
        SenderDisplayName = senderDisplayName is null ? null : ChannelText.RequireText(senderDisplayName, MaximumDisplayNameLength, nameof(senderDisplayName));
    }

    public TicketScope Scope { get; }
    public ChannelKind Channel { get; }
    public string ConfigurationId { get; }
    public string ExternalMessageId { get; }
    public ChannelSenderEvidence Sender { get; }
    public DateTimeOffset ReceivedAt { get; }
    public string? BodyText { get; }
    public string? BodyHtml { get; }
    public IReadOnlyList<AttachmentRef> Attachments { get; }
    public string? ExternalThreadId { get; }
    public string? InReplyTo { get; }

    /// <summary>Referenced message ids, oldest first (email <c>References</c>).</summary>
    public IReadOnlyList<string> References { get; }

    public string? Subject { get; }
    public string? SenderDisplayName { get; }

    public override string ToString() => $"InboundChannelMessage {{ Channel = {Channel}, <redacted> }}";
}
