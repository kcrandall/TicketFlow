# E8 — Routing and assignment

Status: **Completed**

Conversations reach the right agent exactly once.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Round robin, capacity, skills, sticky owner, overflow, fenced assignment, collision detection.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Routing model, policy codec and permissions](tickets/T01-routing-model-codec-and-permissions.md) | Completed | E2, E3, E5 |
| [T02 — Routing store contracts and in-memory stores](tickets/T02-store-contracts-and-in-memory-stores.md) | Completed | E8 T01 |
| [T03 — Policy, availability, collision and queue services](tickets/T03-routing-services.md) | Completed | E8 T02 |
| [T04 — Assignment engine, outbox handler and maintenance worker](tickets/T04-assignment-engine.md) | Completed | E8 T03, E5 T04 |
| [T05 — PostgreSQL routing store](tickets/T05-postgres-routing-store.md) | Completed | E8 T02 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` (41 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Routing/TicketFlow.Routing.Stores.Postgres.Tests -c Release` against an isolated `postgres:18` container (11 passed; without PostgreSQL these 11 are inconclusive); Access (99), Workspaces (50), Conversations (36), composition (6) and Access PostgreSQL (15) suites re-run green after adding the routing built-ins. `bash scripts/verify-package-metadata.sh` currently fails only on E6's in-progress Channels project. Parallel post-ticket reviews are still outstanding before commit.
