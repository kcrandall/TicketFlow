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


Review fixes (2026-11-22, after commit 1742cba):

- P1: `AssistChatClientFactory` (singleton, `IDisposable`) caches pipelines per (model, feature, thinking level) and provider clients
  per provider model, and disposes both with the container; hosts can supply a keyed `IChatClient` per provider
  (`AssistChatClients.ServiceKey`) and add middleware through `AssistOptions.Pipeline`.
- P2 accounting: `AssistProviderException.TokensMayHaveBeenConsumed`; reservations are released only when nothing was provably
  consumed (client not built, not configured, provider error status) and every ambiguous outcome or missing usage is settled at the
  estimate (`AssistUsageRecord.Estimated`, `AssistUsageTotal.EstimatedTokens`); `IAssistStore.SweepReservationsAsync` +
  `AssistMaintenance.RunOnceAsync` (with `AssistOptions.ReservationTimeout`) settle abandoned reservations.
- P2 quota: PostgreSQL migration 2 adds `tf_assist_usage_days` (backfilled) and the `estimated` column; reservations charge the day
  counter with a conditional UPSERT and read only the last minute's rows; settlement adjusts the counter by actual − reserved.
- P3: registration extensions moved to `TicketFlow.Assist`, `TicketFlow.Assist.Providers.Claude` and `TicketFlow.Assist.Stores.Postgres`.
- Regression tests: pipeline reuse and disposal, keyed host client and middleware, timeout/network/cancellation after send and null
  usage charged at the estimate, provably-unsent releases, maintenance sweep, contract tests for estimated settlement, sweep and mixed
  concurrent reserve/settle, day-counter authority and v1→v2 backfill on PostgreSQL, Claude `TokensMayHaveBeenConsumed` mapping.
- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` — 28 passed; `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` —
  17 passed; `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` — 9 passed (isolated
  `postgres:18` container on port 55917).

Post-ticket parallel reviews ran; the findings above are fixed.
