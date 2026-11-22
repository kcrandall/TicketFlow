# T04 — Due-work scheduler and at-risk/breached queries

Epic: [E9 — SLA and business calendars](../PLAN.md)

Status: **Completed**

## Goal

`SlaTimerScheduler.RunOnceAsync` claims due timers and re-evaluates their trackers so warnings and breaches fire without new
conversation events; `ISlaService` returns per-conversation SLA status with predictions (`sla:read` on the conversation) and lists
at-risk/breached conversations with the planned Access filter applied inside the store query.

## Dependencies

E9 T03.

## Acceptance criteria

- Firing is idempotent under redelivery and concurrent schedulers.
- Lists are keyset-paged by urgency with cursors bound to the query; every returned row also passes a point check.

## Verification

- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release`.

## Risks and boundaries

No hosted service is shipped; hosts (E11) call the dispatcher and scheduler in their loops.

## Results

Implemented `SlaTimerScheduler` and `SlaService` (status with breach prediction; at-risk/breached keyset lists with planned Access filters plus per-row point checks, query-bound cursors). Tests: `SlaTrackerTests.RiskListsApplyAccessFiltersInsideTheQuery` and the timer-driven warning/breach assertions; `SlaStoreContract.VerifyRiskListAsync`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E6/E8 work in the tree).
- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release` — 26 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55609;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Sla/TicketFlow.Sla.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e9`) — 6 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — fails only on E6's in-progress `src/Channels/TicketFlow.Channels` project not yet in the solution (not an E9 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.

### Review fixes (2026-11-22, after ee963dc)

Appends are fenced on the conversation revision read during a single, actor-checked duplicate scan (conflict → rescan); only the SLA
worker's events count as present; failed flushes defer `NextCheckAt` via `SlaTracker.FlushRetryAt` (+`TimerRetryDelay`) and throw
`SlaFlushDeferredException`; undecidable scans (span > `SlaOptions.MaximumDuplicateScanEvents`, default 1000) keep emissions pending;
tracker writes release a timer lease only for the holding claim (`ISlaTrackerStore.WriteAsync(..., release)`); resumed trackers no
longer overwrite their labels with the snapshot before replay; breached lists are bounded by `urgency_at <= now` and at-risk lists use
the new partial index (migration 2 `at-risk-index`); PostgreSQL leases use `statement_timestamp()` unless a test clock is injected.
Regression tests: `SlaReviewRegressionTests` (concurrent schedulers + syncs + dispatcher + label writers → exactly one event per clock,
verified to fail with an unfenced append; outbox writes keep another worker's lease; forged `sla.*` event does not suppress the real one
and failed flushes back off; >1000-event scan stays pending; label replay), contract additions (fenced lease release, future breach
excluded), PostgreSQL `ConcurrentSchedulersSyncsAndDispatcherEmitExactlyOnce` and `LeasesUseTheDatabaseClockByDefault`.

- `dotnet build TicketFlow.slnx -c Release` — 0 Warning(s), 0 Error(s).
- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release` — 31 passed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55619;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Sla/TicketFlow.Sla.Stores.Postgres.Tests -c Release` (container `tf-pg-e9fix`) — 8 passed.
