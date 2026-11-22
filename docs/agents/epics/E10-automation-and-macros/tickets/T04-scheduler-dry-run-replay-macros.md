# T04 — Scheduler, contact follower, dry-run, replay, macro runs and canned-response rendering

Epic: [E10 — Automation and macros](../PLAN.md)

Status: **Completed**

## Goal

`AutomationScheduler` (leased cron timers over an access-filtered bounded conversation scan), `AutomationContactFollower`
(durable contact change log catch-up for `ContactUpdated`), `IAutomationRuleService.DryRunAsync` / `ReplayAsync` (side-effect
free), `IMacroService.RunAsync` (agent principal) and `ICannedResponseService.RenderAsync` (masked values never substituted).

## Dependencies

E10 T03.

## Acceptance criteria

- Scheduled executions are idempotent per (rule, conversation, slot); missed slots coalesce; DST-correct next fire instants.
- Dry-run and replay never write conversations or execution records.
- Macros run with the agent's own permissions.

## Verification

- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release`.

## Risks and boundaries

Replay evaluates historical triggers against the current snapshot (no historical snapshots exist).

## Results

Implemented `AutomationScheduler`, `AutomationContactFollower`, dry-run, replay, macro runs and canned-response rendering. Tests: `DryRunPreviewsWithoutSideEffects`, `ReplayReDerivesTriggersAndJoinsRecordedExecutions`, `ScheduledRulesFireOncePerSlotAndRescheduleInTheirTimeZone`, `ContactUpdatesFireContactRules`, `MacrosRunWithTheAgentsOwnPermissions`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E7/E13 work in the tree).
- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 35 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55710;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10`) — 10 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — Validated 47 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.

### Review fixes (2026-11-22, after 1742cba)

- Scheduler claims one rule at a time (each lease covers one run). Tests: `MissedSlotsCoalesceIntoOneRun`, `FailedScheduledRunsAreReleasedOrCountedLost` (release succeeds → retried; release refused → lost; recovery after lease expiry).

Review-fix verification (2026-11-22):

- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 42 passed, 0 failed (regression tests repeated 5×).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55712;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10fix`) — 12 passed, 0 failed.
- `dotnet build` of every Automation project — 0 warnings, 0 errors; `bash scripts/verify-package-metadata.sh` — Validated 51 package projects. `dotnet build TicketFlow.slnx -c Release` currently fails only at restore on E14's in-progress `tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests` project (missing file, not an E10 project).
