# T05 — PostgreSQL automation store

Epic: [E10 — Automation and macros](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Automation.Stores.Postgres`: `PostgresAutomationStore` implementing every automation store contract, an append-only
`ticketflow.automation` migration catalog and `AddPostgresAutomationStores`.

## Dependencies

E10 T02.

## Acceptance criteria

- `tf_key_v1` keys plus raw-column comparison, `format_version` on every row, parameterized SQL, capacity checks under advisory
  locks, `FOR UPDATE SKIP LOCKED` schedule claims; passes the shared contract with two independent data sources.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Schema is created only by `InitializeAsync`.

## Results

Implemented `PostgresAutomationStore`, the `ticketflow.automation` catalog and `AddPostgresAutomationStores`. Tests: `PostgresAutomationStoreTests` (10: every contract with independent clients, concurrent claims/begins, 256-unit identifiers, undecodable rows, idempotent initialization).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E7/E13 work in the tree).
- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 35 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55710;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10`) — 10 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — Validated 47 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
