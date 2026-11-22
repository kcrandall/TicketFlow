# T02 — Channel state stores (in-memory and PostgreSQL)

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Completed**

## Goal

Durable channel state behind four store contracts: `IChannelEndpointStore` (binding configuration → inbox, optional
partition-unique endpoint address for adapter lookup), `IChannelConversationStateStore` (recipient, thread id, subject, last
incoming time per conversation), `IChannelThreadStore` (bounded Message-Id/References chain per conversation for email threading
and inbound correlation) and `IChannelDispatchStore` (dispatch attempts and outcomes). Development in-memory implementation in
`TicketFlow.Channels`; `TicketFlow.Channels.Stores.Postgres` with an append-only `ticketflow.channels` migration catalog and
`tf_key_v1` keys. Shared contract fixture `tests/Channels/TicketFlow.Channels.StoreContracts`.

## Dependencies

E6 T01.

## Acceptance criteria

- Scope confinement, revision-fenced writes (create at 1, compare-and-set), address uniqueness per partition and kind, bounded
  reference chains (oldest pruned), idempotent reference inserts, capacity limits checked atomically.
- The same contract fixture passes against in-memory and PostgreSQL (two independent data sources).

## Verification

- `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release`.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Channels/TicketFlow.Channels.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Channel state stores addresses (recipient, Message-Ids) as correlation data; never logged, never returned through agent views.

## Results

Implemented the four store contracts, `InMemoryChannelStore`, `PostgresChannelStore` (`ticketflow.channels` catalog: endpoints with a
partial unique address index, conversation states, references with advisory-locked pruning, dispatches) and the shared fixture
`ChannelStoreContract` (endpoints, conversation state, threads, dispatches; concurrent creates and adds).

- `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release` — 35 passed (4 in-memory contract tests).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55606;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Channels/TicketFlow.Channels.Stores.Postgres.Tests -c Release` against an isolated `postgres:18`
  container — 6 passed, 0 failed (4 contracts, decode failure, PostgreSQL round trip). Without PostgreSQL they are inconclusive.

Review fixes (2026-11-22): `ChannelWriteConflict(long)`; CAS writes of absent state/dispatch return not found; `IChannelEndpointStore.ListByAddressPrefixAsync`; per-contact `IChannelConversationStateStore.FindByThreadAsync` (state gains `Contact`; PostgreSQL migration 2 adds `contact_id`, `thread_key` and an address prefix index). Contract tests extended (prefix listing, thread-by-contact, not-found CAS); Postgres 6 passed against an isolated `postgres:18` container (port 55616).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
