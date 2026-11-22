# T02 — Support endpoints: contacts, conversations, routing, SLA and live chat

Epic: [E11 — Hosting and agent console](../PLAN.md)

Status: **Completed**

## Goal

Endpoint groups `MapTicketFlowContacts()` (search with opaque cursors, CRUD with edits, reveal, merge/unmerge, NDJSON export
streaming, attribute definitions), `MapTicketFlowConversations()` (list with filters and keyset cursor, start, view, timeline, reply,
note, assign, team, status, snooze, priority, labels, attributes, delete, attribute definitions), `MapTicketFlowRouting()` (own and
managed agent status, heartbeat, agent settings, queue, presence, reply locks, routing policies), `MapTicketFlowSla()` (conversation
SLA status, risk list, SLA policies) and `MapTicketFlowLiveChat()` (widget configurations, channel endpoints, agent visitor view).

## Dependencies

E11 T01.

## Acceptance criteria

- Masked values stay masked: DTOs carry `masked`/`revealable` flags and never the removed values; reveal is an explicit audited call.
- Export streams `application/x-ndjson` from the service's bounded async enumerable without buffering the result set.
- Conversation commands accept an optional `If-Match`; conflicts return 412 (fenced) or 409.

## Verification

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release`.

## Risks and boundaries

Timeline events are explicit per-kind DTOs; extension payloads pass through as the owning feature's JSON object.

## Results

Implemented contacts (search, CRUD with typed edits, reveal with `no-store`, merge/unmerge, NDJSON export with the audit id header,
attribute definitions), conversations (filtered keyset lists incl. `assignee=me`, start, view, explicit per-kind timeline DTOs, fenced
commands, delete, attribute definitions), routing (agent status/heartbeat/settings, queue with opaque cursors, presence, reply locks with
409 `reply-lock-held`, policies as codec JSON), SLA (status, risk list, policies) and live chat (widgets, channel endpoints, visitor view).
Tests: `ContactsSearchUpdateRevealAndExportStreams`, `ConversationLifecycleTimelineAndFencedCommands`, `RoutingStatusPresenceAndSlaRiskEndpoints`.

Verification (2026-11-22):

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release` — 18 passed, 0 failed.
