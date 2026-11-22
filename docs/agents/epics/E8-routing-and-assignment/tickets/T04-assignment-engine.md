# T04 — Assignment engine, outbox handler and maintenance worker

Epic: [E8 — Routing and assignment](../PLAN.md)

Status: **Completed**

## Goal

`RoutingEngine` reconciles a conversation into the queue/assignment projections and routes it: policy by inbox
`RoutingPolicyId`, candidate pool (available agents, team, skills), Access eligibility (`conversation:reply` via one planned filter
per agent, cached per run), atomic reservation, fenced `AssignAsync(expectedRevision)` as a grant-holding API client, bounded
retries. `RoutingOutboxHandler` (`IConversationOutboxHandler`, consumer `routing`) drives it from E5 changes;
`RoutingMaintenanceWorker` retries due queue entries, applies overflow and max-wait, expires presence and reassigns offline agents'
conversations per policy.

## Dependencies

E8 T03, E5 T04.

## Acceptance criteria

- Concurrency tests: N workers racing assign each conversation exactly once, capacity never exceeded, round-robin distribution
  fair (±1).
- Strategies: round robin, least busy (capacity-aware), skills, sticky owner with fallback, manual; overflow to team and inbox.

## Verification

- `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release`.

## Risks and boundaries

The engine principal needs `conversation:assign` in each workspace; without it routing refuses and entries stay queued.

## Results

`RoutingEngine` (reconcile + policy/pool/Access eligibility via one cached plan per agent + atomic reservation + fenced `AssignAsync`), `RoutingOutboxHandler` (consumer `routing`), `RoutingMaintenanceWorker` (presence expiry, offline reassignment, due queue claims, overflow, max wait, presence prune), `AddRouting`/`AddInMemoryRoutingStores`; `EngineTests` (10) and `ConcurrencyTests` (2: 8 engines over 48 conversations assign each exactly once with round-robin spread ≤ 1; engines + dispatcher + maintenance racing end exactly at capacity).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E6/E9 work in the tree).
- `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` — 41 passed, 0 failed (model/codec 18, in-memory store contracts 6, services 5, engine 10, concurrency 2).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55608;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Routing/TicketFlow.Routing.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 11 passed, 0 failed (6 store contracts across two data sources, migration/bounded keys, undecodable rows, 3 engine scenarios); both suites re-run 5 times without failures.
- Regression after the Access built-in additions: `tests/Access/TicketFlow.Access.Tests` (99 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (50 passed), `tests/Conversations/TicketFlow.Conversations.Tests` (36 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E6's in-progress `src/Channels/TicketFlow.Channels` project not yet in the solution (not an E8 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
