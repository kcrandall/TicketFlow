using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Channels;

/// <summary>Default <see cref="IChannelDeliveryIngestor"/>: endpoint → inbox, then <see cref="IConversationService.UpdateDeliveryStatusAsync"/> by provider id.</summary>
public sealed class ChannelDeliveryIngestor(IChannelEndpointStore endpoints, IConversationService conversations) : IChannelDeliveryIngestor
{
    private readonly IChannelEndpointStore endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    private readonly IConversationService conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));

    public async Task<DeliveryReportResult> ReportAsync(PrincipalRef actor, ChannelDeliveryReport report, CancellationToken cancellationToken = default)
    {
        if (actor.Value is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
        ArgumentNullException.ThrowIfNull(report);
        if (actor.Scope != report.Scope)
            return new DeliveryReportRejected(DeliveryReportRejection.Forbidden);
        var endpoint = await endpoints.GetAsync(report.Scope, report.Channel, report.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
            return new DeliveryReportRejected(DeliveryReportRejection.EndpointNotFound);
        var update = DeliveryStatusUpdate.ByExternalId(endpoint.Endpoint.Inbox, report.ExternalMessageId, report.ToMessageState(),
            report.State == ChannelDeliveryState.Failed ? report.FailureCode ?? "failed" : null);
        return await conversations.UpdateDeliveryStatusAsync(actor, update, cancellationToken).ConfigureAwait(false) switch
        {
            ConversationApplied applied => new DeliveryReportApplied(applied.Conversation, applied.Replayed),
            ConversationNotFound => new DeliveryReportRejected(DeliveryReportRejection.MessageNotFound),
            ConversationConflict => new DeliveryReportRejected(DeliveryReportRejection.Conflict),
            ConversationRefused { Reason: ConversationRefusal.Forbidden } => new DeliveryReportRejected(DeliveryReportRejection.Forbidden),
            ConversationRefused { Reason: ConversationRefusal.Rejected } => new DeliveryReportRejected(DeliveryReportRejection.Conflict),
            ConversationRefused => new DeliveryReportRejected(DeliveryReportRejection.Invalid),
        };
    }
}
