# T04 — PostgreSQL reporting store

Epic: [E14 — Reporting](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Reporting.Stores.Postgres`: `PostgresReportingStore` implementing `IReportingStore` with the append-only
`ticketflow.reporting` migration catalog, `tf_key_v1` keys and translated Access filters.

## Dependencies

E14 T01.

## Acceptance criteria

- One transaction per projection: fenced state row, `INSERT … ON CONFLICT DO NOTHING RETURNING` facts, rollup upserts for the
  inserted facts in key order.
- Aggregates bucket with `width_bucket` over supplied boundaries; filters via `PostgresAccessFilterTranslator` with unknown labels
  and attributes mapped to NULL columns (Kleene parity with `AccessFilterEvaluator`).
- Shared store contract passes against two independent data sources.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Rollup cells are hot rows under bursty load in one workspace; upserts are ordered by key to avoid deadlocks.

## Results

Implemented `PostgresReportingStore` (migration 1 `initial-schema` of `ticketflow.reporting`: `tf_report_conversations`,
`tf_report_facts`, `tf_report_rollups`; `cell_us` microsecond cells; one transaction per projection with shared scope advisory lock;
`unnest` fact insert `ON CONFLICT DO NOTHING RETURNING`; ordered rollup upsert; `width_bucket` aggregates; NULL label/attribute
columns for Kleene parity; exclusive-lock rebuild; `SKIP LOCKED` retention), `PostgresReportingMigrations`,
`AddPostgresReportingStores`. Tests: `PostgresReportingStoreTests` (all contracts on two data sources, parity seeds 12-14, idempotent
initialization, undecodable rows, projector + service end to end).

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
