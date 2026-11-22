# E2 — Access control

Status: **Planned**

Every later feature can authorize point actions and list queries through one engine with union-typed decisions.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Permission catalog, roles, scoped grants, deny, conditions, masking, query filters, break-glass and audit.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Permission catalog and roles](tickets/T01-permission-catalog-and-roles.md) | Planned | None |
| [T02 — Scoped grants, inheritance and explicit deny](tickets/T02-scoped-grants-and-deny.md) | Planned | E2 T01 |
| [T03 — Union decisions and attribute conditions](tickets/T03-decisions-and-conditions.md) | Planned | E2 T02 |
| [T04 — Field masking policies](tickets/T04-field-masking.md) | Planned | E2 T03 |
| [T05 — Access query filters](tickets/T05-query-filters.md) | Planned | E2 T03 |
| [T06 — Break-glass grants and decision audit](tickets/T06-break-glass-and-audit.md) | Planned | E2 T02, E2 T04 |
| [T07 — PostgreSQL access stores](tickets/T07-postgres-access-store.md) | Planned | E2 T05, E2 T06 |

Independent tickets may run in parallel when file ownership is clear.

## Verification record

Not started.
