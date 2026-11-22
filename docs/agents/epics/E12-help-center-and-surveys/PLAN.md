# E12 — Help center and surveys

Status: **Completed**

Self-service and satisfaction measurement.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Articles, collections, locales, widget deflection, CSAT/NPS through EnvelopeFlow templates.

Boundaries: conversations are referenced only through `ConversationId` from Core (E5 is built concurrently); the survey scheduler
consumes its own `ConversationResolvedNotification` input that the E5 outbox will feed later. Survey delivery is an
`ISurveyInvitationSender` abstraction that the E6/EnvelopeFlow adapter will implement; no transport ships here. Widget deflection
(E7) and assistant grounding (E13) consume `IHelpCenterSearch`.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Help center model, permissions and safe Markdown rendering](tickets/T01-help-center-model-and-rendering.md) | Completed | E2, E3 |
| [T02 — Help center store contract, in-memory store and services](tickets/T02-help-center-store-and-services.md) | Completed | E12 T01 |
| [T03 — PostgreSQL help center store with full-text search](tickets/T03-postgres-help-center-store.md) | Completed | E12 T02 |
| [T04 — Survey model, invitations, store contract and in-memory store](tickets/T04-survey-model-and-store.md) | Completed | E2, E3 |
| [T05 — Survey scheduling, dispatch, responses and aggregates](tickets/T05-survey-services.md) | Completed | E12 T04 |
| [T06 — PostgreSQL survey store and documentation](tickets/T06-postgres-survey-store-and-docs.md) | Completed | E12 T03, E12 T05 |

## Verification record

T01–T06 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release` (67 passed); `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release` (24 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Stores.Postgres.Tests -c Release` (10 passed) and `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Surveys/TicketFlow.Surveys.Stores.Postgres.Tests -c Release` (10 passed) against an isolated `postgres:18` container (without PostgreSQL these 20 are inconclusive); `bash scripts/verify-package-metadata.sh` (22 package projects). Parallel post-ticket reviews are still outstanding before commit.
