# E2 — Access control

Status: **Completed**

Every later feature can authorize point actions and list queries through one engine with union-typed decisions.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Permission catalog, roles, scoped grants, deny, conditions, masking, query filters, break-glass and audit.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Permission catalog and roles](tickets/T01-permission-catalog-and-roles.md) | Completed | None |
| [T02 — Scoped grants, inheritance and explicit deny](tickets/T02-scoped-grants-and-deny.md) | Completed | E2 T01 |
| [T03 — Union decisions and attribute conditions](tickets/T03-decisions-and-conditions.md) | Completed | E2 T02 |
| [T04 — Field masking policies](tickets/T04-field-masking.md) | Completed | E2 T03 |
| [T05 — Access query filters](tickets/T05-query-filters.md) | Completed | E2 T03 |
| [T06 — Break-glass grants and decision audit](tickets/T06-break-glass-and-audit.md) | Completed | E2 T02, E2 T04 |
| [T07 — PostgreSQL access stores](tickets/T07-postgres-access-store.md) | Completed | E2 T05, E2 T06 |

Independent tickets may run in parallel when file ownership is clear.

## Verification record

T01–T07: `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (154 passed: Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15); without PostgreSQL 138 passed and 16 inconclusive. `bash scripts/test-release-scripts.sh` (15 OK), `bash scripts/verify-package-metadata.sh` (7 packages), `bash scripts/pack.sh --skip-tests` plus `smoke-package-consumption.sh` (7 packages). Post-ticket reviews completed; all high-confidence findings fixed with regression tests (see ticket Results).
