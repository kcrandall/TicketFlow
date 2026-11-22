# T05 — PostgreSQL conversation store

Epic: [E5 — Conversations and timeline](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Conversations.Stores.Postgres`: `PostgresConversationStore` implementing the store, outbox and attribute definition
contracts, `PostgresConversationMigrations` (append-only catalog), `AddPostgresConversationStores`.

## Dependencies

E5 T02.

## Acceptance criteria

- `tf_key_v1` keys, explicit `format_version` and numeric discriminators, parameterized SQL, decode failures throw.
- Revision-fenced conversation row update, event inserts, message rows (unique external id index) and outbox fan-out commit in one
  transaction; list queries keyset-page over `(scope_key, …, last_activity_at, conversation_id)` indexes with the translated
  `AccessFilter` in the statement.
- Outbox claims use `FOR UPDATE SKIP LOCKED` with fences and leases.
- The shared contract passes with two independent data sources, plus concurrency tests.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Tables live in the data source's schema and are created only by `InitializeAsync`.

## Results

Implemented `PostgresConversationStore` (partials: reads/lists, writes, outbox, definitions), `PostgresConversationMigrations` (catalog `ticketflow.conversations` v1), `AddPostgresConversationStores`. Tests: `PostgresConversationStoreTests` (15): the shared contract with two data sources, 16 concurrent appenders, 10 concurrent duplicate inbound writers, 8 concurrent claimers, 20 concurrent service replies, long identifiers, decode failures, idempotent initialization and an index-plan check.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E12 work in the tree).
- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` — 36 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55505;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 15 passed, 0 failed.
- Regression after the Access built-in change: `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` (95 passed), `tests/Contacts/TicketFlow.Contacts.Tests` (72 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (42 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E12's in-progress `src/HelpCenter/TicketFlow.HelpCenter` project not yet in the solution (not an E5 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
