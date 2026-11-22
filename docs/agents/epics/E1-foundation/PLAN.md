# E1 — Foundation

Status: **Completed**

A tested .NET 11 foundation: full scope with workspace, bounded identifiers, principal references, clock, meta-package, release tooling and manual CI.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Core scope, identifiers, principals, DI, in-memory stores, test helpers, CI and packaging.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Core scope, identifiers and validation](tickets/T01-core-scope-and-identifiers.md) | Completed | None |
| [T02 — Principals, actor references and clock](tickets/T02-principals-and-clock.md) | Completed | E1 T01 |
| [T03 — DI composition, meta-package and in-memory store base](tickets/T03-composition-and-meta-package.md) | Completed | E1 T01, E1 T02 |
| [T04 — Release tooling, package graph and manual CI](tickets/T04-release-tooling-and-ci.md) | Completed | E1 T03 |

Independent tickets may run in parallel when file ownership is clear.

## Verification record

T01–T03: `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test TicketFlow.slnx -c Release` (40 passed: Core 29, TicketFlow 6, Stores.InMemory 5). Post-ticket reviews completed; follow-ups (builder moved to Core, require-PostgreSQL guard, workflow trigger/permission tests) re-verified with E2: 154 tests passed against PostgreSQL, 15 release-script tests OK. T04: `bash scripts/test-release-scripts.sh` (11 OK); `bash scripts/verify-package-metadata.sh` (3 packages); `bash scripts/pack.sh --version 0.1.0-alpha.1 --prerelease alpha --output artifacts/nuget-rehearsal-*` then `verify-package-contents.sh` and `smoke-package-consumption.sh` passed; `bash scripts/test-postgres.sh` (40 passed).
