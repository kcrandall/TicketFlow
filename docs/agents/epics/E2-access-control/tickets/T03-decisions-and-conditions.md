# T03 — Union decisions and attribute conditions

Epic: [E2 — Access control](../PLAN.md)

Status: **Completed**

## Goal

`AccessDecision = AccessAllowed | AccessDenied | AccessMasked` union; condition language as `closed` record AST (equals, in, not, and, or, principal/self references, time windows) evaluated against resource attribute snapshots; `IAccessEvaluator.AuthorizeAsync`.

## Dependencies

E2 T02.

## Acceptance criteria

All decision consumers switch exhaustively; conditions bounded in depth/size; evaluation fails closed on missing attributes.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented:

- `public union AccessDecision(AccessAllowed, AccessDenied, AccessMasked);` with `IsPermitted` and `AccessDenialReason`; every consumer in the code base switches exhaustively without a discard arm.
- `closed record AccessCondition` AST (and, or, not, assignee-is-self, team-in-principal-teams, attribute-equals, attribute-in, has-label, UTC time window) bounded at construction (depth ≤ 8, ≤ 64 nodes, ≤ 64 values) and the versioned `AccessConditionCodec` (`{"v":1,"c":…}` with explicit `op` discriminators, bounded decoding).
- Three-valued evaluation over `AccessResource` snapshots: missing attributes/labels and self references for non-user principals are unknown; allow needs true, deny applies unless false (fail closed).
- `IAccessEvaluator.AuthorizeAsync` implemented by `AccessEvaluator`.
- Tests: `ConditionTests` (depth/size bounds, missing-attribute fail-closed for allow and deny, negation of unknown, principal references, midnight-wrapping time windows, codec round trip and rejection cases).

Verification commands (run from the repository root, 2026-11-22, after the post-ticket review fixes):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (isolated `postgres:18` container) — Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15 passed (154 total, 0 failed, 0 skipped).
- `dotnet test TicketFlow.slnx -c Release` without PostgreSQL — 138 passed, 16 PostgreSQL tests inconclusive, 0 failed.
- `bash scripts/test-release-scripts.sh` — 15 OK; `bash scripts/verify-package-metadata.sh` — 7 package projects; `bash scripts/pack.sh --skip-tests --output <scratch>` and `smoke-package-consumption.sh` — 7 packages validated, restored, built and loaded.

Post-ticket reviews (security, correctness, tests, perf/API) completed. Fixes applied across E2, each with regression tests: role create/update/delete require holding the role's permissions (old and new) via standard grants (P0 role escalation); tenant grant authority and escalation count only tenant-scoped allows (P1); grant update/revoke and role changes are revision-fenced before authorization (P2 TOCTOU); break-glass grants that change masking or reveal outcomes are flagged and audited (P2); `CreateGrantAsync` rejects break-glass kinds; cross-workspace team/inbox grants never apply and store contract violations throw in Authorize/Plan/Reveal; planner coalesces membership siblings and fails closed with an audited deny-all on oversized plans; custom roles resolve in one store round trip; evaluator/planner/administrator are scoped; `TicketFlowBuilder` moved to Core so Access packages no longer reference the meta-package; PostgreSQL uses an append-only checksummed migration catalog (`TicketFlow.Stores.Migrations.Postgres`) with fixed-size `tf_key_v1` hash keys instead of raw composite text keys (unused scope index dropped); the SQL translator switch has no default arm; parity fixtures add break-glass and deleted-role deny grants; `TICKETFLOW_REQUIRE_POSTGRES=1` prevents silent skips.
