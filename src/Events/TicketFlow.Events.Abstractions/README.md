# TicketFlow.Events.Abstractions

Public integration event stream contracts for TicketFlow.

- `IntegrationEvent` (deterministic id, time, actor, typed `IntegrationEventData` union) with `IntegrationEventTypes`
  (`ticketflow.conversation.created.v1`, …) and `EventResource` (conversation, inbox, team, assignee, contact) for filters and Access.
- `IntegrationEventCodec`: persisted `{"v":1,…}` payload JSON keyed by event type, and CloudEvents 1.0 structured JSON rendering at an
  `EventPrivacy` level (`IdsOnly`, `Metadata`, `Content`).
- `EventSubscription` (type patterns, inbox/team filters, privacy, API-client principal, sink reference) and `EventPermissions`
  (`events:read`, `events:manage` and `events:content`, the latter two sensitive).
- `IEventSink`, `IEventSubscriber`, `IEventPublisher`, `IEventSubscriptionService`, and the `IEventLog` / `IEventSubscriptionStore`
  store contracts (gapless per-workspace sequences, idempotent appends, leased fenced delivery claims, dead letters, replay).
