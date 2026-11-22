# T03 — Union decisions and attribute conditions

Epic: [E2 — Access control](../PLAN.md)

Status: **Planned**

## Goal

`AccessDecision = AccessAllowed | AccessDenied | AccessMasked` union; condition language as `closed` record AST (equals, in, not, and, or, principal/self references, time windows) evaluated against resource attribute snapshots; `IAccessEvaluator.AuthorizeAsync`.

## Dependencies

E2 T02.

## Acceptance criteria

All decision consumers switch exhaustively; conditions bounded in depth/size; evaluation fails closed on missing attributes.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
