# T03 — DI composition, meta-package and in-memory store base

Epic: [E1 — Foundation](../PLAN.md)

Status: **Planned**

## Goal

Add `AddTicketFlow()` builder, `TicketFlow` meta-package, `TicketFlow.Stores.InMemory` base with revision helpers, and `tests/TicketFlow.Testing` helpers (fake clock, scope factory, principal factory, PostgreSQL random-schema fixture).

## Dependencies

E1 T01, E1 T02.

## Acceptance criteria

Builder preserves application-registered implementations; in-memory stores document non-durability; Testing helpers used by Core tests.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
