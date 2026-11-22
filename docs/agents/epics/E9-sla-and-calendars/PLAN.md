# E9 — SLA and business calendars

Status: **Completed**

Measurable, escalating service levels.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: First/next response and resolution targets, pause states, escalations, breach prediction.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Policy model, codec, permissions, policy store and service](tickets/T01-policy-model-codec-and-service.md) | Completed | E2, E3, E5 |
| [T02 — Clock engine: business time, pauses, escalations and prediction](tickets/T02-clock-engine.md) | Completed | E9 T01 |
| [T03 — Tracker store, durable timers and the outbox tracker](tickets/T03-tracker-store-and-outbox-handler.md) | Completed | E9 T02, E5 |
| [T04 — Due-work scheduler and at-risk/breached queries](tickets/T04-timer-scheduler-and-risk-queries.md) | Completed | E9 T03 |
| [T05 — PostgreSQL SLA store](tickets/T05-postgres-sla-store.md) | Completed | E9 T03 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release` (26 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Sla/TicketFlow.Sla.Stores.Postgres.Tests -c Release` against an isolated `postgres:18` container (6 passed; without PostgreSQL these 6 are inconclusive). `bash scripts/verify-package-metadata.sh` currently fails only on E6's in-progress Channels project. Parallel post-ticket reviews are still outstanding before commit. See [docs/sla.md](../../../sla.md).

Review fixes (2026-11-22, after ee963dc): fenced exactly-once appends, actor-checked dedupe, flush back-off, bounded duplicate scan, claim-scoped lease release, label replay, bounded risk queries with an at-risk index (migration 2), database lease clock. `TicketFlow.Sla.Tests` 31 passed; PostgreSQL 8 passed (`tf-pg-e9fix`); solution build 0 warnings, 0 errors. See the T03-T05 Results.
