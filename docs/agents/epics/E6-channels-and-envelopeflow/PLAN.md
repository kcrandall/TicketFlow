# E6 — Channels and EnvelopeFlow integration

Status: **Completed**

Conversations receive and reply across channels: a provider-neutral channel contract, an inbound pipeline, an outbound
dispatcher on the E5 outbox, delivery status, reply windows, a signed-webhook API channel and the EnvelopeFlow adapter
(email, SMS, WhatsApp, Slack) published on nuget.org (`0.1.0-alpha.3.1`).

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Channel contract, EnvelopeFlow inbound/outbound adapter (email, SMS, WhatsApp, Slack/Teams), delivery status.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Channel contracts](tickets/T01-channel-contracts.md) | Completed | E3, E4, E5 |
| [T02 — Channel state stores (in-memory and PostgreSQL)](tickets/T02-channel-state-stores.md) | Completed | E6 T01 |
| [T03 — Inbound pipeline, outbound dispatcher, delivery status and reply windows](tickets/T03-pipeline-dispatch-and-reply-windows.md) | Completed | E6 T01, T02 |
| [T04 — Generic API channel with signed webhooks](tickets/T04-api-channel.md) | Completed | E6 T03 |
| [T05 — EnvelopeFlow integration](tickets/T05-envelopeflow-integration.md) | Completed | E6 T03, E12 |
| [T06 — Survey resolution bridge and documentation](tickets/T06-survey-bridge-and-docs.md) | Completed | E6 T03, E12 |
| [T07 — IMAP mailbox channel](tickets/T07-mailbox-channel.md) | Completed | E6 T03 |

## Verification record

T01–T06 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors, including concurrent E8/E9 and fix-up work in
the tree); `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release` (35 passed); `tests/Channels/TicketFlow.Channels.Api.Tests`
(10 passed); `tests/Integrations/TicketFlow.Integrations.EnvelopeFlow.Tests` (9 passed); `tests/Integrations/TicketFlow.Integrations.Surveys.Tests`
(3 passed); `tests/TicketFlow.Tests` (6 passed); `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55606;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Channels/TicketFlow.Channels.Stores.Postgres.Tests -c Release`
against an isolated `postgres:18` container (6 passed); `bash scripts/verify-package-metadata.sh` (34 package projects validated).
EnvelopeFlow packages restored from nuget.org at `0.1.0-alpha.3.1`. Parallel post-ticket reviews are still outstanding before commit.

Review fixes (2026-11-22, after ee963dc): address-hijack ownership validator, per-contact thread lookup, `Retry-After` honoured through
the additive E5 `ConversationOutboxRetryException`, bounded API response body, clamped receive times, retryable delivery conflicts,
abstraction-only EnvelopeFlow adapter plus `TicketFlow.Integrations.EnvelopeFlow.Surveys`, once-per-kind registration, non-nullable
conflict revisions. `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); Channels (42), Channels.Api (11),
Integrations.EnvelopeFlow (12), Integrations.Surveys (3), Conversations (46) and Channels PostgreSQL (6, isolated `postgres:18` on
port 55616) passed. `scripts/verify-package-metadata.sh` currently fails only on E13's in-progress `src/Assist/TicketFlow.Assist`.
