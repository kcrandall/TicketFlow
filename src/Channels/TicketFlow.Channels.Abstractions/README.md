# TicketFlow.Channels.Abstractions

Provider-neutral channel contracts for TicketFlow.

- `ITicketChannel` (kind, `ChannelCapabilities` flags — attachments, rich content, reply window, threading, typing, delivery and
  read receipts, templates — recipient identity kinds, `SendAsync`) and `IChannelRegistry`.
- `InboundChannelMessage`: trusted scope, channel kind and binding configuration id, external message/thread ids,
  In-Reply-To/References, `ChannelSenderEvidence` (kind, value, verified/claimed), text/HTML bodies, attachment refs, received-at.
- `OutboundChannelMessage` with `ChannelThreadContext` and optional `ChannelTemplate`; `ChannelSendResult = Accepted | Rejected(code,
  retryable) | Unknown`.
- `ChannelDeliveryReport`, `ChannelReplyWindow.Evaluate` (`ReplyWindowState = Open | Closed`).
- Services: `IInboundChannelPipeline`, `IChannelDeliveryIngestor`, `IChannelReplyService`, `IChannelEndpointService` with union
  results; `ChannelPermissions` (`channel:read`, `channel:manage`).
- Store contracts: `IChannelEndpointStore`, `IChannelConversationStateStore`, `IChannelThreadStore`, `IChannelDispatchStore`.

Addresses, thread ids and Message-Ids are correlation evidence, never authentication; bodies and addresses are never logged.
