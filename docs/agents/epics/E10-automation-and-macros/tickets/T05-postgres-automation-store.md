# T05 — PostgreSQL automation store

Epic: [E10 — Automation and macros](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Automation.Stores.Postgres`: `PostgresAutomationStore` implementing every automation store contract, an append-only
`ticketflow.automation` migration catalog and `AddPostgresAutomationStores`.

## Dependencies

E10 T02.

## Acceptance criteria

- `tf_key_v1` keys plus raw-column comparison, `format_version` on every row, parameterized SQL, capacity checks under advisory
  locks, `FOR UPDATE SKIP LOCKED` schedule claims; passes the shared contract with two independent data sources.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Schema is created only by `InitializeAsync`.

## Results

Implemented `PostgresAutomationStore`, the `ticketflow.automation` catalog and `AddPostgresAutomationStores`. Tests: `PostgresAutomationStoreTests` (10: every contract with independent clients, concurrent claims/begins, 256-unit identifiers, undecodable rows, idempotent initialization).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E7/E13 work in the tree).
- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 35 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55710;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10`) — 10 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — Validated 47 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.

### Review fixes (2026-11-22, after 1742cba)

- Append-only migration v2: `tf_automation_rule_sets` (bumped in the rule write transaction), executions `finished` flag with partial purge index, `(scope_key, rule_id, started_at DESC, execution_id DESC)` index, cause index on `execution_id`, backfill of existing rows; `FindManyAsync` in one query; `PurgeAsync` with `FOR UPDATE SKIP LOCKED` batches deleting cause rows. Tests: `PurgeContractUsesIndependentClients`, `UpgradeFromVersionOneBackfillsRuleSetsAndRetentionFlags`, catalog at version 2.

Review-fix verification (2026-11-22):

- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 42 passed, 0 failed (regression tests repeated 5×).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55712;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10fix`) — 12 passed, 0 failed.
- `dotnet build` of every Automation project — 0 warnings, 0 errors; `bash scripts/verify-package-metadata.sh` — Validated 51 package projects. `dotnet build TicketFlow.slnx -c Release` currently fails only at restore on E14's in-progress `tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests` project (missing file, not an E10 project).
