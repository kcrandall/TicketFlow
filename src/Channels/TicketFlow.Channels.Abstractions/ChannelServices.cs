using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>The message was recorded (or, when <see cref="Replayed"/>, had already been recorded).</summary>
public sealed record InboundRecorded(ConversationId Conversation, MessageId Message, bool StartedConversation, bool Replayed);

public enum InboundRejection
{
    /// <summary>No endpoint or inbox matches the channel binding in the scope.</summary>
    InboxNotFound = 1,

    InboxDisabled = 2,

    /// <summary>The endpoint's inbox is bound to a different channel binding.</summary>
    ChannelMismatch = 3,

    /// <summary>The sender address could not be normalized.</summary>
    InvalidSender = 4,

    /// <summary>The sender evidence matched several contacts.</summary>
    ContactAmbiguous = 5,

    /// <summary>The resolved contact is blocked; the message was dropped.</summary>
    ContactBlocked = 6,

    /// <summary>Access denied the ingesting principal.</summary>
    Forbidden = 7,

    /// <summary>The message is invalid for the conversation model.</summary>
    Invalid = 8,

    /// <summary>A store limit refused the write or concurrent writers kept winning.</summary>
    Rejected = 9,
}

/// <summary>The message was not recorded. <see cref="Errors"/> are non-sensitive.</summary>
public sealed record InboundRejected(InboundRejection Reason, IReadOnlyList<string> Errors)
{
    public static InboundRejected Of(InboundRejection reason, string? error = null) => new(reason, error is null ? [] : [error]);
}

/// <summary>Outcome of inbound ingestion. Switch over every case without a discard arm.</summary>
public union InboundResult(InboundRecorded, InboundRejected);

/// <summary>
/// Records normalized inbound channel messages: endpoint → inbox (binding and enabled checks) → contact resolve-or-create from the
/// sender evidence → conversation by reference chain or channel thread (same contact only) or a new conversation → idempotent
/// append → channel state. The actor must hold <c>conversation:create</c>, <c>conversation:ingest</c>, <c>contact:read</c> and
/// <c>contact:create</c> (typically an API client principal; automation and system principals are denied by Access).
/// </summary>
public interface IInboundChannelPipeline
{
    Task<InboundResult> ReceiveAsync(PrincipalRef actor, InboundChannelMessage message, CancellationToken cancellationToken = default);
}

/// <summary>The receipt was applied (or was stale or repeated: <see cref="Replayed"/>).</summary>
public sealed record DeliveryReportApplied(ConversationId Conversation, bool Replayed);

public enum DeliveryReportRejection
{
    /// <summary>No endpoint matches the channel binding in the scope.</summary>
    EndpointNotFound = 1,

    /// <summary>No outgoing message with that provider id exists in the inbox (yet).</summary>
    MessageNotFound = 2,

    Forbidden = 3,

    Invalid = 4,

    /// <summary>Concurrent delivery updates kept winning; retry the report.</summary>
    Conflict = 5,
}

public sealed record DeliveryReportRejected(DeliveryReportRejection Reason);

/// <summary>Outcome of a delivery report. Switch over every case without a discard arm.</summary>
public union DeliveryReportResult(DeliveryReportApplied, DeliveryReportRejected);

/// <summary>Applies provider delivery receipts to outgoing messages (<c>conversation:ingest</c>), monotonic and idempotent.</summary>
public interface IChannelDeliveryIngestor
{
    Task<DeliveryReportResult> ReportAsync(PrincipalRef actor, ChannelDeliveryReport report, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a channel-aware reply. Switch over every case without a discard arm.</summary>
public union ChannelReplyResult(ConversationApplied, ConversationNotFound, ConversationConflict, ConversationRefused, ReplyWindowClosed);

/// <summary>Reply window as seen by a principal allowed to reply. Switch over every case without a discard arm.</summary>
public union ReplyWindowResult(ReplyWindowOpen, ReplyWindowClosed, ConversationNotFound, ConversationRefused);

/// <summary>
/// Agent replies with channel rules: requires <c>conversation:reply</c> on the conversation, refuses free-form replies outside the
/// channel's reply window (<see cref="ReplyWindowClosed"/>) and attachments the channel cannot send, then records the reply through
/// <see cref="IConversationService.ReplyAsync"/> for dispatch.
/// </summary>
public interface IChannelReplyService
{
    Task<ChannelReplyResult> ReplyAsync(PrincipalRef actor, ConversationId conversation, OutgoingMessageDraft message, long? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>For composers: whether a free-form reply is currently possible and until when.</summary>
    Task<ReplyWindowResult> GetReplyWindowAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default);
}

/// <summary>
/// Endpoint administration: <c>channel:manage</c> on the bound inbox to create, update (both inboxes when moving) and delete;
/// <c>channel:read</c> to read. The inbox must exist and its <see cref="ChannelBinding"/> must equal the endpoint's kind and
/// configuration id.
/// </summary>
public interface IChannelEndpointService
{
    Task<TicketResult<StoredChannelEndpoint>> GetAsync(PrincipalRef actor, ChannelKind kind, string configurationId, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredChannelEndpoint>> CreateAsync(PrincipalRef actor, ChannelEndpoint endpoint, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredChannelEndpoint>> UpdateAsync(PrincipalRef actor, ChannelEndpoint endpoint, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredChannelEndpoint>> DeleteAsync(PrincipalRef actor, ChannelKind kind, string configurationId, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>The dispatch record of an outgoing message (<c>channel:read</c> on the conversation's inbox).</summary>
    Task<TicketResult<StoredChannelDispatch>> GetDispatchAsync(PrincipalRef actor, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default);
}
