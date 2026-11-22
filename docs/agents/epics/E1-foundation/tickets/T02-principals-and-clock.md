# T02 — Principals, actor references and clock

Epic: [E1 — Foundation](../PLAN.md)

Status: **Planned**

## Goal

Add `PrincipalRef` as a union of `UserPrincipal | ApiClientPrincipal | AutomationPrincipal | SystemPrincipal`, team-membership snapshots, and an injectable `TimeProvider` usage convention.

## Dependencies

E1 T01.

## Acceptance criteria

Principals carry full scope; exhaustive switches compile without discard; principals never embed credentials.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
