# TicketFlow.Events

The public integration event stream for TicketFlow.

- `AddEvents(options => options.SlaWorkerClientId = new ApiClientId("sla-worker"))` registers `events:read`, `events:manage` and
  `events:content` (the latter two sensitive), `EventProjector` (an `IConversationOutboxHandler`, consumer `events`),
  `EventContactFollower`, `EventSurveyFollower`, `EventRoutingObserver`, `IEventSubscriptionService`, `EventDeliveryWorker`,
  `EventRetentionWorker` and the `in-process` sink for `IEventSubscriber`s (`AddEventSubscriber<T>()`).
- Events are written once per source change into a gapless per-workspace log (ids derived from their sources) and fanned out
  at least once to subscriptions in sequence order, filtered by type pattern, inbox and team and authorized for the subscription's
  API-client principal; failures back off, then dead-letter; replay rewinds a subscription's cursor.
- `AddInMemoryEventStores()` registers the development-only, non-durable `InMemoryEventStore`. Hosts run
  `ConversationOutboxDispatcher.RunOnceAsync`, `EventDeliveryWorker.RunOnceAsync`, `EventContactFollower.CatchUpAsync(scope)`,
  `EventSurveyFollower.CatchUpAsync(scope)` and `EventRetentionWorker.RunOnceAsync` in their own loops.
