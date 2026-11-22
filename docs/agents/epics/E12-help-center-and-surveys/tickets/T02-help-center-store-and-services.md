# T02 — Help center store contract, in-memory store and services

Epic: [E12 — Help center and surveys](../PLAN.md)

Status: **Completed**

## Goal

`IHelpCenterStore` (portals, collections, articles, append-only revisions, published search index), `IArticleFeedbackStore`
(idempotent views and helpful votes), in-memory development implementations, the shared `TicketFlow.HelpCenter.StoreContracts`
fixture, the authorized agent service `IHelpCenterService`, the anonymous `IHelpCenterPublicReader` and `IHelpCenterSearch` for E7
deflection and E13 grounding.

## Dependencies

E12 T01.

## Acceptance criteria

- Writes are scope-confined, revision-fenced and capacity-bounded; portal slugs and published article slugs per (portal, locale)
  are unique; collections with children or articles cannot be deleted; collection depth is bounded.
- Publishing appends an immutable revision and reindexes atomically; rollback republishes an earlier revision as a new revision;
  unpublish/archive remove the article from the public index.
- Anonymous reads see only published articles in public portals; private portals and unpublished content are indistinguishable
  from missing. Agent reads/writes authorize through `IAccessEvaluator`.
- Search is bounded (query ≤ 256 chars, limit ≤ 50, offset ≤ 200) and ranked; feedback is one vote per hashed visitor per article
  and one view per visitor per UTC day.

## Verification

- `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release`.

## Risks and boundaries

In-memory stores are bounded, process-local development examples with naive search and no durability.

## Results

Implemented:

- `IHelpCenterStore` (portals, collections, articles, append-only revisions, atomic published read model with unique slugs, ranked
  bounded search) and `IArticleFeedbackStore` (hashed visitors, daily views, single votes, purge).
- `InMemoryHelpCenterStore` (single lock, bounded, naive all-terms search), shared fixture
  `tests/HelpCenter/TicketFlow.HelpCenter.StoreContracts` (`HelpCenterStoreContract`).
- `HelpCenterService` (Access point checks; planner for lists; publish/unpublish/archive/restore/rollback/delete; preview; feedback),
  `HelpCenterPublicReader` (`IHelpCenterPublicReader` + `IHelpCenterSearch`), `AddHelpCenter()` / `AddInMemoryHelpCenterStores()`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E5 projects).
- `bash scripts/verify-package-metadata.sh` — Validated 22 package projects and central versions.
- `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release` — 67 passed, 0 failed (contract, service, public reader,
  model, composition and renderer tests).

Post-ticket parallel reviews have not been run yet.
