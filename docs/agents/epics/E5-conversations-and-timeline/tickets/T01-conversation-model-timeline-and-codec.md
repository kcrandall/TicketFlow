# T01 — Conversation model, timeline events, codec and permissions

Epic: [E5 — Conversations and timeline](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Conversations.Abstractions`: `Conversation` (inbox, contact, `ConversationStatus` union with `Snoozed(until)`, priority,
assignee, team, labels, typed custom attributes with sensitive definitions, participants, opaque channel thread reference,
activity/SLA markers, timeline head, contact-merge redirects), the timeline event union (`MessageEvent`, `NoteEvent`,
`AssignmentChanged`, `TeamChanged`, `StatusChanged`, `PriorityChanged`, `LabelsChanged`, `AttributeChanged`, `ParticipantChanged`,
`ContactChanged`, `SystemEvent`, `ExtensionEvent`), `ConversationJsonCodec` with explicit versioned discriminators, field paths,
the Access resource shape and the permission set.

## Dependencies

E2 (Access abstractions), E4 (contact identifiers and change events for the merge hook).

## Acceptance criteria

- Bodies are bounded to 64 KiB of UTF-8, well-formed UTF-16 and free of control characters other than tab/CR/LF.
- Every persisted shape (events, status, attribute values, redirects, outbox payloads) uses `{"v":1,...}` envelopes with explicit
  discriminators; decoding re-validates and rejects unknown versions, discriminators and properties. Unions are never serialized.
- `conversation:manage` is an Access built-in held by Agent (and so Supervisor/Admin/Owner); `conversation:create`,
  `conversation:ingest` and `conversation-attribute:manage` are registered by `AddConversations()`. Existing built-ins
  (`conversation:read|reply|assign|resolve|delete`, `note:read|write`) are reused.

## Verification

- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` (model and codec tests).

## Risks and boundaries

Channel thread and external message identifiers are correlation evidence only and may contain addresses, so they are maskable
(`conversation.channel`). Contacts are referenced by identifier only; no contact PII is copied into conversations.

## Results

Implemented in `TicketFlow.Conversations.Abstractions`:

- `Conversation`/`StoredConversation` (canonical labels/participants, typed attributes, `ChannelThreadRef`, SLA markers, timeline head, `ContactRedirect`s, `With` using `Optional<T>`), `ConversationStatus` union with `FromKind`, `ConversationPriority`.
- Timeline: `TimelineEvent` and the `ConversationEvent` union (`MessageEvent`, `NoteEvent`, `AssignmentChanged`, `TeamChanged`, `StatusChanged`, `PriorityChanged`, `LabelsChanged`, `AttributeChanged`, `ParticipantChanged`, `ContactChanged`, `SystemEvent`, `ExtensionEvent`), `ConversationParty` union, `MessageDelivery` with forward-only transitions, `AttachmentRef`, 64 KiB UTF-8 body validation.
- `ConversationJsonCodec` (events, attributes, condition attributes, redirects, outbox change payloads; strict decoding).
- `ConversationPermissions` (registers `conversation:create`, `conversation:ingest`, `conversation-attribute:manage`), `ConversationFieldPaths`, `ConversationAccess`.
- Access change: `conversation:manage` added to `AccessPermissions` and the Agent built-in permission set (so Supervisor/Admin/Owner hold it too).
- Tests: `ModelAndCodecTests` (9).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E12 work in the tree).
- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` — 36 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55505;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 15 passed, 0 failed.
- Regression after the Access built-in change: `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` (95 passed), `tests/Contacts/TicketFlow.Contacts.Tests` (72 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (42 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E12's in-progress `src/HelpCenter/TicketFlow.HelpCenter` project not yet in the solution (not an E5 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
