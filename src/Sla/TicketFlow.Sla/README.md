# TicketFlow.Sla

SLA engine for TicketFlow.

- `AddSla(options => options.WorkerClientId = new ApiClientId("sla-worker"))` registers `sla:read`/`sla:manage`, the
  access-checked `ISlaPolicyService` and `ISlaService` (status, breach prediction, at-risk/breached lists with Access filters),
  `SlaConversationTracker` (an `IConversationOutboxHandler` keeping per-conversation clocks in sync and appending `sla.applied`,
  `sla.warning`, `sla.breached` and `sla.met` extension events) and `SlaTimerScheduler` (durable due-work timers).
- `SlaEngine` is the deterministic clock state machine; `SlaClockTime` routes all business-time math through
  `BusinessCalendarCalculator`.
- `AddInMemorySlaStores()` registers development-only, non-durable stores. Hosts run `ConversationOutboxDispatcher.RunOnceAsync`
  and `SlaTimerScheduler.RunOnceAsync` in their own loops; the worker API client needs `conversation:manage`.
