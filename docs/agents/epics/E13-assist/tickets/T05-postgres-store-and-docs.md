# T05 — PostgreSQL assist store and documentation

Epic: [E13 — Assist](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Assist.Stores.Postgres`: `PostgresAssistStore` implementing `IAssistStore` (revisioned settings, atomic quota
reservations under a per-workspace advisory lock, usage settlement and summaries) with an append-only migration catalog and
`tf_key_v1` keys; shared `TicketFlow.Assist.StoreContracts` fixture; `docs/assist.md`.

## Dependencies

E13 T03.

## Acceptance criteria

- Passes the shared store contract against the in-memory store and PostgreSQL with two independent data sources, including
  concurrent reservations that never exceed the quota.
- Every statement is parameterized; rows carry `format_version` and numeric discriminators; undecodable rows throw.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Usage records hold identifiers and counts only — no prompts or outputs.

## Results

Implemented `PostgresAssistStore`, `PostgresAssistMigrations` (component `ticketflow.assist`), `AddPostgresAssistStores()`,
the shared `TicketFlow.Assist.StoreContracts` fixture and `docs/assist.md`. Ran against an isolated `postgres:18` container.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` — 24 passed, 0 failed.
- `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` — 17 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` — 6 passed, 0 failed.
- `dotnet pack` of the four Assist packages succeeded; `bash scripts/verify-package-metadata.sh` currently stops on an unrelated
  in-progress E10 project (`src/Automation/TicketFlow.Automation` not yet in the solution).

Post-ticket parallel reviews have not been run yet.
