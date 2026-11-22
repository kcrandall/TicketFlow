# E14 — Reporting

Status: **Completed**

Support analytics without a separate reporting database.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: InsightFlow event emission and support reports (volume, response times, SLA, CSAT, agent performance).

Boundaries: reporting is a durable projection of the E5 conversation outbox (consumer `reporting`) into immutable fact rows and
15-minute rollup cells inside TicketFlow's own stores; CSAT/NPS come from E12 survey aggregates through a seam; SLA outcomes come
from authentic E9 `sla.*` timeline events. InsightFlow publishes no stable HTTP ingestion contract (its client event contract is
admitted in-process by `ClientAnalyticsIngestionService`), so the `TicketFlow.Integrations.InsightFlow` export package is deferred
to a follow-up ticket; the fact listing (`IReportingStore.ListFactsAsync`) is the export seam.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Reporting model, buckets, histogram percentiles, store contract and in-memory store](tickets/T01-reporting-model-and-store.md) | Completed | E2, E3, E5 |
| [T02 — Reporting projector (outbox consumer `reporting`)](tickets/T02-reporting-projector.md) | Completed | E14 T01, E9, E12 |
| [T03 — Report queries, access filtering, CSV export and retention](tickets/T03-report-queries-and-export.md) | Completed | E14 T02 |
| [T04 — PostgreSQL reporting store](tickets/T04-postgres-reporting-store.md) | Completed | E14 T01 |
| [T05 — Documentation](tickets/T05-reporting-docs.md) | Completed | E14 T03, E14 T04 |
| [T06 — InsightFlow event export](tickets/T06-insightflow-export.md) | Completed | E14 T04, InsightFlow client event wire contract |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release`
(37 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests -c Release` (11 passed)
against an isolated `postgres:18` container; `bash scripts/verify-package-metadata.sh` (51 package projects, before a concurrent E11
project was added to the inventory). T06 (InsightFlow export) is deferred: no stable InsightFlow HTTP ingestion contract. Parallel
post-ticket reviews are still outstanding before commit.

Review fixes (2026-11-22, after 4574992): label rollups replaced by fact-based label aggregates (one rollup row per fact), partial indexes
with an EXPLAIN test, monotonic fact horizon refusing unsafe rebuilds, projector/rebuild concurrency tests; Reporting.Tests 43 passed,
Reporting.Stores.Postgres.Tests 21 passed (real PostgreSQL), solution build 0 warnings/0 errors.
