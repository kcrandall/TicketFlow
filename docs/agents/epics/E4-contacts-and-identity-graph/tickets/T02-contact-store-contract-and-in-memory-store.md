# T02 — Contact store contract and in-memory store

Epic: [E4 — Contacts and identity graph](../PLAN.md)

Status: **Completed**

## Goal

`IContactStore` (atomic revision-fenced write batches, verified-identity uniqueness, identity lookup, Access-filtered keyset search,
merge records, per-scope change outbox), `IContactAttributeDefinitionStore`, `IContactAuditSink`, development in-memory
implementations and the shared `TicketFlow.Contacts.StoreContracts` fixture.

## Dependencies

E4 T01.

## Acceptance criteria

- Batches apply all-or-nothing; creates start at revision 1; updates/deletes compare-and-set; verified identities are unique per
  (scope, kind, namespace, normalized value) while claimed identities may repeat.
- Search is bounded (1-100 per page), keyset-paged, scope-confined and filtered by an `AccessFilter` before paging.
- Change events are appended in the write transaction with a gapless per-scope sequence.

## Verification

- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` (in-memory store contract).

## Risks and boundaries

In-memory stores are bounded, process-local development examples with no durability.

## Results

Implemented:

- `IContactStore` (atomic `ContactWriteBatch`, `ContactWriteResult` union, identity lookup, `AccessFilter` keyset search, merge records, per-scope gapless outbox), `IContactAttributeDefinitionStore` (`AttributeDefinitionWriteResult` union), `IContactAuditSink`.
- `InMemoryContactStore` (also the audit sink) and `InMemoryContactAttributeDefinitionStore`: bounded, single-lock development examples.
- Shared fixture `tests/Contacts/TicketFlow.Contacts.StoreContracts` (`ContactStoreContract`: contacts, merge records and delete cascade, search, outbox, concurrent verified identity claims, definitions with capacity), run by `InMemoryContactStoreTests`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E3 projects).
- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` — 72 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55474;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 12 passed, 0 failed.
- `bash scripts/verify-package-metadata.sh` — Validated 13 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
