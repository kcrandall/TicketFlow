using System.Text.RegularExpressions;
using EnvelopeFlow.Chat;
using EnvelopeFlow.Email;
using EnvelopeFlow.Sms;
using EnvelopeFlow.WhatsApp;
using Microsoft.Extensions.Options;
using TicketFlow.Channels;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.EnvelopeFlow;

/// <summary>The sending identity of an email channel configuration. Addresses are personal/business data: never logged.</summary>
public sealed record EnvelopeFlowEmailSender(string FromAddress, string? FromDisplayName = null, string? ReplyToAddress = null)
{
    public override string ToString() => "EnvelopeFlowEmailSender { <redacted> }";
}

/// <summary>The sending identity of an SMS channel configuration: a sender number or a messaging service id.</summary>
public sealed record EnvelopeFlowSmsSender(string? From = null, string? MessagingServiceId = null)
{
    public override string ToString() => "EnvelopeFlowSmsSender { <redacted> }";
}

/// <summary>Application-owned sender settings per channel configuration (credentials stay in EnvelopeFlow provider configuration).</summary>
public interface IEnvelopeFlowSenderDirectory
{
    ValueTask<EnvelopeFlowEmailSender?> GetEmailSenderAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default);

    ValueTask<EnvelopeFlowSmsSender?> GetSmsSenderAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default);
}

/// <summary>Marker for channels implemented over EnvelopeFlow transports.</summary>
public interface IEnvelopeFlowChannel : ITicketChannel;

/// <summary>
/// Email over <see cref="IEmailTransport"/>. Replies carry <c>In-Reply-To</c> (the customer's last Message-Id) and <c>References</c>
/// (the conversation's chain, root first), a <c>Re:</c> subject and a stable <see cref="EmailSubmissionContext"/>. The provider id
/// is the delivery correlation id; when it is an RFC 5322 Message-Id (SMTP, Mailgun) it is also recorded as the thread reference so
/// customer replies thread. Attachments are not sent (TicketFlow stores references only).
/// </summary>
public sealed class EnvelopeFlowEmailChannel(IEmailTransport transport, IEnvelopeFlowSenderDirectory senders, IOptions<EnvelopeFlowIntegrationOptions> options) : IEnvelopeFlowChannel
{
    private readonly IEmailTransport transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly IEnvelopeFlowSenderDirectory senders = senders ?? throw new ArgumentNullException(nameof(senders));
    private readonly EnvelopeFlowIntegrationOptions options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public ChannelKind Kind => EnvelopeFlowChannelKinds.Email;
    public ChannelCapabilities Capabilities { get; } = new(ChannelFeatures.RichContent | ChannelFeatures.Threading | ChannelFeatures.DeliveryReceipts);
    public IReadOnlyList<ContactIdentityKind> RecipientKinds { get; } = [ContactIdentityKind.Email];

    public async Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Attachments.Count > 0)
            return new ChannelSendRejected("attachments-unsupported", retryable: false);
        var sender = await senders.GetEmailSenderAsync(message.Scope, message.Binding.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (sender is null)
            return new ChannelSendRejected("sender-missing", retryable: false);
        var headers = new List<EmailHeader>();
        if (message.Thread.InReplyTo is { } inReplyTo)
            headers.Add(new EmailHeader("In-Reply-To", Bracket(inReplyTo)));
        var references = message.Thread.References.Append(message.Thread.InReplyTo).OfType<string>().Distinct(StringComparer.Ordinal).Select(Bracket).ToList();
        if (references.Count > 0)
            headers.Add(new EmailHeader("References", string.Join(' ', references)));
        EmailMessage email;
        try
        {
            email = new EmailMessage
            {
                From = new EmailAddress(sender.FromAddress, sender.FromDisplayName),
                To = [new EmailAddress(message.Recipient.Value)],
                ReplyTo = sender.ReplyToAddress is null ? [] : [new EmailAddress(sender.ReplyToAddress)],
                Subject = "Re: " + (message.Thread.Subject ?? options.DefaultEmailSubject),
                TextBody = message.ContentType == MessageContentType.Html ? null : message.Body,
                HtmlBody = message.ContentType == MessageContentType.Html ? message.Body : null,
                Headers = headers,
                SubmissionContext = new EmailSubmissionContext(message.IdempotencyKey),
            };
        }
        catch (ArgumentException)
        {
            return new ChannelSendRejected("invalid-message", retryable: false);
        }
        var result = await transport.SendAsync(email, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            EmailSubmissionStatus.Accepted => new ChannelSendAccepted(result.ProviderMessageId, result.ProviderMessageId is { } id && id.Contains('@') ? id : null),
            EmailSubmissionStatus.Rejected => new ChannelSendRejected(ChannelText.FailureCode(result.FailureCode.ToString().ToLowerInvariant()),
                result.RetryAdvice == EmailRetryAdvice.Retry, Clamp(result.RetryAfter)),
            EmailSubmissionStatus.Unknown => new ChannelSendUnknown(),
            _ => new ChannelSendUnknown(),
        };
    }

    private static string Bracket(string reference) => reference.StartsWith('<') ? reference : $"<{reference}>";

    internal static TimeSpan? Clamp(TimeSpan? delay) => delay is { } value ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, 0, TimeSpan.FromDays(1).Ticks)) : null;
}

/// <summary>SMS over <see cref="ISmsTransport"/> (no threading; replies go to the recorded sender number).</summary>
public sealed class EnvelopeFlowSmsChannel(ISmsTransport transport, IEnvelopeFlowSenderDirectory senders) : IEnvelopeFlowChannel
{
    private readonly ISmsTransport transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly IEnvelopeFlowSenderDirectory senders = senders ?? throw new ArgumentNullException(nameof(senders));

    public ChannelKind Kind => EnvelopeFlowChannelKinds.Sms;

    public ChannelCapabilities Capabilities { get; } = new((transport.Capabilities.DeliveryReceipts ? ChannelFeatures.DeliveryReceipts : ChannelFeatures.None)
        | (transport.Capabilities.ReadReceipts ? ChannelFeatures.ReadReceipts : ChannelFeatures.None), maximumBodyLength: 1600);

    public IReadOnlyList<ContactIdentityKind> RecipientKinds { get; } = [ContactIdentityKind.Phone];

    public async Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Attachments.Count > 0)
            return new ChannelSendRejected("attachments-unsupported", retryable: false);
        var sender = await senders.GetSmsSenderAsync(message.Scope, message.Binding.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (sender is null || sender.From is null && sender.MessagingServiceId is null)
            return new ChannelSendRejected("sender-missing", retryable: false);
        var result = await transport.SendAsync(new SmsMessage { To = message.Recipient.Value, From = sender.From, MessagingServiceId = sender.MessagingServiceId, Text = message.Body },
            cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            SmsSubmissionStatus.Accepted => new ChannelSendAccepted(result.ProviderMessageId),
            SmsSubmissionStatus.Rejected => new ChannelSendRejected(ChannelText.FailureCode(result.FailureCode, "rejected"), result.RetryAdvice == SmsRetryAdvice.Retry,
                EnvelopeFlowEmailChannel.Clamp(result.RetryAfter)),
            SmsSubmissionStatus.Unknown => new ChannelSendUnknown(),
            _ => new ChannelSendUnknown(),
        };
    }
}

/// <summary>
/// WhatsApp over <see cref="IWhatsAppTransport"/>. Destinations are encoded with <see cref="EnvelopeFlowDestinations"/>; the provider
/// re-resolves the binding (and enforces the 24-hour window from TicketFlow's last customer message time). A
/// <see cref="ChannelTemplate"/> is sent as an approved template; otherwise free text, optionally quoting the customer's last message.
/// </summary>
public sealed class EnvelopeFlowWhatsAppChannel(IWhatsAppTransport transport, IOptions<EnvelopeFlowIntegrationOptions> options) : IEnvelopeFlowChannel
{
    private readonly IWhatsAppTransport transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly EnvelopeFlowIntegrationOptions options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public ChannelKind Kind => EnvelopeFlowChannelKinds.WhatsApp;

    public ChannelCapabilities Capabilities { get; } = new(ChannelFeatures.ReplyWindow | ChannelFeatures.Templates | ChannelFeatures.DeliveryReceipts | ChannelFeatures.ReadReceipts,
        TimeSpan.FromHours(24), maximumBodyLength: 4096);

    public IReadOnlyList<ContactIdentityKind> RecipientKinds { get; } = [ContactIdentityKind.WhatsApp, ContactIdentityKind.Phone];

    public async Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Attachments.Count > 0)
            return new ChannelSendRejected("attachments-unsupported", retryable: false);
        if (EnvelopeFlowDestinations.Encode(message.Scope, message.Binding.ConfigurationId, message.Conversation) is not { } destinationId)
            return new ChannelSendRejected("destination-too-long", retryable: false);
        WhatsAppSendRequest request;
        try
        {
            var content = message.Template is { } template
                ? new WhatsAppMessage(Template: new WhatsAppTemplate(template.Name, template.Locale,
                    template.Parameters.Count == 0 ? [] : [new WhatsAppTextComponent(WhatsAppComponentKind.Body, template.Parameters)]))
                : new WhatsAppMessage(Text: message.Body)
                {
                    ReplyToMessageId = options.QuoteWhatsAppReplies && message.Thread.InReplyTo is { } quoted && quoted.StartsWith("wamid.", StringComparison.Ordinal) ? quoted : null,
                };
            request = new WhatsAppSendRequest(new WhatsAppDestination(EnvelopeFlowDestinations.Owner(message.Scope, message.Contact), destinationId, 1), content);
            request.Validate();
            transport.Validate(request);
        }
        catch (ArgumentException)
        {
            return new ChannelSendRejected("invalid-message", retryable: false);
        }
        var result = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            WhatsAppSubmissionStatus.Accepted => new ChannelSendAccepted(result.ProviderMessageId),
            WhatsAppSubmissionStatus.Rejected => new ChannelSendRejected(ChannelText.FailureCode(result.FailureCode, "rejected"), result.RetryAfter is not null,
                EnvelopeFlowEmailChannel.Clamp(result.RetryAfter)),
            WhatsAppSubmissionStatus.Unknown => new ChannelSendUnknown(),
            _ => new ChannelSendUnknown(),
        };
    }
}

/// <summary>
/// Slack (or Teams, unthreaded) over <see cref="IChatTransport"/>. Slack replies are threaded under the conversation's root
/// <c>ts</c> (the last segment of the channel thread key <c>{channel}:{ts}</c>); Teams Workflows cannot thread, so no thread id is
/// sent. Destinations are encoded with <see cref="EnvelopeFlowDestinations"/>.
/// </summary>
public sealed partial class EnvelopeFlowChatChannel : IEnvelopeFlowChannel
{
    private readonly IChatTransport transport;
    private readonly bool threaded;

    public EnvelopeFlowChatChannel(IChatTransport transport, ChannelKind kind, bool threaded)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (!kind.IsInitialized)
            throw new ArgumentException("A channel kind is required.", nameof(kind));
        Kind = kind;
        this.threaded = threaded;
        Capabilities = new(threaded ? ChannelFeatures.Threading : ChannelFeatures.None, maximumBodyLength: 4000);
    }

    public ChannelKind Kind { get; }
    public ChannelCapabilities Capabilities { get; }
    public IReadOnlyList<ContactIdentityKind> RecipientKinds { get; } = [ContactIdentityKind.ExternalUser];

    public async Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Attachments.Count > 0)
            return new ChannelSendRejected("attachments-unsupported", retryable: false);
        if (EnvelopeFlowDestinations.Encode(message.Scope, message.Binding.ConfigurationId, message.Conversation) is not { } destinationId)
            return new ChannelSendRejected("destination-too-long", retryable: false);
        string? threadId = null;
        if (threaded && message.Thread.ExternalThreadId is { } thread)
        {
            var ts = thread[(thread.LastIndexOf(':') + 1)..];
            threadId = SlackTs().IsMatch(ts) ? ts : null;
        }
        ChatSendRequest request;
        try
        {
            request = new ChatSendRequest(new ChatDestination(EnvelopeFlowDestinations.Owner(message.Scope, message.Contact), destinationId, 1), new ChatMessage(message.Body, threadId));
            request.Validate();
            transport.Validate(request);
        }
        catch (ArgumentException)
        {
            return new ChannelSendRejected("invalid-message", retryable: false);
        }
        var result = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ChatSubmissionStatus.Accepted => new ChannelSendAccepted(result.ProviderMessageId),
            ChatSubmissionStatus.Rejected => new ChannelSendRejected(ChannelText.FailureCode(result.FailureCode, "rejected"), result.RetryAfter is not null,
                EnvelopeFlowEmailChannel.Clamp(result.RetryAfter)),
            ChatSubmissionStatus.Unknown => new ChannelSendUnknown(),
            _ => new ChannelSendUnknown(),
        };
    }

    [GeneratedRegex(@"^\d+\.\d+\z")]
    private static partial Regex SlackTs();
}
