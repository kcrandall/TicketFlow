using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>Delivery states a provider can report for an outgoing message.</summary>
public enum ChannelDeliveryState
{
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4,
}

/// <summary>
/// A provider delivery receipt for an outgoing message, correlated by the inbox's binding and the provider message id.
/// <see cref="FailureCode"/> (a token, see <see cref="ChannelText.FailureCode"/>) accompanies only <see cref="ChannelDeliveryState.Failed"/>.
/// </summary>
public sealed record ChannelDeliveryReport
{
    public ChannelDeliveryReport(TicketScope scope, ChannelKind channel, string configurationId, string externalMessageId, ChannelDeliveryState state,
        DateTimeOffset occurredAt, string? failureCode = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (!channel.IsInitialized)
            throw new ArgumentException("A channel kind is required.", nameof(channel));
        Channel = channel;
        ConfigurationId = TicketScope.RequireIdentifier(configurationId, nameof(configurationId));
        ExternalMessageId = ChannelText.RequireText(externalMessageId, ChannelText.MaximumExternalIdLength, nameof(externalMessageId));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        OccurredAt = occurredAt.ToUniversalTime();
        if (failureCode is not null && state != ChannelDeliveryState.Failed)
            throw new ArgumentException("Only failed reports carry a failure code.", nameof(failureCode));
        FailureCode = failureCode is null ? null : ChannelText.RequireToken(failureCode, MessageDelivery.MaximumFailureCodeLength, nameof(failureCode));
    }

    public TicketScope Scope { get; }
    public ChannelKind Channel { get; }
    public string ConfigurationId { get; }
    public string ExternalMessageId { get; }
    public ChannelDeliveryState State { get; }
    public DateTimeOffset OccurredAt { get; }
    public string? FailureCode { get; }

    public MessageDeliveryState ToMessageState() => State switch
    {
        ChannelDeliveryState.Sent => MessageDeliveryState.Sent,
        ChannelDeliveryState.Delivered => MessageDeliveryState.Delivered,
        ChannelDeliveryState.Read => MessageDeliveryState.Read,
        ChannelDeliveryState.Failed => MessageDeliveryState.Failed,
        _ => throw new InvalidOperationException("Unknown delivery state."),
    };

    public override string ToString() => $"ChannelDeliveryReport {{ Channel = {Channel}, State = {State} }}";
}
