# TicketFlow.Sla.Abstractions

Contracts for TicketFlow service-level agreements.

- `SlaPolicy`: ordered `SlaRule`s (`SlaCondition` over priority and labels, `SlaTargets` for first response, next response and
  resolution, `SlaClockBasis` business or calendar time, `SlaEscalation`s with the `SlaAction` union: notify user/team, raise
  priority, reassign team), `SlaPausePolicy` (Pending/Snoozed pause clocks) and an at-risk percentage. `SlaPolicyCodec` persists
  definitions as versioned JSON with explicit discriminators.
- `SlaTracker`: per-conversation clocks (`SlaClock`: elapsed measured time, due instant, fired thresholds, met/missed outcome),
  pause history, pending timeline emissions, the mirrored Access snapshot and the derived `SlaSchedule`. `SlaTrackerCodec`
  persists it.
- Timeline events `SlaTimelineEvent = SlaAppliedEvent | SlaWarningEvent | SlaBreachedEvent | SlaMetEvent`, encoded by
  `SlaEventCodec` as conversation `ExtensionEvent`s (`sla.applied`, `sla.warning`, `sla.breached`, `sla.met`, payload version 1).
- `SlaPermissions` (`sla:read`, `sla:manage`), store contracts `ISlaPolicyStore` and `ISlaTrackerStore` (revision-fenced trackers,
  durable leased timers, filtered risk lists) and services `ISlaPolicyService` and `ISlaService` with union results.
