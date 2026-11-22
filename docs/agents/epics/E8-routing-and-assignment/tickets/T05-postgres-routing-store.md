# T05 — PostgreSQL routing store

Epic: [E8 — Routing and assignment](../PLAN.md)

Status: **Completed**

## Goal

`PostgresRoutingStore` implementing every routing store contract with the append-only `ticketflow.routing` migration catalog,
`tf_key_v1` keys, `format_version` columns, row locks for cursor and agent load anchors (taken in a global order), `FOR UPDATE SKIP
LOCKED` queue/reassignment claims and the translated Access filter in queue lists.

## Dependencies

E8 T02.

## Acceptance criteria

- The shared contract fixture passes against two independent data sources, including racing reservations.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Routing/TicketFlow.Routing.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Lease and presence times come from the store `TimeProvider`; clients need synchronized clocks.

## Results

`PostgresRoutingStore` with the `ticketflow.routing` migration catalog (cursor → load-anchor lock order, `SKIP LOCKED` claims, single fence sequence, translated Access filters), `AddPostgresRoutingStores`; `PostgresRoutingStoreTests` (8) and `PostgresRoutingEngineTests` (3, including the concurrency scenarios).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E6/E9 work in the tree).
- `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` — 41 passed, 0 failed (model/codec 18, in-memory store contracts 6, services 5, engine 10, concurrency 2).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55608;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Routing/TicketFlow.Routing.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 11 passed, 0 failed (6 store contracts across two data sources, migration/bounded keys, undecodable rows, 3 engine scenarios); both suites re-run 5 times without failures.
- Regression after the Access built-in additions: `tests/Access/TicketFlow.Access.Tests` (99 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (50 passed), `tests/Conversations/TicketFlow.Conversations.Tests` (36 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E6's in-progress `src/Channels/TicketFlow.Channels` project not yet in the solution (not an E8 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
