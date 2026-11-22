# T02 — Store, outbox contracts and in-memory store

Epic: [E5 — Conversations and timeline](../PLAN.md)

Status: **Completed**

## Goal

`IConversationStore` (atomic revision-fenced write of conversation + timeline events + message rows + outbox entries; gapless
per-conversation sequences; inbound idempotency on (scope, inbox, external message id); fenced delivery-status updates; filtered
keyset list; thread lookup; delete), `IConversationOutbox` (durable consumers, per-consumer leased claims with fences, ack,
release-for-retry, dead-letter, per-conversation ordering, pruning), `IConversationAttributeDefinitionStore`, the development
in-memory implementation and the shared `TicketFlow.Conversations.StoreContracts` fixture.

## Dependencies

E5 T01.

## Acceptance criteria

- A write whose expected revision or timeline head does not match writes nothing; sequences are contiguous from 1.
- A duplicate external message id returns `ConversationDuplicateMessage` naming the existing message; nothing is written.
- Claims are fenced (a re-claim after lease expiry invalidates the previous fence); a consumer never holds two pending entries of
  one conversation at once; dead-lettering unblocks the conversation.
- List queries apply the `AccessFilter` inside the query over `ConversationAccess.Resource`.

## Verification

- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` (contract against the in-memory store).

## Risks and boundaries

The in-memory store is a process-local development example (single lock, bounded) with no durability.

## Results

Implemented: `IConversationStore`, `ConversationWrite`, `MessageDeliveryWrite`, `StoredMessage`, `ConversationWriteResult`/`MessageDeliveryWriteResult` unions, `ConversationListQuery`/cursor/page; `IConversationOutbox` with `ConversationChange`, `ConversationOutboxEntry`, `ConversationOutboxClaim`; `IConversationAttributeDefinitionStore`; `InMemoryConversationStore` and `InMemoryConversationAttributeDefinitionStore`; shared fixture `tests/Conversations/TicketFlow.Conversations.StoreContracts` (writes, timeline/messages, delivery, list/filter parity, delete, concurrent appends, concurrent duplicate inbound, outbox, definitions). Tests: `InMemoryConversationStoreTests` (10).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E12 work in the tree).
- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` — 36 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55505;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 15 passed, 0 failed.
- Regression after the Access built-in change: `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` (95 passed), `tests/Contacts/TicketFlow.Contacts.Tests` (72 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (42 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E12's in-progress `src/HelpCenter/TicketFlow.HelpCenter` project not yet in the solution (not an E5 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
