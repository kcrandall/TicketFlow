# T02 — Scoped grants, inheritance and explicit deny

Epic: [E2 — Access control](../PLAN.md)

Status: **Planned**

## Goal

Grants bind principal/team/API client + role + resource scope (tenant, workspace, team, inbox) with optional validity window. Inheritance flows downward; explicit deny overrides allow. `IAccessGrantStore` contract plus in-memory implementation.

## Dependencies

E2 T01.

## Acceptance criteria

Deny beats allow at same or broader scope; expired/not-yet-valid grants ignored using TimeProvider; cross-scope grants never apply.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
