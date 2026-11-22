# T01 — Core scope, identifiers and validation

Epic: [E1 — Foundation](../PLAN.md)

Status: **Completed**

## Goal

Implement `TicketScope` (tenant/project/environment/app/workspace), strongly typed bounded IDs (WorkspaceId, InboxId, TeamId, ContactId, ConversationId, MessageId, LabelId) and shared validation using `Utf16.IsValid`, rejecting null/blank/control/oversized values.

## Dependencies

None.

## Acceptance criteria

Scope equality distinguishes every level; IDs of different kinds cannot be confused at compile time; invalid input rejected with stable parameter names.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented in `src/TicketFlow.Core`:

- `TicketScope(tenantId, projectId, environmentId, appId, WorkspaceId)` sealed record; every level participates in equality; `ToString` is redacted. `TicketScope.RequireIdentifier` is the shared validator (null/blank, >256 UTF-16 code units, C0/C1 controls, malformed UTF-16 via `Utf16.IsValid`) with stable parameter names and no normalization.
- `WorkspaceId`, `InboxId`, `TeamId`, `ContactId`, `ConversationId`, `MessageId`, `LabelId`, `UserId`, `ApiClientId` readonly record structs (no implicit conversions); `default` is uninitialized (`Value` throws) and rejected where required.
- Tests: `tests/TicketFlow.Core.Tests/ScopeTests.cs`, `IdentifierTests.cs` (per-level equality, null/whitespace/control/lone-surrogate/256-257 boundaries, kind distinctness).

Verification commands (run from the repository root):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test TicketFlow.slnx -c Release` — TicketFlow.Core.Tests: Passed 29/29; TicketFlow.Tests: Passed 6/6; TicketFlow.Stores.InMemory.Tests: Passed 5/5 (40 total, 0 failed, 0 skipped).
- `dotnet pack TicketFlow.slnx -c Release -o <scratch>` — TicketFlow.Core, TicketFlow and TicketFlow.Stores.InMemory packed with package validation; test support is nonpackable.

Post-ticket parallel reviews (security, correctness, tests, perf/API) completed 2026-11-22. E1-specific findings: `TicketFlowBuilder` moved from the `TicketFlow` meta-package into `TicketFlow.Core` (public constructor, same `TicketFlow` namespace) so feature packages never reference the meta-package; `TICKETFLOW_REQUIRE_POSTGRES=1` turns inconclusive PostgreSQL tests into failures (set in both workflows and `scripts/test-postgres.sh`, covered by `TestingHelperTests`); `scripts/test_release_scripts.py` now asserts both workflows are `workflow_dispatch`-only, check `refs/heads/main`, and grant `id-token: write` only to the publish job. Re-verified: `dotnet build TicketFlow.slnx -c Release` (0 warnings), `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (154 passed), `bash scripts/test-release-scripts.sh` (15 OK), `bash scripts/verify-package-metadata.sh` (7 packages).
