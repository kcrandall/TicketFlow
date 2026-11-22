# T06 — Break-glass grants and decision audit

Epic: [E2 — Access control](../PLAN.md)

Status: **Planned**

## Goal

Time-bound elevated grants require justification; `IAccessAuditSink` records sensitive decisions (grant changes, break-glass use, reveal, export); bounded payloads without content.

## Dependencies

E2 T02, E2 T04.

## Acceptance criteria

Break-glass auto-expires; every sensitive decision recorded once; audit payloads exclude bodies and masked values.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
