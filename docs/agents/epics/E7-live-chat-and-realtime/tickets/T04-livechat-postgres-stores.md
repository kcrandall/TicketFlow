# T04 — PostgreSQL live chat stores

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Channels.LiveChat.Stores.Postgres`: widget configurations (versioned JSON), sessions (token hashes only), session ↔
conversation links and visitor read cursors, behind the shared store contract fixture (`TicketFlow.Channels.LiveChat.StoreContracts`)
also run against the in-memory store.

## Dependencies

E7 T02, T03.

## Acceptance criteria

- `tf_key_v1` keys with raw-column comparison, `format_version`, append-only `ticketflow.livechat` catalog, CAS writes, bounded links
  per session, monotonic cursors, partition-wide session lookup returning the full scope.

## Verification

- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Expired sessions are pruned by an explicit call (no hosted worker until E11).

## Results

Implemented `TicketFlow.Channels.LiveChat.Stores.Postgres` (`PostgresLiveChatStore`, `PostgresLiveChatMigrations` catalog
`ticketflow.livechat` v1, `AddPostgresLiveChatStores`) and the shared fixture `tests/Channels/TicketFlow.Channels.LiveChat.StoreContracts`
(also run against `InMemoryLiveChatStore`).

- `docker run -d --rm --name tf-pg-e7 -e POSTGRES_PASSWORD=pw -p 127.0.0.1:55707:5432 postgres:18`, then
  `TICKETFLOW_TEST_POSTGRES="Host=127.0.0.1;Port=55707;Username=postgres;Password=pw;Database=postgres" TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Stores.Postgres.Tests -c Release`
  — 4 passed, 0 failed (widget and session contracts, concurrent links and CAS, token-hash-only storage and idempotent migrations).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
