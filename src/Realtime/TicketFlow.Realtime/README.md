# TicketFlow.Realtime

Transport-neutral realtime fan-out for TicketFlow.

- `RealtimeSubscriptionRegistry` (`IRealtimeSubscriptions`): bounded per-node connections and subscriptions. Conversation topics
  need `conversation:read`; inbox queue topics need `inbox:read` and deliver only conversations matching the subscriber's planned
  `conversation:read` filter (re-planned every `QueueFilterLifetime`); agent topics are self-only; visitors subscribe only to their
  own session. Every delivery is re-authorized against the current conversation; note summaries need `note:read`.
- `RealtimeOutboxHandler` (consumer `realtime`) publishes identifier-only `ConversationChangedEvent`s; `RealtimeRoutingEventObserver`
  publishes E8 presence, reply locks, agent status, assignments and queue changes; `RealtimeAgentActions` publishes agent typing and
  read receipts (also to linked visitor sessions, anonymized).
- `InProcessRealtimeBus` + `BusRealtimePublisher`. Replace `IRealtimeBus` to fan out across nodes.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddConversations().AddRouting()
    .AddRealtime(options => options.QueueFilterLifetime = TimeSpan.FromSeconds(15));
// Transports (for example TicketFlow.Realtime.SignalR) register IRealtimeConnection sinks with IRealtimeSubscriptions.
```
