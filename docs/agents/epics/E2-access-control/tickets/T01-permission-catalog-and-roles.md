# T01 — Permission catalog and roles

Epic: [E2 — Access control](../PLAN.md)

Status: **Planned**

## Goal

`TicketFlow.Access.Abstractions`: `Permission` (`resource:action`), catalog registration by feature packages, built-in roles (Owner, Admin, Supervisor, Agent, LightAgent, Viewer) and custom role definitions with revisions.

## Dependencies

None.

## Acceptance criteria

Unknown permissions rejected; duplicate registration deterministic; built-ins immutable; custom roles revisioned.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
