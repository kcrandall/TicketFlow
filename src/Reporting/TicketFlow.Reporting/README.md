# TicketFlow.Reporting

Support reporting for TicketFlow.

- `AddReporting(options => options.SlaWorkerClientId = new ApiClientId("sla-worker"))` registers `report:export` (sensitive) and
  `report:view-own`, `ReportingProjector` (an `IConversationOutboxHandler`, consumer `reporting`), `IReportingService`,
  `ReportingRetentionWorker` and a survey-backed `IReportSatisfactionSource`.
- `ReportingProjector` replays each conversation's timeline into immutable facts (created, resolved, reopened, messages by
  direction and channel, first response and per-reply response times, resolution time in calendar and inbox business time,
  assignments, handoffs, team transfers, SLA achieved/breached from authentic `sla.*` events) and applies them atomically with
  their 15-minute rollup cells — exactly once per source event.
- `ReportingService`: overview, agent leaderboard, time-zone trends and streamed CSV export, all filtered by Access plans inside the
  store query.
- `AddInMemoryReportingStores()` registers the development-only, non-durable `InMemoryReportingStore`. Hosts run
  `ConversationOutboxDispatcher.RunOnceAsync` and `ReportingRetentionWorker.RunOnceAsync` in their own loops.
