# TicketFlow.Realtime.Abstractions

Transport-neutral realtime contracts for TicketFlow.

- Topics (`RealtimeTopic` union): `ConversationTopic`, `InboxQueueTopic`, `AgentTopic`, `VisitorSessionTopic`.
- Subscribers (`RealtimeSubscriber` union): `AgentSubscriber` (an application-resolved `PrincipalRef`) and `VisitorSubscriber`
  (a live-chat session whose token was verified).
- Events (`RealtimeEvent` union): identifier-only conversation change notifications, presence, reply locks, agent status,
  assignments, queue changes, typing, read receipts, and visitor-only message and session events.
- `IRealtimePublisher`, `IRealtimeBus` (node fan-out transport), `IRealtimeConnection` (transport sink),
  `IRealtimeSubscriptions` (authorized subscription registry), `IRealtimeAgentActions`, `IVisitorSessionDirectory`.

Notifications never carry bodies, attribute values or masked fields; clients refetch through the Access-checked services. The
only content-bearing event, `VisitorMessageEvent`, is delivered exclusively to the owning visitor session. See `docs/realtime.md`.
