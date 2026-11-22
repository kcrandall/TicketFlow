# T06 — PostgreSQL survey store and documentation

Epic: [E12 — Help center and surveys](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Surveys.Stores.Postgres` (`PostgresSurveyStore`, migration catalog, Access-filtered aggregate and response queries),
plus `docs/help-center.md` and `docs/surveys.md`.

## Dependencies

E12 T03, E12 T05.

## Acceptance criteria

- Passes the shared survey store contract with two independent data sources, including concurrent cooldown races.
- Aggregates and response lists translate the `AccessFilter` with `PostgresAccessFilterTranslator`; untranslatable filters return
  no rows.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Surveys/TicketFlow.Surveys.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Aggregates scan responses within the bounded time range using a (scope, responded_at) index.

## Results

Implemented:

- `PostgresSurveyStore` (`ISurveyStore`), `PostgresSurveyMigrations` (component `ticketflow.surveys`, version 1),
  `AddPostgresSurveyStores`. Invitation creates serialize per (survey, contact) on an advisory lock; unique (survey, conversation) and
  token-hash indexes; row locks for updates and responses; response lists and aggregates translate the `AccessFilter` with
  `PostgresAccessFilterTranslator` over a derived table exposing inbox/team/agent and unknown labels/attributes.
- `docs/help-center.md` and `docs/surveys.md`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E5 projects).
- `bash scripts/verify-package-metadata.sh` — Validated 22 package projects and central versions.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55612;... TICKETFLOW_REQUIRE_POSTGRES=1` `dotnet test tests/Surveys/TicketFlow.Surveys.Stores.Postgres.Tests -c Release` against an isolated `postgres:18`
  container — 10 passed, 0 failed (shared contract incl. concurrent cooldown/response races, randomized grant parity seeds 1/7/42,
  token-hash-only persistence, explicit checksummed init, corrupt rows). Without PostgreSQL these are inconclusive.

Post-ticket parallel reviews have not been run yet.


### Review fixes (2026-11-22, after commit 18d5fce)

- `PostgresSurveyStore.ClaimDueInvitationsAsync`: `UPDATE … WHERE invitation_key IN (SELECT … FOR UPDATE SKIP LOCKED LIMIT n)`;
  migration version 2 `dispatch-leases` (state 6, `lease_until`, `sent_at` → `issued_at`, lease partial index) upgrades version 1 in
  place. Tests: `TwoDispatchersOnTwoDataSourcesSendEachInvitationOnce` (120 invitations, two dispatchers on two data sources, exactly one
  send each), `VersionOneSchemasUpgradeInPlace`, claims contract with independent clients.
- `AddPostgresSurveyStores()` moved to namespace `TicketFlow.Surveys.Stores.Postgres`; docs updated (`docs/surveys.md`, READMEs).

Verification (2026-11-22): `dotnet build TicketFlow.slnx -c Release` — 0 warnings, 0 errors; `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release` — 68 passed; `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release` — 30 passed; `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Stores.Postgres.Tests -c Release` — 11 passed and `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Surveys/TicketFlow.Surveys.Stores.Postgres.Tests -c Release` — 13 passed (isolated `postgres:18` on port 55613); `dotnet test tests/Integrations/TicketFlow.Integrations.Surveys.Tests -c Release` — 3 passed; `dotnet test tests/Integrations/TicketFlow.Integrations.EnvelopeFlow.Tests -c Release` — 9 passed.
