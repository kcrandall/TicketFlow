# T05 — Access query filters

Epic: [E2 — Access control](../PLAN.md)

Status: **Completed**

## Goal

`IAccessQueryPlanner` produces a `closed` `AccessFilter` tree (scope, workspace, team-in, inbox-in, assignee-is, attribute predicates, true/false) for list/search. In-memory translator; translation failure fails closed.

## Dependencies

E2 T03.

## Acceptance criteria

Filter results equal point-check results over randomized fixtures; untranslatable conditions yield deny-all, never allow-all.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented:

- `closed record AccessFilter` (allow-all, deny-all, unknown, scope, and, or, not, team-in, inbox-in, assignee-is, has-label, attribute-equals, attribute-in; ≤ 4096 nodes) with Kleene semantics, the reference in-memory translator `AccessFilterEvaluator`, and Kleene-preserving folding helpers `AccessFilters`.
- `IAccessQueryPlanner` / `AccessQueryPlanner`: `scope ∧ (allows) ∧ ¬denies`, principal references bound at plan time, prechecks fail to `DenyAllFilter`, unknown deny terms stay unknown (deny-all for affected rows, never allow-all).
- Tests: `FilterParityTests` — filter results equal point checks for every principal × permission × resource over 5 seeded randomized fixtures (40 grants, 150 resources, 6 principals, 6 permissions each, including other-scope resources and unknown permissions), plus folding and Kleene unit checks. SQL translation parity is covered in T07.

Verification commands (run from the repository root, 2026-11-22, after the post-ticket review fixes):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (isolated `postgres:18` container) — Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15 passed (154 total, 0 failed, 0 skipped).
- `dotnet test TicketFlow.slnx -c Release` without PostgreSQL — 138 passed, 16 PostgreSQL tests inconclusive, 0 failed.
- `bash scripts/test-release-scripts.sh` — 15 OK; `bash scripts/verify-package-metadata.sh` — 7 package projects; `bash scripts/pack.sh --skip-tests --output <scratch>` and `smoke-package-consumption.sh` — 7 packages validated, restored, built and loaded.

Post-ticket reviews (security, correctness, tests, perf/API) completed. Fixes applied across E2, each with regression tests: role create/update/delete require holding the role's permissions (old and new) via standard grants (P0 role escalation); tenant grant authority and escalation count only tenant-scoped allows (P1); grant update/revoke and role changes are revision-fenced before authorization (P2 TOCTOU); break-glass grants that change masking or reveal outcomes are flagged and audited (P2); `CreateGrantAsync` rejects break-glass kinds; cross-workspace team/inbox grants never apply and store contract violations throw in Authorize/Plan/Reveal; planner coalesces membership siblings and fails closed with an audited deny-all on oversized plans; custom roles resolve in one store round trip; evaluator/planner/administrator are scoped; `TicketFlowBuilder` moved to Core so Access packages no longer reference the meta-package; PostgreSQL uses an append-only checksummed migration catalog (`TicketFlow.Stores.Migrations.Postgres`) with fixed-size `tf_key_v1` hash keys instead of raw composite text keys (unused scope index dropped); the SQL translator switch has no default arm; parity fixtures add break-glass and deleted-role deny grants; `TICKETFLOW_REQUIRE_POSTGRES=1` prevents silent skips.
