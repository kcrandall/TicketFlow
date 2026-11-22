# T03 — Tracker store, durable timers and the outbox tracker

Epic: [E9 — SLA and business calendars](../PLAN.md)

Status: **Completed**

## Goal

`ISlaTrackerStore` (revision-fenced per-conversation tracker records, durable timer rows with leased claims, filtered risk lists),
its in-memory implementation and shared store contract, and `SlaConversationTracker` — an `IConversationOutboxHandler` that keeps
trackers in sync with conversations and appends versioned `sla.applied`, `sla.warning`, `sla.breached` and `sla.met`
`ExtensionEvent`s through `IConversationService.AppendExtensionAsync` as a grant-holding API client principal.

## Dependencies

E9 T02, E5 (outbox, `AppendExtensionAsync`).

## Acceptance criteria

- Tracker writes are compare-and-set on revision and set the timer atomically; claims lease due timers without sharing a live
  lease across concurrent claimers; release is fenced.
- Timeline emissions are recorded as pending in the tracker before they are appended and de-duplicated by key on retry, so
  redelivery never duplicates timeline entries.
- Deleting a conversation deletes its tracker; extension-only changes do not re-trigger processing.

## Verification

- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release`.

## Risks and boundaries

Trackers read conversations and timelines through the trusted `IConversationStore` (they mirror the exact Access resource); every
timeline write goes through the access-checked service.

## Results

Implemented `ISlaTrackerStore` (+ `InMemorySlaTrackerStore`, shared `SlaStoreContract`) and `SlaConversationTracker` (outbox consumer `sla`; pending emissions recorded before appending, duplicate scan by key on retry). Tests: `SlaTrackerTests` (end-to-end timeline events exactly once, snooze expiry, lost acknowledgement, worker without grant, deletion), `InMemorySlaStoreTests` (5).

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
