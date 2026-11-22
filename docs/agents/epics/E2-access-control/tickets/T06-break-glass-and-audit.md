# T06 — Break-glass grants and decision audit

Epic: [E2 — Access control](../PLAN.md)

Status: **Completed**

## Goal

Time-bound elevated grants require justification; `IAccessAuditSink` records sensitive decisions (grant changes, break-glass use, reveal, export); bounded payloads without content.

## Dependencies

E2 T02, E2 T04.

## Acceptance criteria

Break-glass auto-expires; every sensitive decision recorded once; audit payloads exclude bodies and masked values.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented:

- `AccessAuditEvent` (identifiers, permission, field, outcome, denial reason, break-glass flag, justification; no content or values), `AccessActor`, `IAccessAuditSink` (persist before returning, throw on failure) and bounded `InMemoryAccessAuditSink` (throws when full so decisions fail closed).
- Sensitive-permission decisions and plans, every break-glass use, every reveal and every administrative change record exactly one event per call.
- `IAccessAdministrator` / `AccessAdministrator`: authorized (standard grants only), validated, escalation-free role/grant/field-policy changes with `AccessChangeResult<T>` union outcomes.
- `ActivateBreakGlassAsync`: justification ≥ configurable minimum (default 10), duration ≤ `MaximumBreakGlassDuration` (default 4 h, ceiling 24 h), requester-only subject, workspace/team/inbox scope, role without management/break-glass permissions, requires `access:break-glass`; grants auto-expire through their validity window, cannot be updated, and activation/use are audited.
- Tests: `BreakGlassAndAuditTests` (activation rules, audited activation/use/plan, auto-expiry, exactly-once sensitive audit, audit failure fails closed), audit assertions in `RoleTests`, `GrantTests` and `MaskingTests`.

Known limitation (pinned by `AdministrationTests.AdministrativeAuditIsRecordedAfterTheWrite`): administrative audit events are recorded after the store write, not in the same transaction; a sink failure then surfaces as an exception after the change applied.

Verification commands (run from the repository root, 2026-11-22, after the post-ticket review fixes):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (isolated `postgres:18` container) — Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15 passed (154 total, 0 failed, 0 skipped).
- `dotnet test TicketFlow.slnx -c Release` without PostgreSQL — 138 passed, 16 PostgreSQL tests inconclusive, 0 failed.
- `bash scripts/test-release-scripts.sh` — 15 OK; `bash scripts/verify-package-metadata.sh` — 7 package projects; `bash scripts/pack.sh --skip-tests --output <scratch>` and `smoke-package-consumption.sh` — 7 packages validated, restored, built and loaded.

Post-ticket reviews (security, correctness, tests, perf/API) completed. Fixes applied across E2, each with regression tests: role create/update/delete require holding the role's permissions (old and new) via standard grants (P0 role escalation); tenant grant authority and escalation count only tenant-scoped allows (P1); grant update/revoke and role changes are revision-fenced before authorization (P2 TOCTOU); break-glass grants that change masking or reveal outcomes are flagged and audited (P2); `CreateGrantAsync` rejects break-glass kinds; cross-workspace team/inbox grants never apply and store contract violations throw in Authorize/Plan/Reveal; planner coalesces membership siblings and fails closed with an audited deny-all on oversized plans; custom roles resolve in one store round trip; evaluator/planner/administrator are scoped; `TicketFlowBuilder` moved to Core so Access packages no longer reference the meta-package; PostgreSQL uses an append-only checksummed migration catalog (`TicketFlow.Stores.Migrations.Postgres`) with fixed-size `tf_key_v1` hash keys instead of raw composite text keys (unused scope index dropped); the SQL translator switch has no default arm; parity fixtures add break-glass and deleted-role deny grants; `TICKETFLOW_REQUIRE_POSTGRES=1` prevents silent skips.
