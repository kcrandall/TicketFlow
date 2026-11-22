# E13 — Assist

Status: **Completed**

AI help that never sees more than the agent can.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Pluggable assistant abstractions with Claude provider: summaries, drafts grounded in help center, triage; access/masking-aware context.

Boundaries: the assistant only suggests. Summaries, drafts, triage, sentiment/intent, translation and tone rewrites are returned to
the caller; applying them (replying, labelling, assigning) goes through the normal conversation commands by the agent, or by E10
automation acting as its own API-client principal. Context is built through the conversation, contact and help center services with
the requesting principal, so Access decisions and masking apply unchanged.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Assist abstractions: model contract, features, unions, permissions and store contract](tickets/T01-assist-abstractions.md) | Completed | E2, E5, E12 |
| [T02 — Access- and masking-aware context builder with prompt-injection hygiene](tickets/T02-context-builder.md) | Completed | E13 T01 |
| [T03 — Assist service: features, quotas, usage accounting and timeline recording](tickets/T03-assist-service-quotas-recording.md) | Completed | E13 T02 |
| [T04 — Claude provider over the official Anthropic .NET SDK](tickets/T04-claude-provider.md) | Completed | E13 T01 |
| [T05 — PostgreSQL assist store and documentation](tickets/T05-postgres-store-and-docs.md) | Completed | E13 T03 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release`
(24 passed); `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` (17 passed, fake `HttpMessageHandler` only, no real
API calls); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` (6 passed) against an
isolated `postgres:18` container; `dotnet pack` for the four packages. `scripts/verify-package-metadata.sh` is blocked by the concurrent E10
work (Automation project not yet in the solution). Parallel post-ticket reviews ran after commit 1742cba; their P1–P3 findings (pipeline caching/disposal and host clients, ambiguous-failure accounting and reservation sweep, PostgreSQL day counters, feature namespaces) are fixed with regression tests: Assist 28, Claude 17, PostgreSQL 9 passed.
