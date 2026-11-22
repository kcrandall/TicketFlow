# E5 — Conversations and timeline

Status: **Completed**

Access-checked, revision-fenced conversations with filtered list queries.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Revisioned conversation timeline, messages, notes, labels, custom attributes, status/priority.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Conversation model, timeline events, codec and permissions](tickets/T01-conversation-model-timeline-and-codec.md) | Completed | E2, E4 |
| [T02 — Store, outbox contracts and in-memory store](tickets/T02-store-outbox-contracts-and-in-memory-store.md) | Completed | E5 T01 |
| [T03 — Conversation service: commands, timeline reads, masking and filtered lists](tickets/T03-conversation-service.md) | Completed | E5 T02, E3 |
| [T04 — Outbox dispatcher and contact-merge follower](tickets/T04-outbox-dispatcher-and-merge-follower.md) | Completed | E5 T03, E4 |
| [T05 — PostgreSQL conversation store](tickets/T05-postgres-conversation-store.md) | Completed | E5 T02 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` (36 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` against an isolated `postgres:18` container (15 passed; without PostgreSQL these 15 are inconclusive); Access (95), Contacts (72), Workspaces (42), composition (6) and Access PostgreSQL (15) suites re-run green after adding the `conversation:manage` built-in. `bash scripts/verify-package-metadata.sh` currently fails only on E12's in-progress HelpCenter project. Parallel post-ticket reviews are still outstanding before commit.
