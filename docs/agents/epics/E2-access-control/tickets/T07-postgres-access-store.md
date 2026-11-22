# T07 — PostgreSQL access stores

Epic: [E2 — Access control](../PLAN.md)

Status: **Planned**

## Goal

`TicketFlow.Access.Stores.Postgres`: roles, grants, field policies, audit with explicit schema initialization, revision fencing and indexed scope columns; SQL translation of `AccessFilter`.

## Dependencies

E2 T05, E2 T06.

## Acceptance criteria

Store contract fixtures pass against PostgreSQL when configured; concurrent grant updates fenced; SQL filter parity with in-memory translator.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
