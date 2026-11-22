# T03 — PostgreSQL help center store with full-text search

Epic: [E12 — Help center and surveys](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.HelpCenter.Stores.Postgres`: `PostgresHelpCenterStore` implementing `IHelpCenterStore` and `IArticleFeedbackStore`
with an append-only migration catalog, `tf_key_v1` bounded keys and a per-locale `tsvector` search index.

## Dependencies

E12 T02.

## Acceptance criteria

- Passes the shared store contract with two independent data sources.
- Search uses a weighted `tsvector` per (article, locale) with a fixed locale → `regconfig` map (fallback `simple`),
  `websearch_to_tsquery`, `ts_rank_cd` ranking, bounded limit/offset and plain-text `ts_headline` snippets.
- Every statement is parameterized; regconfig names come from a fixed allow-list.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Stemming quality depends on PostgreSQL's built-in dictionaries.

## Results

Implemented:

- `PostgresHelpCenterStore` (`IHelpCenterStore`, `IArticleFeedbackStore`), `PostgresHelpCenterMigrations` (component
  `ticketflow.helpcenter`, version 1), `AddPostgresHelpCenterStores`. Structural writes serialize per workspace on an advisory lock;
  `tf_key_v1` keys; FK cascades for revisions, read model and feedback.
- Search: stored weighted `tsvector` (title A, tags+excerpt B, body C) with per-row `regconfig` from the allow-listed
  `HelpCenterSearchConfiguration` (fallback `simple`), GIN index, `websearch_to_tsquery`, `ts_rank_cd`, bounded LIMIT/OFFSET,
  plain-text `ts_headline` snippets.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E5 projects).
- `bash scripts/verify-package-metadata.sh` — Validated 22 package projects and central versions.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55612;... TICKETFLOW_REQUIRE_POSTGRES=1` `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Stores.Postgres.Tests -c Release` against an isolated
  `postgres:18` container — 10 passed, 0 failed (shared contract with two data sources, stemming/fallback/negation, all regconfigs
  exist, long identifiers, explicit checksummed init, corrupt rows). Without PostgreSQL these are inconclusive.

Post-ticket parallel reviews have not been run yet.


### Review fixes (2026-11-22, after commit 18d5fce)

- `AddPostgresHelpCenterStores()` moved to namespace `TicketFlow.HelpCenter.Stores.Postgres`; regression test `BuilderExtensionsLiveInTheStoreNamespace`.

Verification (2026-11-22): `dotnet build TicketFlow.slnx -c Release` — 0 warnings, 0 errors; `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release` — 68 passed; `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release` — 30 passed; `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Stores.Postgres.Tests -c Release` — 11 passed and `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Surveys/TicketFlow.Surveys.Stores.Postgres.Tests -c Release` — 13 passed (isolated `postgres:18` on port 55613); `dotnet test tests/Integrations/TicketFlow.Integrations.Surveys.Tests -c Release` — 3 passed; `dotnet test tests/Integrations/TicketFlow.Integrations.EnvelopeFlow.Tests -c Release` — 9 passed.
