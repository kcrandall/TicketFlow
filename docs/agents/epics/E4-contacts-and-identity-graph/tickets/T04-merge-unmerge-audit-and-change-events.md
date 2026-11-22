# T04 — Merge, unmerge, audit and change events

Epic: [E4 — Contacts and identity graph](../PLAN.md)

Status: **Completed**

## Goal

Merge moves identities, attributes, labels and profile gaps from a source into a target under documented conflict rules, tombstones
the source (`MergedInto`) and persists a merge record that enables unmerge. Both require `contact:merge`, are audited through
`IContactAuditSink` and emit outbox change events plus in-process `IContactChangeObserver` notifications for downstream features (E5).

## Dependencies

E4 T03.

## Acceptance criteria

- Merge and unmerge are single atomic store batches fenced on both contacts' revisions.
- Unmerge restores the source snapshot and removes only what the merge contributed and that is still unchanged on the target.
- Audit events and change events carry identifiers only.

## Verification

- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` (merge/unmerge, audit, observer tests).

## Risks and boundaries

Observers run after commit; the outbox (`IContactStore.ReadChangesAsync`) is the durable integration point.

## Results

Implemented:

- `ContactMerger` (documented conflict rules) and `ContactMergeRecord` (source snapshot plus exact contributions).
- `MergeAsync`/`UnmergeAsync` as single atomic batches fenced on both revisions, requiring `contact:merge` on both contacts (and the merge result); `ContactAuditEvent` (Merged/Unmerged) committed with the write; outbox changes (`Merged`/`Unmerged` + `Updated`); `IContactChangeObserver` notified after commit (failures propagate, change stays applied).
- Unmerge removes only unchanged contributions; chains unmerge newest first; tombstones redirect reads via `ContactRefusal.Merged`.
- Tests: `MergeTests` (graph move, audit, observers, outbox parity, unmerge keeping later edits, chained merges, observer failure).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E3 projects).
- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` — 72 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55474;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 12 passed, 0 failed.
- `bash scripts/verify-package-metadata.sh` — Validated 13 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
