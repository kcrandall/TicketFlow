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
