using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Api;

/// <summary>An application-submitted inbound message for the API channel. Bodies and addresses are customer data: never logged.</summary>
public sealed record ApiInboundMessage(
    string ExternalMessageId,
    ChannelSenderEvidence Sender,
    string? Text = null,
    string? Html = null,
    string? ThreadId = null,
    string? InReplyTo = null,
    string? SenderDisplayName = null,
    IReadOnlyList<AttachmentRef>? Attachments = null,
    DateTimeOffset? ReceivedAt = null)
{
    public override string ToString() => "ApiInboundMessage { <redacted> }";
}

/// <summary>
/// Inbound side of the API channel: applications call it (from their own authenticated endpoints) to record contact messages and
/// delivery reports for an API channel configuration. Runs the standard pipeline as the calling principal, which must hold the
/// channel ingestion grants.
/// </summary>
public sealed class ApiChannelInbound(IInboundChannelPipeline pipeline, IChannelDeliveryIngestor deliveries, IOptions<ApiChannelOptions> options, TimeProvider time)
{
    private readonly IInboundChannelPipeline pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    private readonly IChannelDeliveryIngestor deliveries = deliveries ?? throw new ArgumentNullException(nameof(deliveries));
    private readonly ChannelKind kind = new((options ?? throw new ArgumentNullException(nameof(options))).Value.Kind);
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));

    public Task<InboundResult> ReceiveAsync(PrincipalRef actor, string configurationId, ApiInboundMessage message, CancellationToken cancellationToken = default)
    {
        if (actor is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
        ArgumentNullException.ThrowIfNull(message);
        InboundChannelMessage normalized;
        try
        {
            normalized = new InboundChannelMessage(actor.Scope, kind, configurationId, message.ExternalMessageId, message.Sender, message.ReceivedAt ?? time.GetUtcNow(),
                message.Text, message.Html, message.Attachments, message.ThreadId, message.InReplyTo, senderDisplayName: message.SenderDisplayName);
        }
        catch (ArgumentException)
        {
            return Task.FromResult<InboundResult>(InboundRejected.Of(InboundRejection.Invalid, "The inbound message is invalid."));
        }
        return pipeline.ReceiveAsync(actor, normalized, cancellationToken);
    }

    public Task<DeliveryReportResult> ReportDeliveryAsync(PrincipalRef actor, string configurationId, string externalMessageId, ChannelDeliveryState state,
        string? failureCode = null, CancellationToken cancellationToken = default)
    {
        if (actor is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
        ChannelDeliveryReport report;
        try
        {
            report = new ChannelDeliveryReport(actor.Scope, kind, configurationId, externalMessageId, state, time.GetUtcNow(),
                state == ChannelDeliveryState.Failed ? ChannelText.FailureCode(failureCode) : null);
        }
        catch (ArgumentException)
        {
            return Task.FromResult<DeliveryReportResult>(new DeliveryReportRejected(DeliveryReportRejection.Invalid));
        }
        return deliveries.ReportAsync(actor, report, cancellationToken);
    }
}

public static class ApiChannelTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the API channel (<see cref="ApiChannel"/> as an <see cref="ITicketChannel"/>) and <see cref="ApiChannelInbound"/>. The
    /// application must register an <see cref="IApiChannelDestinationResolver"/>. Requires <c>AddChannels()</c>.
    /// </summary>
    public static TicketFlowBuilder AddApiChannel(this TicketFlowBuilder builder, Action<ApiChannelOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ApiChannelOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new ApiChannel(provider.GetRequiredService<IApiChannelDestinationResolver>(),
            provider.GetRequiredService<IOptions<ApiChannelOptions>>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITicketChannel, ApiChannel>(provider => provider.GetRequiredService<ApiChannel>()));
        services.TryAddScoped<ApiChannelInbound>();
        return builder;
    }
}
