# T04 — Worker hosting, checkpoints and telemetry

Epic: [E11 — Hosting and agent console](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Hosting.Worker`: `BackgroundService` loops for every library loop — `ConversationOutboxDispatcher` (all registered
consumers, plus optional outbox retention pruning), `RoutingMaintenanceWorker`, `SlaTimerScheduler`, `AutomationScheduler`,
`AutomationContactFollower` and `ConversationContactMergeFollower.CatchUpAsync` per workspace (durable checkpoint),
`ISurveyDispatcher.DispatchDueAsync` per workspace and live-chat session pruning. Per-loop options (enabled, interval, batch,
backoff), jittered exponential backoff, graceful shutdown, an `IHealthCheck`, and `ActivitySource`/`Meter` `TicketFlow.Hosting.Worker`
(loop names and outcomes only). `IWorkerCheckpointStore` (in-memory) and `TicketFlow.Hosting.Worker.Stores.Postgres`. Workspace
enumeration through a new minimal `IWorkspaceDirectory` (Workspaces abstractions; in-memory and PostgreSQL implementations).

## Dependencies

E11 T01 (none at code level), E3–E10, E12.

## Acceptance criteria

- Loops whose library service is not registered disable themselves and report so in health data.
- Failures back off exponentially with jitter up to the configured ceiling; exception messages are never logged (type only).
- Stopping the host cancels in-flight runs and completes within the host shutdown timeout.

## Verification

- `dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Tests -c Release`.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Loops are single-process; store leases make concurrent nodes safe for leased loops, and per-workspace followers are idempotent.

## Results

Implemented `TicketFlow.Hosting.Worker` (ten built-in loops, `AddTicketFlowWorkerLoop<TService>` for extra loops, per-loop options,
drain runs, jittered exponential backoff, `TicketFlowWorkerHealth` and `TicketFlowWorkerHealthCheck`, `TicketFlow.Hosting.Worker`
ActivitySource/Meter, `IWorkerCheckpointStore` with `InMemoryWorkerCheckpointStore`), `TicketFlow.Hosting.Worker.Stores.Postgres`
(`PostgresWorkerCheckpointStore`, migration catalog `ticketflow.worker`) and the shared contract `WorkerCheckpointStoreContract`. Added
`IWorkspaceDirectory` to Workspaces (in-memory settings store and `PostgresWorkspaceStore` implement it; registered by both store
registrations) with `WorkspaceStoreContract.VerifyDirectoryAsync`.

Verification (2026-11-22):

- `dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Tests -c Release` — 7 passed, 0 failed.
- `dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Stores.Postgres.Tests -c Release` with `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55711;... TICKETFLOW_REQUIRE_POSTGRES=1` against an isolated `postgres:18` container (`tf-pg-e11`) — 2 passed, 0 failed.
- `dotnet test tests/Workspaces/TicketFlow.Workspaces.Tests -c Release` — 51 passed; `dotnet test tests/Workspaces/TicketFlow.Workspaces.Stores.Postgres.Tests -c Release` (PostgreSQL) — 14 passed.

Review fixes (2026-11-22): per-workspace failures no longer fail or back off the loop (per-workspace exponential backoff, item failures
reported as Degraded); the directory position persists between runs and wraps (`MaximumWorkspacesPerRun` covers large installations
across runs); staleness check (`StaleAfterIntervals`); new loops `event-delivery`, `event-retention`, `event-contacts`, `event-surveys`,
`reporting-retention`; `TicketFlowServiceIdentities` (`Collect`, startup `ValidateSlaWorker`). Tests: `PerWorkspaceLoopsVisitEveryWorkspaceAndIsolateFailures`,
`CappedRunsContinueWhereThePreviousRunStoppedAndWrap`, `LoopsWithoutACompletedRunForSeveralIntervalsDegradeHealth`,
`MismatchedSlaWorkerIdentitiesFailStartup`. `dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Tests -c Release` — 10 passed;
PostgreSQL checkpoint tests 2 passed.
