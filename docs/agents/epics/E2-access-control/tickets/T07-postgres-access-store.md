# T07 — PostgreSQL access stores

Epic: [E2 — Access control](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Access.Stores.Postgres`: roles, grants, field policies, audit with explicit schema initialization, revision fencing and indexed scope columns; SQL translation of `AccessFilter`.

## Dependencies

E2 T05, E2 T06.

## Acceptance criteria

Store contract fixtures pass against PostgreSQL when configured; concurrent grant updates fenced; SQL filter parity with in-memory translator.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented in `src/Access/TicketFlow.Access.Stores.Postgres`:

- `PostgresAccessStore` implements `IAccessRoleStore`, `IAccessGrantStore`, `IAccessFieldPolicyStore` and `IAccessAuditSink` (plus `ReadAuditAsync`) over an application-owned `NpgsqlDataSource`; `AddPostgresAccessStores(dataSource)` registers it with TryAdd.
- Explicit `InitializeAsync` applies `PostgresAccessMigrations.Catalog` through the new `TicketFlow.Stores.Migrations.Postgres` runner (append-only, checksummed, advisory-locked; unknown versions, edits and downgrades fail). Tables `tf_access_roles`, `tf_access_grants`, `tf_access_field_policies`, `tf_access_audit` carry full raw scope columns, `format_version`, numeric discriminators and CHECK constraints; primary keys and indexes use fixed-size generated `tf_key_v1` SHA-256 keys (verified with 256-unit identifiers).
- Revision-fenced compare-and-set updates/deletes; per-scope/per-subject capacity under transaction advisory locks; all SQL parameterized; undecodable rows throw.
- `PostgresAccessFilterTranslator` → `PostgresAccessFilterTranslation` union (`PostgresAccessFilterSql`, `PostgresAccessFilterUntranslatable`); `OrDenyAll()` yields `FALSE`; validated, quoted column mapping; values only as parameters.
- Tests (`tests/Access/TicketFlow.Access.Stores.Postgres.Tests` and `tests/Stores/TicketFlow.Stores.Migrations.Postgres.Tests`, inconclusive without `TICKETFLOW_TEST_POSTGRES`, failing under `TICKETFLOW_REQUIRE_POSTGRES=1`): migration protocol, maximum-length identifiers, concurrent per-subject capacity (exactly one of 8 racing creates applies), role/grant/field-policy contracts with two independent data sources, capacity, explicit/concurrent/versioned initialization, 10-way concurrent grant update fencing (exactly one applies), audit round trip per scope, corrupt rows fail closed, SQL filter parity with point checks over 3 seeded fixtures, untranslatable filters deny all and hostile values stay parameters; translator unit tests run without a database.

Verification commands (run from the repository root, 2026-11-22, after the post-ticket review fixes):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (isolated `postgres:18` container) — Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15 passed (154 total, 0 failed, 0 skipped).
- `dotnet test TicketFlow.slnx -c Release` without PostgreSQL — 138 passed, 16 PostgreSQL tests inconclusive, 0 failed.
- `bash scripts/test-release-scripts.sh` — 15 OK; `bash scripts/verify-package-metadata.sh` — 7 package projects; `bash scripts/pack.sh --skip-tests --output <scratch>` and `smoke-package-consumption.sh` — 7 packages validated, restored, built and loaded.

Post-ticket reviews (security, correctness, tests, perf/API) completed. Fixes applied across E2, each with regression tests: role create/update/delete require holding the role's permissions (old and new) via standard grants (P0 role escalation); tenant grant authority and escalation count only tenant-scoped allows (P1); grant update/revoke and role changes are revision-fenced before authorization (P2 TOCTOU); break-glass grants that change masking or reveal outcomes are flagged and audited (P2); `CreateGrantAsync` rejects break-glass kinds; cross-workspace team/inbox grants never apply and store contract violations throw in Authorize/Plan/Reveal; planner coalesces membership siblings and fails closed with an audited deny-all on oversized plans; custom roles resolve in one store round trip; evaluator/planner/administrator are scoped; `TicketFlowBuilder` moved to Core so Access packages no longer reference the meta-package; PostgreSQL uses an append-only checksummed migration catalog (`TicketFlow.Stores.Migrations.Postgres`) with fixed-size `tf_key_v1` hash keys instead of raw composite text keys (unused scope index dropped); the SQL translator switch has no default arm; parity fixtures add break-glass and deleted-role deny grants; `TICKETFLOW_REQUIRE_POSTGRES=1` prevents silent skips.
