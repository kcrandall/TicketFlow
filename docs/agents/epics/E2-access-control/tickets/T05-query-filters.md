# T05 — Access query filters

Epic: [E2 — Access control](../PLAN.md)

Status: **Planned**

## Goal

`IAccessQueryPlanner` produces a `closed` `AccessFilter` tree (scope, workspace, team-in, inbox-in, assignee-is, attribute predicates, true/false) for list/search. In-memory translator; translation failure fails closed.

## Dependencies

E2 T03.

## Acceptance criteria

Filter results equal point-check results over randomized fixtures; untranslatable conditions yield deny-all, never allow-all.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
