# T02 — Store contracts, in-memory stores and access-checked management services

Epic: [E10 — Automation and macros](../PLAN.md)

Status: **Completed**

## Goal

`IAutomationRuleStore` (rules plus leased schedule timers), `IAutomationExecutionStore` (idempotent execution records, cause
index, rate counts, checkpoints, log queries), `IMacroStore`, `ICannedResponseStore`, development in-memory stores, the shared
`AutomationStoreContract`, and the access-checked `IAutomationRuleService`, `IMacroService` and `ICannedResponseService`.

## Dependencies

E10 T01.

## Acceptance criteria

- All stores are scope-confined, create at revision 1, compare-and-set update/delete and enforce capacities atomically.
- Execution records are unique per (rule, conversation, trigger key); schedule claims never share a live lease.
- `automation:read`/`automation:manage` (sensitive), `macro:run`/`macro:manage`, `canned-response:manage` are registered by
  `AddAutomation()`; canned responses are readable with `conversation:reply`; personal macros are visible only to their owner.

## Verification

- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release`.

## Risks and boundaries

In-memory stores are process-local development examples with no durability.

## Results

Implemented the four store contracts, in-memory stores, shared `AutomationStoreContract` and `AutomationRuleService`/`MacroService`/`CannedResponseService`. Tests: `InMemoryAutomationStoreTests` (7), `RuleManagementIsAccessChecked`, `PersonalMacrosAreVisibleOnlyToTheirOwner`, `CannedResponsesRenderWithoutMaskedValues`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E7/E13 work in the tree).
- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 35 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55710;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10`) — 10 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — Validated 47 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.

### Review fixes (2026-11-22, after 1742cba)

- `IAutomationRuleStore.GetRuleSetVersionAsync`, `IAutomationExecutionStore.FindManyAsync` and `PurgeAsync(before, limit)` (finished records plus cause rows); in-memory implementations; contract `VerifyPurgeAsync`, version and FindMany checks in the rule/execution contracts.

Review-fix verification (2026-11-22):

- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 42 passed, 0 failed (regression tests repeated 5×).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55712;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10fix`) — 12 passed, 0 failed.
- `dotnet build` of every Automation project — 0 warnings, 0 errors; `bash scripts/verify-package-metadata.sh` — Validated 51 package projects. `dotnet build TicketFlow.slnx -c Release` currently fails only at restore on E14's in-progress `tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests` project (missing file, not an E10 project).
