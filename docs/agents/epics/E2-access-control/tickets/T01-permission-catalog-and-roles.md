# T01 — Permission catalog and roles

Epic: [E2 — Access control](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Access.Abstractions`: `Permission` (`resource:action`), catalog registration by feature packages, built-in roles (Owner, Admin, Supervisor, Agent, LightAgent, Viewer) and custom role definitions with revisions.

## Dependencies

None.

## Acceptance criteria

Unknown permissions rejected; duplicate registration deterministic; built-ins immutable; custom roles revisioned.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Implemented in `src/Access/TicketFlow.Access.Abstractions`:

- `Permission` (`readonly record struct`, `resource:action`, segments 1-64 chars of `a-z 0-9 - _ .` starting with a letter, `default` uninitialized), `PermissionDefinition` (sensitivity flag, bounded description), built-in `AccessPermissions`.
- `IPermissionCatalog` / `PermissionCatalog` (≤ 4096 entries, ordered). Identical duplicate registrations merge; conflicting ones throw naming the first conflicting permission in ordinal order (deterministic regardless of registration order). Feature packages register through `services.AddAccessPermissions(...)` / `builder.AddAccessPermissions(...)`.
- `RoleId`, `AccessRole` (≤ 512 permissions, set equality), immutable `BuiltInRoles` (Owner = all catalog permissions, Admin, Supervisor, Agent, LightAgent, Viewer); custom roles cannot reuse built-in IDs. `StoredAccessRole` carries scope and revision; `IAccessRoleStore` rejects built-in writes and fences on revision.
- Unknown permissions are denied by the evaluator and planner for every principal, including Owners.
- Tests: `PermissionCatalogTests`, `RoleTests`, role store contract in `AccessStoreContract.VerifyRolesAsync`.

Verification commands (run from the repository root, 2026-11-22, after the post-ticket review fixes):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (isolated `postgres:18` container) — Core 29, TicketFlow 6, Stores.InMemory 5, Stores.Migrations.Postgres 4, Access 95, Access.Stores.Postgres 15 passed (154 total, 0 failed, 0 skipped).
- `dotnet test TicketFlow.slnx -c Release` without PostgreSQL — 138 passed, 16 PostgreSQL tests inconclusive, 0 failed.
- `bash scripts/test-release-scripts.sh` — 15 OK; `bash scripts/verify-package-metadata.sh` — 7 package projects; `bash scripts/pack.sh --skip-tests --output <scratch>` and `smoke-package-consumption.sh` — 7 packages validated, restored, built and loaded.

Post-ticket reviews (security, correctness, tests, perf/API) completed. Fixes applied across E2, each with regression tests: role create/update/delete require holding the role's permissions (old and new) via standard grants (P0 role escalation); tenant grant authority and escalation count only tenant-scoped allows (P1); grant update/revoke and role changes are revision-fenced before authorization (P2 TOCTOU); break-glass grants that change masking or reveal outcomes are flagged and audited (P2); `CreateGrantAsync` rejects break-glass kinds; cross-workspace team/inbox grants never apply and store contract violations throw in Authorize/Plan/Reveal; planner coalesces membership siblings and fails closed with an audited deny-all on oversized plans; custom roles resolve in one store round trip; evaluator/planner/administrator are scoped; `TicketFlowBuilder` moved to Core so Access packages no longer reference the meta-package; PostgreSQL uses an append-only checksummed migration catalog (`TicketFlow.Stores.Migrations.Postgres`) with fixed-size `tf_key_v1` hash keys instead of raw composite text keys (unused scope index dropped); the SQL translator switch has no default arm; parity fixtures add break-glass and deleted-role deny grants; `TICKETFLOW_REQUIRE_POSTGRES=1` prevents silent skips.
