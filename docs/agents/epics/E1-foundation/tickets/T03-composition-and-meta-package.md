# T03 — DI composition, meta-package and in-memory store base

Epic: [E1 — Foundation](../PLAN.md)

Status: **Completed**

## Goal

Add `AddTicketFlow()` builder, `TicketFlow` meta-package, `TicketFlow.Stores.InMemory` base with revision helpers, and `tests/TicketFlow.Testing` helpers (fake clock, scope factory, principal factory, PostgreSQL random-schema fixture).

## Dependencies

E1 T01, E1 T02.

## Acceptance criteria

Builder preserves application-registered implementations; in-memory stores document non-durability; Testing helpers used by Core tests.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented:

- `src/TicketFlow`: `AddTicketFlow()` returns `TicketFlowBuilder { Services }`; TryAdd registration of `TimeProvider.System`; idempotent and preserves application registrations.
- `src/Stores/TicketFlow.Stores.InMemory`: `ScopedRevisionStore<TKey, TValue>` (full-scope keyed, single lock, bounded capacity, revision-fenced add/replace/remove) returning the `RevisionWriteResult` union (`RevisionApplied`, `RevisionNotFound`, `RevisionConflict`); README documents non-durability.
- `tests/TicketFlow.Testing` (nonpackable): `TestClock` (Microsoft.Extensions.TimeProvider.Testing 11.0.0), `TestScopes` (factory, per-level variants, long identifiers), `TestPrincipals`, `PostgresTestServer`/`PostgresTestDatabase` random-schema fixture reading `TICKETFLOW_TEST_POSTGRES` (Inconclusive when absent). Used by Core, composition and store tests.
- Packaging: package READMEs and Descriptions; `src/Directory.Build.props`; projects added to `TicketFlow.slnx`; packable projects listed in `scripts/nuget-projects.tsv`.
- Tests: `tests/TicketFlow.Tests` (composition, testing helpers), `tests/Stores/TicketFlow.Stores.InMemory.Tests` (fencing, cross-scope isolation per level, capacity, concurrency).

Verification commands (run from the repository root):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test TicketFlow.slnx -c Release` — TicketFlow.Core.Tests: Passed 29/29; TicketFlow.Tests: Passed 6/6; TicketFlow.Stores.InMemory.Tests: Passed 5/5 (40 total, 0 failed, 0 skipped).
- `dotnet pack TicketFlow.slnx -c Release -o <scratch>` — TicketFlow.Core, TicketFlow and TicketFlow.Stores.InMemory packed with package validation; test support is nonpackable.

Post-ticket parallel reviews (security, correctness, tests, perf/API) completed 2026-11-22. E1-specific findings: `TicketFlowBuilder` moved from the `TicketFlow` meta-package into `TicketFlow.Core` (public constructor, same `TicketFlow` namespace) so feature packages never reference the meta-package; `TICKETFLOW_REQUIRE_POSTGRES=1` turns inconclusive PostgreSQL tests into failures (set in both workflows and `scripts/test-postgres.sh`, covered by `TestingHelperTests`); `scripts/test_release_scripts.py` now asserts both workflows are `workflow_dispatch`-only, check `refs/heads/main`, and grant `id-token: write` only to the publish job. Re-verified: `dotnet build TicketFlow.slnx -c Release` (0 warnings), `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (154 passed), `bash scripts/test-release-scripts.sh` (15 OK), `bash scripts/verify-package-metadata.sh` (7 packages).
