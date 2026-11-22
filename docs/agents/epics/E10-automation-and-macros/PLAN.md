# E10 — Automation and macros

Status: **Completed**

Replayable automations that respect access control.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Durable trigger/condition/action rules, dry-run, macros, canned responses.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Model, codecs, templates, schedules and permissions](tickets/T01-model-codecs-and-permissions.md) | Completed | E2–E5, E9 |
| [T02 — Store contracts, in-memory stores and management services](tickets/T02-stores-and-management-services.md) | Completed | E10 T01 |
| [T03 — Rule engine, outbox handler, loop prevention and execution log](tickets/T03-engine-outbox-handler.md) | Completed | E10 T02, E6 |
| [T04 — Scheduler, contact follower, dry-run, replay, macros and canned responses](tickets/T04-scheduler-dry-run-replay-macros.md) | Completed | E10 T03 |
| [T05 — PostgreSQL automation store](tickets/T05-postgres-automation-store.md) | Completed | E10 T02 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` (35 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` against an isolated `postgres:18` container (10 passed; inconclusive without PostgreSQL); `bash scripts/verify-package-metadata.sh` (47 package projects validated). Parallel post-ticket reviews are still outstanding before commit. See [docs/automation.md](../../../automation.md).

Review fixes (2026-11-22): forged SLA events ignored, constant store calls per occurrence (rule-set cache, batched records, single context and timeline read), execution retention (`PurgeAsync`, migration v2), one-at-a-time schedule claims, race/resume regression tests. Automation tests 42 passed; PostgreSQL tests 12 passed against an isolated container.
