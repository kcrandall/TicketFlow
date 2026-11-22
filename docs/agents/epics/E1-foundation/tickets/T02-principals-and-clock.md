# T02 — Principals, actor references and clock

Epic: [E1 — Foundation](../PLAN.md)

Status: **Completed**

## Goal

Add `PrincipalRef` as a union of `UserPrincipal | ApiClientPrincipal | AutomationPrincipal | SystemPrincipal`, team-membership snapshots, and an injectable `TimeProvider` usage convention.

## Dependencies

E1 T01.

## Acceptance criteria

Principals carry full scope; exhaustive switches compile without discard; principals never embed credentials.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented in `src/TicketFlow.Core/Principals`:

- `public union PrincipalRef(UserPrincipal, ApiClientPrincipal, AutomationPrincipal, SystemPrincipal)` with an exhaustive (no discard) `Scope` accessor; `default(PrincipalRef)` throws `SwitchExpressionException`.
- Sealed record principals, each carrying full `TicketScope`, redacted `ToString`, no credential members. `UserPrincipal` carries a deduplicated, frozen team-membership snapshot bounded at 256 (`MaximumTeamMemberships`) with set-based value equality.
- Clock convention: inject `TimeProvider` (no custom clock interface); `AddTicketFlow()` registers `TimeProvider.System` via TryAdd; tests use `TestClock` (`FakeTimeProvider`).
- Tests: `tests/TicketFlow.Core.Tests/PrincipalTests.cs` (exhaustive switch, scope carriage, bounds, equality, detachment, redaction, no credential-shaped members).

Verification commands (run from the repository root):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test TicketFlow.slnx -c Release` — TicketFlow.Core.Tests: Passed 29/29; TicketFlow.Tests: Passed 6/6; TicketFlow.Stores.InMemory.Tests: Passed 5/5 (40 total, 0 failed, 0 skipped).
- `dotnet pack TicketFlow.slnx -c Release -o <scratch>` — TicketFlow.Core, TicketFlow and TicketFlow.Stores.InMemory packed with package validation; test support is nonpackable.

Post-ticket parallel reviews (security, correctness, tests, perf/API) completed 2026-11-22. E1-specific findings: `TicketFlowBuilder` moved from the `TicketFlow` meta-package into `TicketFlow.Core` (public constructor, same `TicketFlow` namespace) so feature packages never reference the meta-package; `TICKETFLOW_REQUIRE_POSTGRES=1` turns inconclusive PostgreSQL tests into failures (set in both workflows and `scripts/test-postgres.sh`, covered by `TestingHelperTests`); `scripts/test_release_scripts.py` now asserts both workflows are `workflow_dispatch`-only, check `refs/heads/main`, and grant `id-token: write` only to the publish job. Re-verified: `dotnet build TicketFlow.slnx -c Release` (0 warnings), `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (154 passed), `bash scripts/test-release-scripts.sh` (15 OK), `bash scripts/verify-package-metadata.sh` (7 packages).
