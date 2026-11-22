# T04 — PostgreSQL event store

Epic: [E15 — Public event stream and integrations](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Events.Stores.Postgres`: `PostgresEventStore` implementing `IEventLog` and `IEventSubscriptionStore` (event log with
per-scope head rows, follower checkpoints, subscriptions, delivery state, dead letters), `PostgresEventMigrations` and
`AddPostgresEventStores`.

## Dependencies

E15 T01.

## Acceptance criteria

- Appends lock the scope head row, skip existing event ids and assign gapless sequences; concurrent appenders never duplicate or
  skip a sequence.
- Delivery claims use `FOR UPDATE SKIP LOCKED` over a due index; concurrent claimers never share a live lease.
- `tf_key_v1` keys, explicit `format_version` columns, parameterized SQL, append-only migration catalog.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Events/TicketFlow.Events.Stores.Postgres.Tests -c Release` against an isolated
  `postgres:18` container.

## Risks and boundaries

Head-row locking serializes appends per workspace (the price of a gapless sequence).

## Results

Implemented `PostgresEventStore` (head-row locked gapless appends with `unnest` inserts, follower checkpoints, advisory-locked
subscription limit, `FOR UPDATE SKIP LOCKED` claims joined to the head, fenced completion/failure/dead-letter, replay, retention),
`PostgresEventMigrations` (`ticketflow.events` v1) and `AddPostgresEventStores`. Tests: `PostgresEventStoreTests` (store contracts with two
independent data sources, concurrent claims ×3, 6 concurrent appenders gapless, bounded keys with 256-unit identifiers) plus the linked
feature scenarios running on PostgreSQL.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with concurrent E11a/E16/E17 work in the tree).
- `dotnet test tests/Events/TicketFlow.Events.Tests -c Release` — 26 passed, 0 failed.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Events/TicketFlow.Events.Stores.Postgres.Tests -c Release` — 24 passed, 0 failed, against an
  isolated `postgres:18` container (`tf-pg-e15`, port 55915); includes the linked projection and delivery scenarios on PostgreSQL.
- `dotnet test tests/Events/TicketFlow.Events.Webhooks.Tests -c Release` — 5 passed.
- `dotnet test tests/Integrations/TicketFlow.Integrations.PostHog.Tests -c Release` — 4 passed.
- `dotnet test tests/Integrations/TicketFlow.Integrations.WalFlow.Sinks.Tests -c Release` — 3 passed.
- `bash scripts/verify-package-metadata.sh` — fails only on a concurrent E17 project (`src/Mcp/TicketFlow.Mcp`) not yet in the solution.

Post-ticket parallel reviews have not been run yet.
