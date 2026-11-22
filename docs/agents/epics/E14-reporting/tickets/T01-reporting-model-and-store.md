# T01 — Reporting model, buckets, histogram percentiles, store contract and in-memory store

Epic: [E14 — Reporting](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Reporting.Abstractions`: `ReportMetric`, immutable `ReportFact` (key = source event id + metric), `ReportDimensions`,
per-conversation `ReportConversationState`, 15-minute UTC rollup cells, `ReportBuckets` (hour/day/week/month in a time zone,
DST-correct), `ReportHistogram` (log-linear bins, deterministic percentile estimates), query records, union results,
`ReportPermissions` (`report:export` sensitive, `report:view-own`), `ReportResources` and `IReportingStore`. `TicketFlow.Reporting`
ships the development `InMemoryReportingStore`.

## Dependencies

E2, E3, E5.

## Acceptance criteria

- Applying a projection is atomic: the state is revision-fenced, facts whose key exists are ignored and only newly inserted facts
  update rollup cells (exactly once per source event); late facts land in their own event-time cells.
- Aggregates group cells by caller-supplied bucket boundaries and an optional dimension, with histogram rows for duration metrics,
  bounded rows (`Truncated`), and the Access filter applied in the query over the rollup resource (labels unknown).
- Facts list in keyset order with the Access filter over the fact resource (labels known); backlog counts over state rows.
- Retention pruning of facts and rollups is store-wide and bounded; rollups can be rebuilt from facts for a range.

## Verification

- `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release` (model, bucket boundary and percentile tests, store contract
  on the in-memory store).

## Risks and boundaries

The in-memory store is a process-local development example. Bucket boundaries assume zone offsets that are multiples of 15
minutes (every current IANA zone).

## Results

Implemented `TicketFlow.Reporting.Abstractions` (`ReportMetric` (16 metrics), `ReportFact` + `ReportFactIds.Created`,
`ReportDimensions`, `ReportConversationState`, `ReportProjection`, `ReportBuckets` (15-minute cells, DST-correct hour/day/week/month
boundaries, `IndexOf`), `ReportHistogram` (241 log-linear bins, nearest-rank estimates), `ReportAggregateQuery`/`Row`/`Result`,
`ReportFactQuery`/`Cursor`/`Page`, `ReportFilter`, `ReportDimension`, unions `ReportWriteResult` and `ReportResult<T>`,
`ReportPermissions`, `ReportResources`, `IReportingStore`) and `InMemoryReportingStore` (bounded, one lock). Shared fixture
`tests/Reporting/TicketFlow.Reporting.StoreContracts` (`ReportingStoreContract`, `AccessFilteredReportParity`). Tests: `ModelTests`
(histogram contiguity, percentile error bound over seeded data, DST/half-hour/quarter-hour/midnight-gap buckets, weeks, months, CSV)
and `InMemoryReportingStoreTests` (all contracts, parity seeds 12-14, capacity).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with concurrent E11 work in the tree).
- `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release` — 37 passed, 0 failed.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests -c Release` — 11 passed,
  0 failed, against an isolated `postgres:18` container (`tf-pg-e14`, port 55914).
- `bash scripts/verify-package-metadata.sh` — "Validated 51 package projects and central versions" after adding the three reporting
  packages (a later run failed only on a concurrent E11 project, `TicketFlow.Hosting.Worker`, not yet in the solution).

Post-ticket parallel reviews have not been run yet.
