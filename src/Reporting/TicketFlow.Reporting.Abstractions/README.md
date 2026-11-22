# TicketFlow.Reporting.Abstractions

Contracts for TicketFlow support reporting.

- `ReportFact`: an immutable fact keyed by (source event id, `ReportMetric`) with `ReportDimensions` (inbox, team, attributed agent,
  channel, priority, labels) and a value (1 for counts, microseconds for durations). `ReportFactIds.Created` derives the source of
  conversation-created facts.
- `ReportConversationState` (per-conversation projection state, revision-fenced) and `ReportProjection` (state + facts, applied
  atomically).
- `ReportBuckets`: 15-minute UTC rollup cells and DST-correct hour/day/week/month bucket boundaries in any time zone.
- `ReportHistogram`: deterministic log-linear bins and nearest-rank percentile estimates (≤ 6.25 % relative error, exact for
  single-valued bins).
- Queries (`ReportAggregateQuery`, `ReportFactQuery`), service queries (`ReportQuery`, `ReportLeaderboardQuery`, `ReportTrendQuery`,
  `ReportExportQuery`), results (`ReportOverview`, `ReportLeaderboard`, `ReportTrend`, `ReportExportSummary`) and unions
  (`ReportWriteResult`, `ReportResult<T>`).
- `ReportPermissions` (`report:view` built-in, `report:export` sensitive, `report:view-own`), `ReportResources` (rollup, fact and
  conversation Access snapshots), `IReportingStore`, `IReportingService` and the `IReportSatisfactionSource` seam.

Behavior lives in `TicketFlow.Reporting`; durable storage in `TicketFlow.Reporting.Stores.Postgres`.
