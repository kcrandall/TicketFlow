using System.Buffers;
using System.Text;
using EnvelopeFlow.Inbound;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketFlow.Channels;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.EnvelopeFlow;

/// <summary>The receipt names a message TicketFlow has not correlated yet; EnvelopeFlow should redeliver it later.</summary>
public sealed class EnvelopeFlowReceiptPendingException() : Exception("The delivery receipt's message is not correlated yet; retry later.");

/// <summary>
/// The adapter's single EnvelopeFlow entry point (<see cref="IInboundEventHandler"/>). It finds the channel endpoint from the
/// event's scope partition, provider and account (<see cref="EnvelopeFlowEndpointAddresses"/>), then:
/// <list type="bullet">
/// <item><c>Message</c> events become <see cref="InboundChannelMessage"/>s (email threads by In-Reply-To; SMS and WhatsApp by the
/// sender number; Slack by channel and thread root ts) recorded by <see cref="IInboundChannelPipeline"/>.</item>
/// <item><c>DeliveryReceipt</c> events become delivery reports (Accepted/Sent → Sent, Delivered, Read, Failed/Undelivered → Failed
/// with the provider error or bounce class; Deferred is ignored). Conflicting concurrent updates throw so the receipt is retried. Receipts for messages not yet correlated throw
/// <see cref="EnvelopeFlowReceiptPendingException"/> within <see cref="EnvelopeFlowIntegrationOptions.ReceiptCorrelationGrace"/> so
/// the durable receipt is retried, and are dropped afterwards.</item>
/// <item><c>Feedback</c> events are ignored (suppression belongs to EnvelopeFlow's feedback packages).</item>
/// </list>
/// Delivery is at-least-once; TicketFlow's per-inbox external message id and monotonic delivery states make redelivery idempotent.
/// Runs as the principal from <see cref="IChannelWorkerPrincipalResolver"/>. Unknown providers, endpoints and invalid messages are dropped (not
/// retried). Never logs bodies or addresses.
/// </summary>
public sealed partial class EnvelopeFlowInboundEventHandler : IInboundEventHandler
{
    private readonly IChannelEndpointStore endpoints;
    private readonly IInboundChannelPipeline pipeline;
    private readonly IChannelDeliveryIngestor deliveries;
    private readonly IChannelWorkerPrincipalResolver workers;
    private readonly EnvelopeFlowIntegrationOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public EnvelopeFlowInboundEventHandler(IChannelEndpointStore endpoints, IInboundChannelPipeline pipeline, IChannelDeliveryIngestor deliveries,
        IChannelWorkerPrincipalResolver workers, IOptions<EnvelopeFlowIntegrationOptions> options, TimeProvider time, ILogger<EnvelopeFlowInboundEventHandler>? logger = null)
    {
        this.endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        this.deliveries = deliveries ?? throw new ArgumentNullException(nameof(deliveries));
        this.workers = workers ?? throw new ArgumentNullException(nameof(workers));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.logger = logger ?? NullLogger<EnvelopeFlowInboundEventHandler>.Instance;
    }

    public async Task HandleAsync(InboundEvent inboundEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);
        inboundEvent.Validate();
        if (inboundEvent.Kind == InboundEventKind.Feedback)
            return;
        if (!options.ProviderKinds.TryGetValue(inboundEvent.Provider, out var kind))
        {
            LogUnmappedProvider(logger);
            return;
        }
        StoredChannelEndpoint? endpoint;
        if (inboundEvent.Kind == InboundEventKind.Message)
            endpoint = await FindEndpointAsync(inboundEvent, kind, inboundEvent.To, includeAccountFallback: true, cancellationToken).ConfigureAwait(false);
        else
        {
            // Delivery receipts check From, then To, and fall back to Account only once.
            endpoint = await FindEndpointAsync(inboundEvent, kind, inboundEvent.From, includeAccountFallback: false, cancellationToken).ConfigureAwait(false)
                ?? await FindEndpointAsync(inboundEvent, kind, inboundEvent.To, includeAccountFallback: true, cancellationToken).ConfigureAwait(false);
        }
        if (endpoint is null)
        {
            LogNoMatchingEndpoint(logger, inboundEvent.Kind);
            return;
        }
        var actor = workers.Resolve(endpoint.Scope);
        switch (inboundEvent.Kind)
        {
            case InboundEventKind.Message:
                await MessageAsync(actor, endpoint, kind, inboundEvent, cancellationToken).ConfigureAwait(false);
                break;
            case InboundEventKind.DeliveryReceipt:
                await ReceiptAsync(actor, endpoint, kind, inboundEvent, cancellationToken).ConfigureAwait(false);
                break;
            case InboundEventKind.Feedback:
                break;
        }
    }

    private async Task<StoredChannelEndpoint?> FindEndpointAsync(InboundEvent inboundEvent, ChannelKind kind, string? local, bool includeAccountFallback, CancellationToken cancellationToken)
    {
        var partition = EnvelopeFlowScopes.ToPartition(inboundEvent.Scope);
        if (!string.IsNullOrWhiteSpace(local) && local.Length <= 256 && !local.Any(char.IsControl)
            && await endpoints.FindByAddressAsync(partition, kind, EnvelopeFlowEndpointAddresses.Local(inboundEvent.Provider, inboundEvent.ProviderAccountId, local), cancellationToken)
                .ConfigureAwait(false) is { } narrow)
            return narrow;
        if (!includeAccountFallback)
            return null;
        return await endpoints.FindByAddressAsync(partition, kind, EnvelopeFlowEndpointAddresses.Account(inboundEvent.Provider, inboundEvent.ProviderAccountId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task MessageAsync(Core.PrincipalRef actor, StoredChannelEndpoint endpoint, ChannelKind kind, InboundEvent inboundEvent, CancellationToken cancellationToken)
    {
        InboundChannelMessage message;
        try
        {
            message = Normalize(endpoint, kind, inboundEvent);
        }
        catch (ArgumentException)
        {
            LogInvalidMessage(logger, kind);
            return;
        }
        switch (await pipeline.ReceiveAsync(actor, message, cancellationToken).ConfigureAwait(false))
        {
            case InboundRecorded:
                return;
            case InboundRejected { Reason: InboundRejection.Rejected }:
                throw new InvalidOperationException("The inbound message was not recorded; it will be retried.");
            case InboundRejected { Reason: InboundRejection.Forbidden }:
                throw new InvalidOperationException("The channel worker principal lacks the ingestion grants.");
            case InboundRejected rejected:
                LogDroppedMessage(logger, kind, rejected.Reason);
                return;
        }
    }

    private InboundChannelMessage Normalize(StoredChannelEndpoint endpoint, ChannelKind kind, InboundEvent inboundEvent)
    {
        var from = inboundEvent.From ?? throw new ArgumentException("A sender is required.", nameof(inboundEvent));
        var verified = options.VerifiedSenderKinds.Contains(kind);
        ChannelSenderEvidence sender;
        string? thread = null;
        string? inReplyTo = null;
        if (kind == EnvelopeFlowChannelKinds.Email)
        {
            sender = new ChannelSenderEvidence(ContactIdentityKind.Email, from, verified);
            inReplyTo = inboundEvent.InReplyTo;
        }
        else if (kind == EnvelopeFlowChannelKinds.WhatsApp)
        {
            sender = new ChannelSenderEvidence(ContactIdentityKind.WhatsApp, from, verified);
            thread = from;
        }
        else if (kind == EnvelopeFlowChannelKinds.Slack)
        {
            sender = new ChannelSenderEvidence(ContactIdentityKind.ExternalUser, from, verified, "slack");
            var channel = inboundEvent.To ?? throw new ArgumentException("A Slack channel is required.", nameof(inboundEvent));
            thread = $"{channel}:{LastSegment(inboundEvent.InReplyTo ?? inboundEvent.ProviderMessageId)}";
        }
        else
        {
            sender = new ChannelSenderEvidence(ContactIdentityKind.Phone, from, verified);
            thread = from;
        }
        var attachments = new List<AttachmentRef>();
        foreach (var attachment in inboundEvent.Attachments ?? [])
        {
            if (attachments.Count >= ConversationMessageLimits.MaximumAttachments)
                break;
            if (attachment is null)
                continue;
            var providerReference = SanitizeProviderReference(attachment.ProviderReference);
            if (providerReference is null)
                continue;
            var reference = $"envelopeflow:{inboundEvent.Provider}:{providerReference}";
            if (reference.Length > AttachmentRef.MaximumStorageRefLength || !ChannelText.IsClean(reference))
                continue;
            var fileName = SanitizeFileName(attachment.FileName);
            var mediaType = SanitizeMediaType(attachment.ContentType);
            attachments.Add(new AttachmentRef(reference, fileName, mediaType, 0));
        }
        var bodyText = inboundEvent.Text is null ? null : ChannelText.SanitizeBody(inboundEvent.Text, ConversationMessageLimits.MaximumBodyBytes);
        var subject = SanitizeSingleLine(inboundEvent.Subject, InboundChannelMessage.MaximumSubjectLength);
        return new InboundChannelMessage(endpoint.Scope, kind, endpoint.Endpoint.ConfigurationId, inboundEvent.ProviderMessageId, sender, inboundEvent.ObservedAt,
            bodyText: bodyText, attachments: attachments, externalThreadId: thread, inReplyTo: inReplyTo,
            subject: subject);
    }

    private async Task ReceiptAsync(Core.PrincipalRef actor, StoredChannelEndpoint endpoint, ChannelKind kind, InboundEvent inboundEvent, CancellationToken cancellationToken)
    {
        ChannelDeliveryState state;
        string? code = null;
        switch (inboundEvent.DeliveryState)
        {
            case InboundDeliveryState.Accepted:
            case InboundDeliveryState.Sent:
                state = ChannelDeliveryState.Sent;
                break;
            case InboundDeliveryState.Delivered:
                state = ChannelDeliveryState.Delivered;
                break;
            case InboundDeliveryState.Read:
                state = ChannelDeliveryState.Read;
                break;
            case InboundDeliveryState.Failed:
            case InboundDeliveryState.Undelivered:
                state = ChannelDeliveryState.Failed;
                code = inboundEvent.ProviderErrorCode is { } error ? ChannelText.FailureCode(error)
                    : inboundEvent.BounceKind is { } bounce ? "bounce-" + bounce.ToString().ToLowerInvariant() : "undelivered";
                break;
            default:
                return;
        }
        var report = new ChannelDeliveryReport(endpoint.Scope, kind, endpoint.Endpoint.ConfigurationId, inboundEvent.ProviderMessageId, state, inboundEvent.ObservedAt, code);
        switch (await deliveries.ReportAsync(actor, report, cancellationToken).ConfigureAwait(false))
        {
            case DeliveryReportApplied:
                return;
            case DeliveryReportRejected { Reason: DeliveryReportRejection.MessageNotFound } when time.GetUtcNow() - inboundEvent.ObservedAt < options.ReceiptCorrelationGrace:
                throw new EnvelopeFlowReceiptPendingException();
            case DeliveryReportRejected { Reason: DeliveryReportRejection.Forbidden }:
                throw new InvalidOperationException("The channel worker principal lacks conversation:ingest.");
            case DeliveryReportRejected { Reason: DeliveryReportRejection.Conflict }:
                throw new InvalidOperationException("The delivery update conflicted with concurrent updates; it will be retried.");
            case DeliveryReportRejected rejected:
                LogDroppedReceipt(logger, kind, rejected.Reason);
                return;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an EnvelopeFlow event from an unmapped provider.")]
    private static partial void LogUnmappedProvider(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an EnvelopeFlow {Kind} event without a matching channel endpoint.")]
    private static partial void LogNoMatchingEndpoint(ILogger logger, InboundEventKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an invalid EnvelopeFlow {Channel} message.")]
    private static partial void LogInvalidMessage(ILogger logger, ChannelKind channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped an EnvelopeFlow {Channel} message: {Reason}.")]
    private static partial void LogDroppedMessage(ILogger logger, ChannelKind channel, InboundRejection reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped an EnvelopeFlow {Channel} delivery receipt: {Reason}.")]
    private static partial void LogDroppedReceipt(ILogger logger, ChannelKind channel, DeliveryReportRejection reason);

    private static string LastSegment(string value)
    {
        var index = value.LastIndexOf(':');
        return index < 0 ? value : value[(index + 1)..];
    }

    private static string? SanitizeSingleLine(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw) || maxChars <= 0)
            return null;
        var span = raw.AsSpan();
        var builder = new StringBuilder(Math.Min(span.Length, maxChars));
        Span<char> encoded = stackalloc char[2];
        var pendingSpace = false;
        var index = 0;
        while (index < span.Length)
        {
            var status = Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed);
            index += Math.Max(consumed, 1);
            if (status != OperationStatus.Done)
                continue;
            if (Rune.IsWhiteSpace(rune))
            {
                if (builder.Length > 0)
                    pendingSpace = true;
                continue;
            }
            if (rune.Value is < ' ' or (>= 0x7F and <= 0x9F))
                continue;
            var needed = rune.Utf16SequenceLength + (pendingSpace ? 1 : 0);
            if (builder.Length + needed > maxChars)
                break;
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            var written = rune.EncodeToUtf16(encoded);
            builder.Append(encoded[..written]);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? SanitizeProviderReference(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var span = raw.AsSpan();
        var builder = new StringBuilder(Math.Min(span.Length, AttachmentRef.MaximumStorageRefLength));
        Span<char> encoded = stackalloc char[2];
        var index = 0;
        while (index < span.Length)
        {
            var status = Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed);
            index += Math.Max(consumed, 1);
            if (status != OperationStatus.Done || rune.Value is < ' ' or (>= 0x7F and <= 0x9F))
                continue;
            var written = rune.EncodeToUtf16(encoded);
            builder.Append(encoded[..written]);
        }
        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static string SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "attachment";
        var lastSlash = raw.LastIndexOfAny(['/', '\\']);
        var segment = lastSlash >= 0 ? raw[(lastSlash + 1)..] : raw;
        var sanitized = SanitizeSingleLine(segment, AttachmentRef.MaximumFileNameLength);
        return sanitized is null or "." or ".." ? "attachment" : sanitized;
    }

    private static string SanitizeMediaType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "application/octet-stream";
        var semicolon = raw.IndexOf(';');
        var head = semicolon >= 0 ? raw[..semicolon] : raw;
        var cleaned = SanitizeProviderReference(head);
        if (cleaned is null || cleaned.Length > AttachmentRef.MaximumMediaTypeLength || !cleaned.Contains('/') || cleaned.Any(char.IsWhiteSpace) || !ChannelText.IsClean(cleaned))
            return "application/octet-stream";
        return cleaned.ToLowerInvariant();
    }
}
