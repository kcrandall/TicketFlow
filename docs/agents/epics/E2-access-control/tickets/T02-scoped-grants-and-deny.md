# T02 — Scoped grants, inheritance and explicit deny

Epic: [E2 — Access control](../PLAN.md)

Status: **Completed**

## Goal

Grants bind principal/team/API client + role + resource scope (tenant, workspace, team, inbox) with optional validity window. Inheritance flows downward; explicit deny overrides allow. `IAccessGrantStore` contract plus in-memory implementation.

## Dependencies

E2 T01.

## Acceptance criteria

Deny beats allow at same or broader scope; expired/not-yet-valid grants ignored using TimeProvider; cross-scope grants never apply.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented:

- `AccessGrant` (`GrantId`, `AccessPartition`, `public union GrantSubject(UserId, TeamId, ApiClientId)`, `RoleId`, closed `GrantScope` = tenant/workspace/team/inbox, `GrantEffect`, optional condition, `[ValidFrom, ValidUntil)` normalized to UTC microseconds, bounded justification, `GrantKind`); `GrantValidationResult` union and `AccessGrantValidator`.
- `IAccessGrantStore` contract (partition-scoped, subject-filtered listing of tenant + workspace grants, compare-and-set, ≤ 256 grants per subject checked atomically) and `InMemoryAccessGrantStore` (development only).
- Evaluator: principal scope must equal resource scope; inheritance tenant → workspace → team/inbox; any applicable deny overrides every allow (same, broader or narrower scope); deny with an unresolvable role denies everything; validity via `TimeProvider`; team subjects via the principal's team snapshot; automation/system principals unsupported.
- Tests: `GrantTests` (inheritance, deny precedence, cross-scope isolation over all `TestScopes.LevelVariants`, validity windows with `FakeTimeProvider`, administrator authorization and escalation prevention) and the grant store contract.

Verification commands (run from the repository root, 2026-11-22, after the post-ticket review fixes):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (isolated `postgres:18` container) — Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15 passed (154 total, 0 failed, 0 skipped).
- `dotnet test TicketFlow.slnx -c Release` without PostgreSQL — 138 passed, 16 PostgreSQL tests inconclusive, 0 failed.
- `bash scripts/test-release-scripts.sh` — 15 OK; `bash scripts/verify-package-metadata.sh` — 7 package projects; `bash scripts/pack.sh --skip-tests --output <scratch>` and `smoke-package-consumption.sh` — 7 packages validated, restored, built and loaded.

Post-ticket reviews (security, correctness, tests, perf/API) completed. Fixes applied across E2, each with regression tests: role create/update/delete require holding the role's permissions (old and new) via standard grants (P0 role escalation); tenant grant authority and escalation count only tenant-scoped allows (P1); grant update/revoke and role changes are revision-fenced before authorization (P2 TOCTOU); break-glass grants that change masking or reveal outcomes are flagged and audited (P2); `CreateGrantAsync` rejects break-glass kinds; cross-workspace team/inbox grants never apply and store contract violations throw in Authorize/Plan/Reveal; planner coalesces membership siblings and fails closed with an audited deny-all on oversized plans; custom roles resolve in one store round trip; evaluator/planner/administrator are scoped; `TicketFlowBuilder` moved to Core so Access packages no longer reference the meta-package; PostgreSQL uses an append-only checksummed migration catalog (`TicketFlow.Stores.Migrations.Postgres`) with fixed-size `tf_key_v1` hash keys instead of raw composite text keys (unused scope index dropped); the SQL translator switch has no default arm; parity fixtures add break-glass and deleted-role deny grants; `TICKETFLOW_REQUIRE_POSTGRES=1` prevents silent skips.
