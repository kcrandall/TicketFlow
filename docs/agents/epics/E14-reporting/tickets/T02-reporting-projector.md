# T02 — Reporting projector (outbox consumer `reporting`)

Epic: [E14 — Reporting](../PLAN.md)

Status: **Completed**

## Goal

`ReportingProjector`, an `IConversationOutboxHandler` (consumer `reporting`), replays each conversation's timeline from its
reporting state and derives facts: created, resolved, reopened, messages received/sent (channel), first response and per-reply
response times, resolution time (calendar and inbox business time via `BusinessCalendarCalculator`), assignments, handoffs, team
transfers and SLA achieved/breached from `sla.*` events whose actor is the configured SLA worker.

## Dependencies

E14 T01, E9 (SLA events), E12 (survey aggregates for satisfaction).

## Acceptance criteria

- Redelivered and replayed changes add no facts; a consumer registered late backfills from the full timeline (bounded).
- Forged `sla.*` events (other actors) are ignored; SLA facts use the event's own instant (late events).
- Business-time facts use the inbox calendar (calendar time without one).

## Verification

- `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release` (projector scenarios over in-memory conversations).

## Risks and boundaries

Facts are immutable: corrections of past timeline semantics need a rebuild from facts or a re-projection.

## Results

Implemented `ReportingProjector` (consumer `reporting`; fenced per-page projections, first-sight replay with initial
dimensions reconstructed, adoption beyond `MaximumReplayEvents`, state delete on conversation delete) and the internal `ReportReplay`
fold (user replies answer waits, bots do not; first/per-reply response and first resolution times in calendar and business time;
assignments, handoffs, team transfers; `sla.met`/`sla.breached` only from `ReportingOptions.SlaWorkerClientId`, at the payload
instant). Tests: `ProjectorTests` (lifecycle facts, replay/redelivery idempotency, late consumer backfill, adoption, business time
across a weekend and DST, automated replies, forged SLA events, deletes, initial dimensions).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with concurrent E11 work in the tree).
- `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release` — 37 passed, 0 failed.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests -c Release` — 11 passed,
  0 failed, against an isolated `postgres:18` container (`tf-pg-e14`, port 55914).
- `bash scripts/verify-package-metadata.sh` — "Validated 51 package projects and central versions" after adding the three reporting
  packages (a later run failed only on a concurrent E11 project, `TicketFlow.Hosting.Worker`, not yet in the solution).

Post-ticket parallel reviews have not been run yet.
