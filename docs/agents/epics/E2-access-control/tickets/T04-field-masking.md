# T04 — Field masking policies

Epic: [E2 — Access control](../PLAN.md)

Status: **Planned**

## Goal

Field policies per role mark fields (contact.email, contact.phone, attribute keys flagged sensitive) as masked/revealable; decisions return `AccessMasked` with field list; `reveal` permission requires audit.

## Dependencies

E2 T03.

## Acceptance criteria

Masked fields listed deterministically; reveal requires explicit permission and emits audit; no masked value appears in logs.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
