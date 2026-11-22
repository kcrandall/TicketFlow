# T02 — SLA clock engine: business time, pauses, escalations and prediction

Epic: [E9 — SLA and business calendars](../PLAN.md)

Status: **Completed**

## Goal

A deterministic engine that turns conversation timeline facts (incoming/outgoing messages, status, priority and label changes)
into per-metric SLA clocks (elapsed business time, due instant, paused intervals, warning/breach thresholds, met/missed outcomes)
using `BusinessCalendarCalculator` for all business-time math, and computes the tracker schedule (next check, due, at-risk,
breached, urgency) plus breach predictions.

## Dependencies

E9 T01.

## Acceptance criteria

- First response starts at the first incoming message and is met by the first outgoing reply; next response cycles start at each
  later unanswered incoming message; resolution runs from application until resolve/close and resumes on reopen.
- Pending and Snoozed pause running clocks when configured; an expired snooze resumes the clock at its `Until` instant.
- Thresholds fire in instant order exactly once per clock cycle; breach (100 %) always fires; rule re-matching keeps elapsed time.
- Tests cover a 4-business-hour first-response target spanning a weekend, a DST change, a holiday and a snooze.

## Verification

- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release`.

## Risks and boundaries

Policy edits apply to new applications and re-matches only, never retroactively. Calendar edits affect future accumulation only.

## Results

Implemented `SlaEngine` and `SlaClockTime` (all business-time math through `BusinessCalendarCalculator`), `SlaTracker`/`SlaClock`/`SlaSchedule`, `SlaTrackerCodec`, the `SlaTimelineEvent` union and `SlaEventCodec`. Tests: `SlaEngineTests` (9: weekend + spring-forward + snooze, holiday, fall-back day, configurable pending/snooze pauses, next-response cycles and resolution reopen, priority/label re-matching, threshold ordering and once-only firing, late facts).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E6/E8 work in the tree).
- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release` — 26 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55609;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Sla/TicketFlow.Sla.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e9`) — 6 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — fails only on E6's in-progress `src/Channels/TicketFlow.Channels` project not yet in the solution (not an E9 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
