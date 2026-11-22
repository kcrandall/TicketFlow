# T04 — Field masking policies

Epic: [E2 — Access control](../PLAN.md)

Status: **Completed**

## Goal

Field policies per role mark fields (contact.email, contact.phone, attribute keys flagged sensitive) as masked/revealable; decisions return `AccessMasked` with field list; `reveal` permission requires audit.

## Dependencies

E2 T03.

## Acceptance criteria

Masked fields listed deterministically; reveal requires explicit permission and emits audit; no masked value appears in logs.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented:

- `FieldPath`, `AccessFieldPolicy` (applies-to permissions, unmasked roles, optional reveal permission), `IAccessFieldPolicyStore` contract and `InMemoryAccessFieldPolicyStore`.
- Allowed decisions return `AccessMasked` with fields ordered by path; a field is unmasked only when an applicable allow grant uses an unmasked role; `MaskedField.Revealable` reflects the reveal permission.
- `IAccessEvaluator.RevealAsync` returns the `RevealResult` union; reveal requires the read permission, an applicable policy and its explicit reveal permission; every call (granted or refused) records one audit event with the field name only.
- No logging of values; `AccessResource` and principals redact `ToString`.
- Tests: `MaskingTests` (deterministic per-role field lists, reveal rules and audit, audit content excludes masked and attribute values).

Verification commands (run from the repository root, 2026-11-22, after the post-ticket review fixes):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (isolated `postgres:18` container) — Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15 passed (154 total, 0 failed, 0 skipped).
- `dotnet test TicketFlow.slnx -c Release` without PostgreSQL — 138 passed, 16 PostgreSQL tests inconclusive, 0 failed.
- `bash scripts/test-release-scripts.sh` — 15 OK; `bash scripts/verify-package-metadata.sh` — 7 package projects; `bash scripts/pack.sh --skip-tests --output <scratch>` and `smoke-package-consumption.sh` — 7 packages validated, restored, built and loaded.

Post-ticket reviews (security, correctness, tests, perf/API) completed. Fixes applied across E2, each with regression tests: role create/update/delete require holding the role's permissions (old and new) via standard grants (P0 role escalation); tenant grant authority and escalation count only tenant-scoped allows (P1); grant update/revoke and role changes are revision-fenced before authorization (P2 TOCTOU); break-glass grants that change masking or reveal outcomes are flagged and audited (P2); `CreateGrantAsync` rejects break-glass kinds; cross-workspace team/inbox grants never apply and store contract violations throw in Authorize/Plan/Reveal; planner coalesces membership siblings and fails closed with an audited deny-all on oversized plans; custom roles resolve in one store round trip; evaluator/planner/administrator are scoped; `TicketFlowBuilder` moved to Core so Access packages no longer reference the meta-package; PostgreSQL uses an append-only checksummed migration catalog (`TicketFlow.Stores.Migrations.Postgres`) with fixed-size `tf_key_v1` hash keys instead of raw composite text keys (unused scope index dropped); the SQL translator switch has no default arm; parity fixtures add break-glass and deleted-role deny grants; `TICKETFLOW_REQUIRE_POSTGRES=1` prevents silent skips.
