# T03 — Report queries, access filtering, CSV export and retention

Epic: [E14 — Reporting](../PLAN.md)

Status: **Completed**

## Goal

`IReportingService`: overview (volume, open backlog, FRT and resolution median/p90, SLA compliance, CSAT, NPS), agent leaderboard,
trends (hour/day/week/month in the workspace time zone, grouped by inbox/team/agent/channel/label/priority) and streamed, bounded
CSV export of facts. `ReportingRetentionWorker.RunOnceAsync` prunes facts and rollups per retention options.

## Dependencies

E14 T02.

## Acceptance criteria

- Reads use the `report:view` plan (plus `report:view-own` limited to the principal's own agent rows) pushed into the store; no
  applicable grant yields empty reports.
- Export requires `report:export` (sensitive, so the plan is audited), is streamed page by page and bounded, and guards CSV formula
  injection.
- Workspace time zone buckets are DST-correct; ranges are aligned to bucket starts.

## Verification

- `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release`.

## Risks and boundaries

Satisfaction figures use survey aggregates (survey response access resource) and are omitted when the report filters by channel,
priority, label or more than one of inbox/team/agent.

## Results

Implemented `ReportingService` (overview, leaderboard, trends, CSV export; `report:view` ∨ (`report:view-own` ∧ agent =
self) plans pushed into the store; export requires a `report:export` plan, audited by the planner; streamed 1000-fact pages, bounded,
formula-neutralized `Csv`), `SurveyReportSatisfactionSource` / `NoReportSatisfactionSource`, `ReportingRetentionWorker.RunOnceAsync`,
`ReportingOptions`, `AddReporting` / `AddInMemoryReportingStores`. Tests: `ServiceTests` (overview numbers and percentiles, SLA
compliance, CSAT/NPS, filter narrowing, team-scoped / own-stats / no-grant access, leaderboard sorts, New York day trends across DST,
hourly trends, export audit/refusal/truncation/paging, retention, composition).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with concurrent E11 work in the tree).
- `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release` — 37 passed, 0 failed.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests -c Release` — 11 passed,
  0 failed, against an isolated `postgres:18` container (`tf-pg-e14`, port 55914).
- `bash scripts/verify-package-metadata.sh` — "Validated 51 package projects and central versions" after adding the three reporting
  packages (a later run failed only on a concurrent E11 project, `TicketFlow.Hosting.Worker`, not yet in the solution).

Post-ticket parallel reviews have not been run yet.
