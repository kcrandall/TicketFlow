# E1 — Foundation

Status: **Planned**

A tested .NET 11 foundation: full scope with workspace, bounded identifiers, principal references, clock, meta-package, release tooling and manual CI.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Core scope, identifiers, principals, DI, in-memory stores, test helpers, CI and packaging.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Core scope, identifiers and validation](tickets/T01-core-scope-and-identifiers.md) | Planned | None |
| [T02 — Principals, actor references and clock](tickets/T02-principals-and-clock.md) | Planned | E1 T01 |
| [T03 — DI composition, meta-package and in-memory store base](tickets/T03-composition-and-meta-package.md) | Planned | E1 T01, E1 T02 |
| [T04 — Release tooling, package graph and manual CI](tickets/T04-release-tooling-and-ci.md) | Planned | E1 T03 |

Independent tickets may run in parallel when file ownership is clear.

## Verification record

Not started.
