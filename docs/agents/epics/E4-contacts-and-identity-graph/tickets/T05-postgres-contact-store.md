# T05 — PostgreSQL contact store

Epic: [E4 — Contacts and identity graph](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Contacts.Stores.Postgres`: `PostgresContactStore` implementing every contact store contract with an append-only migration
catalog, `tf_key_v1` bounded keys, a partial unique index for verified identities, indexed keyset search with translated Access
filters and a per-scope gapless change sequence.

## Dependencies

E4 T02, E4 T04.

## Acceptance criteria

- Shared store contract passes against PostgreSQL with independent clients; concurrent verified-identity claims admit one winner.
- Search uses `(scope_key, name_folded, contact_id)` and `(scope_key, kind, value, contact_id)` indexes; no unbounded scans.

## Verification

- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Explicit schema creation only (`InitializeAsync`). Rows that fail to decode throw.

## Results

Implemented `TicketFlow.Contacts.Stores.Postgres`:

- `PostgresContactMigrations.Catalog` (`ticketflow.contacts` v1): contacts, identities (partial unique index on verified `identity_key`), attribute definitions, merges, change heads/changes, audit; `tf_key_v1` keys; `"C"` collation for sort/search columns.
- `PostgresContactStore`: one-transaction batches (ordered row locks, identity rows replaced after contact rows, `ON CONFLICT ... WHERE verified` for verified uniqueness, recursive tombstone cascade), keyset search with `PostgresAccessFilterTranslator` over a lateral constant row (no team/inbox/assignee, unknown attributes), gapless per-scope sequence via a head row, strict decoding.
- `AddPostgresContactStores(dataSource)`; explicit `InitializeAsync`.
- Tests: `PostgresContactStoreTests` (shared contract with independent clients, concurrent identity claims, gapless outbox across clients, idempotent init, 256-unit identifiers, batch audit atomicity, decode failure, index-backed plans, end-to-end service on PostgreSQL).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E3 projects).
- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` — 72 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55474;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 12 passed, 0 failed.
- `bash scripts/verify-package-metadata.sh` — Validated 13 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
