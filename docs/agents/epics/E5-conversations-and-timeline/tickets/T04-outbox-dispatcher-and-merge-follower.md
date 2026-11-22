# T04 — Outbox dispatcher and contact-merge follower

Epic: [E5 — Conversations and timeline](../PLAN.md)

Status: **Completed**

## Goal

`ConversationOutboxDispatcher` running registered `IConversationOutboxHandler`s (claim, handle, ack; exponential retry; dead-letter
after a bounded number of attempts) and `ConversationContactMergeFollower` (an `IContactChangeObserver` plus durable catch-up over
`IContactStore.ReadChangesAsync`) that re-points conversations of merged contacts and reverses on unmerge, idempotently.

## Dependencies

E5 T03, E4 (contact change events).

## Acceptance criteria

- Merge re-points every conversation of the source to the target with a `ContactChanged` event; unmerge restores exactly the
  conversations re-pointed by that merge; replaying either is a no-op.
- Handler failures release the claim for retry with backoff; the last allowed attempt dead-letters.

## Verification

- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release`.

## Risks and boundaries

No hosted service is shipped; hosting (E11) runs the dispatcher loop.

## Results

Implemented `ConversationOutboxDispatcher` (+ `IConversationOutboxHandler`, `ConversationOutboxDispatcherOptions`, `ConversationOutboxDispatchResult`) and `ConversationContactMergeFollower` (observer + `CatchUpAsync`; skips merges whose record was removed so replay is a no-op). Tests: `ContactMergeFollowerTests` (2, including chained merges), `OutboxDispatcherTests` (2).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E12 work in the tree).
- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` — 36 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55505;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 15 passed, 0 failed.
- Regression after the Access built-in change: `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` (95 passed), `tests/Contacts/TicketFlow.Contacts.Tests` (72 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (42 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E12's in-progress `src/HelpCenter/TicketFlow.HelpCenter` project not yet in the solution (not an E5 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
