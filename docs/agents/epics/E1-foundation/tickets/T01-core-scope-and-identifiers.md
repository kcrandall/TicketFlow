# T01 — Core scope, identifiers and validation

Epic: [E1 — Foundation](../PLAN.md)

Status: **Planned**

## Goal

Implement `TicketScope` (tenant/project/environment/app/workspace), strongly typed bounded IDs (WorkspaceId, InboxId, TeamId, ContactId, ConversationId, MessageId, LabelId) and shared validation using `Utf16.IsValid`, rejecting null/blank/control/oversized values.

## Dependencies

None.

## Acceptance criteria

Scope equality distinguishes every level; IDs of different kinds cannot be confused at compile time; invalid input rejected with stable parameter names.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
