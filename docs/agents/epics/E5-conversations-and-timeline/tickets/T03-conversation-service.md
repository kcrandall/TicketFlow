# T03 — Conversation service: commands, timeline reads, masking and filtered lists

Epic: [E5 — Conversations and timeline](../PLAN.md)

Status: **Completed**

## Goal

`IConversationService`: start, incoming message (idempotent), reply (pending outbound message + outbox dispatch entry), note,
assign/unassign, team, status/snooze, priority, labels, attributes, participants, delivery status (idempotent, monotonic),
extension events and delete — each returning `ConversationCommandResult = Applied | NotFound | Conflict | Refused`; get, timeline
and list reads with masking; `IConversationAttributeDefinitionService`.

## Dependencies

E5 T02, E3 (inbox/team existence), E4 (contact existence).

## Acceptance criteria

- Every read and mutation authorizes through `IAccessEvaluator`; lists use `IAccessQueryPlanner` filters inside the store query.
- Notes are omitted from timelines unless `note:read`; LightAgent can note but not reply.
- Sensitive attributes and channel identifiers are masked in views and timeline events; a sensitive attribute with no policy fails
  closed.
- Commands without an expected revision retry store conflicts, so concurrent replies all apply with gapless sequences.

## Verification

- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release`.

## Risks and boundaries

Automation and system principals cannot hold grants; channel ingestion, SLA and automation workers act through grant-holding API
client principals (or trusted store access for internal followers).

## Results

Implemented `ConversationService` (all commands with the read → fence → authorize → decide → authorize-result → atomic write pipeline and conflict retry; reads with masking; filtered keyset lists with query-bound cursors), `ConversationMasking`, `ConversationAttributeDefinitionService`, `ConversationsOptions`, `AddConversations`/`AddInMemoryConversationStores`. Tests: `ConversationServiceTests` (13), including 24 concurrent replies with gapless sequences, LightAgent note-not-reply, notes hidden without `note:read`, sensitive attribute and channel masking, team-scoped list filtering.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E12 work in the tree).
- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` — 36 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55505;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 15 passed, 0 failed.
- Regression after the Access built-in change: `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` (95 passed), `tests/Contacts/TicketFlow.Contacts.Tests` (72 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (42 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E12's in-progress `src/HelpCenter/TicketFlow.HelpCenter` project not yet in the solution (not an E5 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
