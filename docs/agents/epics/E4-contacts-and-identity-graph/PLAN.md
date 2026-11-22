# E4 — Contacts and identity graph

Status: **Completed**

Contacts that unify channel identities without treating addresses as authentication.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Contacts, verified/claimed channel identities, audited merge/unmerge, PII-aware attributes.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Contact model, identity normalisation and permissions](tickets/T01-contact-model-and-identity-normalisation.md) | Completed | E2 |
| [T02 — Contact store contract and in-memory store](tickets/T02-contact-store-contract-and-in-memory-store.md) | Completed | E4 T01 |
| [T03 — Contact service: commands, resolve-or-create, masking, search and export](tickets/T03-contact-service-resolution-masking-search.md) | Completed | E4 T02 |
| [T04 — Merge, unmerge, audit and change events](tickets/T04-merge-unmerge-audit-and-change-events.md) | Completed | E4 T03 |
| [T05 — PostgreSQL contact store](tickets/T05-postgres-contact-store.md) | Completed | E4 T02, E4 T04 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` (72 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release` against an isolated `postgres:18` container (12 passed; without PostgreSQL these 12 are inconclusive); `bash scripts/verify-package-metadata.sh` (13 package projects). Parallel post-ticket reviews are still outstanding before commit.

Review fixes (2026-11-22): eight review findings fixed with regression tests (see T03–T05 Results); Contacts 81 + Contacts PostgreSQL 16 + Conversations 36 + Conversations PostgreSQL 15 passed; solution build clean.
