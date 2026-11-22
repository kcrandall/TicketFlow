# E7 — Live chat and realtime

Status: **Completed** (T08 WalFlow change-capture fan-out deferred as a follow-up)

Website live chat and live agent updates.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Widget client, WebSocket gateway, visitor sessions, identity verification, presence/typing, SignalR fan-out via WalFlow.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Realtime abstractions and Access-aware fan-out](tickets/T01-realtime-fanout.md) | Completed | E2, E3, E5, E8 |
| [T02 — Live chat widgets, visitor sessions and identity verification](tickets/T02-livechat-widgets-and-sessions.md) | Completed | E6, E7 T01 |
| [T03 — Live chat channel and visitor service](tickets/T03-livechat-channel-and-visitor-service.md) | Completed | E7 T01, T02, E12 |
| [T04 — PostgreSQL live chat stores](tickets/T04-livechat-postgres-stores.md) | Completed | E7 T02, T03 |
| [T05 — SignalR hubs](tickets/T05-signalr-hosting.md) | Completed | E7 T01–T03 |
| [T06 — Web widget client](tickets/T06-web-widget.md) | Completed | E7 T05 |
| [T07 — Documentation and WalFlow evaluation](tickets/T07-docs-and-walflow-evaluation.md) | Completed | E7 T01–T06 |
| [T08 — WalFlow change-capture fan-out (follow-up)](tickets/T08-walflow-change-capture-fanout.md) | Planned (follow-up) | E7 T01, E5 row contract |

## Verification record

T01–T07 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors, with concurrent E10/E13 and fix-up work in the
tree); `dotnet test tests/Realtime/TicketFlow.Realtime.Tests -c Release` (8 passed); `tests/Channels/TicketFlow.Channels.LiveChat.Tests`
(24 passed); `tests/Hosting/TicketFlow.Realtime.SignalR.Tests` (3 passed); `tests/Channels/TicketFlow.Channels.Tests` (42 passed,
unchanged); `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55707;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Stores.Postgres.Tests -c Release`
against an isolated `postgres:18` container (4 passed); `clients/widget`: `npx tsc --noEmit`, `npx vitest run` (12 passed), `npm run build`;
`bash scripts/verify-package-metadata.sh` (47 package projects validated). The outcome's "SignalR fan-out via WalFlow" is delivered as
SignalR fan-out over the outbox and routing observers; the WalFlow source is the T08 follow-up. Parallel post-ticket reviews are still
outstanding before commit.
