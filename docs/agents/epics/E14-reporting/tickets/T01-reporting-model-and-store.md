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

### Review fixes (2026-11-22, after commit 4574992)

- P1 rollup write amplification: labels are no longer rolled up — each fact upserts exactly one rollup cell; label groupings and
  filters are answered from facts once per carried label with the rollup Access semantics (labels unknown), so results are unchanged
  within fact retention. Migration 2 `label-rollups-from-facts` deletes version-1 label rows and adds `CHECK (label = '')`. Tests:
  `VerifyLabelAggregatesMatchFactsAsync` (seeds 21, 22; every grouping/filter/histogram against a fact reference, in-memory and
  PostgreSQL), `ALabelledFactWritesOneRollupRow`, `VersionOneSchemasUpgradeAndBackfillTheFactHorizon`.
- P2 indexes: partial `tf_report_rollups_query_v2 (scope_key, metric, cell_us) WHERE label = ''` replaces the full index;
  `tf_report_facts_labelled (scope_key, metric, cell_us) WHERE cardinality(labels) > 0` serves label aggregates. Test:
  `AggregatesUseThePartialIndexes` (EXPLAIN of the real statement from `PostgresReportingStore.CreateAggregateCommand` over 12 000 seeded
  facts in 30 scopes).
- P2 retention/rebuild: fact prunes advance a monotonic store-wide fact horizon (`GetFactHorizonAsync`, `tf_report_retention`, backfilled
  by migration 2); `RebuildRollupsAsync` returns `ReportRebuildResult` and refuses ranges before the horizon, serialized against prunes.
  Tests: `VerifyRebuildHorizonAsync` (contract, both stores), `PrunesRacingRebuildsNeverRebuildPrunedRanges`.
- P2 concurrency: `ProjectorConcurrencyScenarios` (eight concurrent `SyncAsync` of one conversation; a late consumer replaying from
  sequence 1 racing the live dispatcher while the conversation changes — both equal a fresh single replay and roll up each fact once),
  run on both stores; `SyncGivesUpAfterMaximumWriteAttempts`; `RebuildsRacingProjectionsNeverLoseOrDoubleCount` (PostgreSQL, two data
  sources).

Verification (2026-11-22): `dotnet build TicketFlow.slnx -c Release` — 0 warnings, 0 errors; `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release` —
43 passed; `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests -c Release` — 21 passed
(isolated `postgres:18` on port 55916; three consecutive runs green).
