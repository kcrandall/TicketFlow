# T04 — Business calendars and business-time calculator

Epic: [E3 — Workspaces, inboxes and teams](../PLAN.md)

Status: **Completed**

## Goal

Business calendars (weekly hours per weekday in an IANA/Windows time zone, holiday and date overrides) with a calculator providing
`IsOpen`, `NextOpen`, `AddBusinessTime` and `BusinessTimeBetween` for SLA (E9); authorized calendar administration.

## Dependencies

E3 T01.

## Acceptance criteria

- Wall-clock semantics across DST: nonexistent local times are never open, repeated local times are open in both passes.
- Tests cover spring-forward/fall-back in several zones (including 30-minute DST and midnight transitions), overnight shifts,
  holidays/overrides and the inverse property between `AddBusinessTime` and `BusinessTimeBetween`.
- Calendar persistence uses a versioned codec.

## Verification

- Focused MSTest projects.

## Risks and boundaries

Transitions are detected by sampling the zone at most one day apart; zones with two transitions within one day are unsupported.

## Results

- `BusinessHours`, `WeeklyBusinessHours`, `CalendarDateOverride`, `BusinessCalendar` (canonical spans), `BusinessCalendarCodec`
  (versioned canonical JSON), `IBusinessCalendarStore`, `BusinessCalendarService` (`workspace:read` / `calendar:manage`).
- `BusinessCalendarCalculator`: `IsOpen`, `NextOpen`, `NextClose`, `AddBusinessTime`, `BusinessTimeBetween`, `GetOpenIntervals`
  with wall-clock DST semantics, UTC results and bounded horizon/instants.
- Tests (`BusinessCalendarCalculatorTests`, 16): New York spring-forward/fall-back (gap hour never open, repeated hour open twice,
  23/25-hour days), overnight shifts across DST nights, holidays and overrides, Lord Howe 30-minute DST, London, Samoa's skipped
  2011-12-30, a custom zone with midnight transitions, always/never-open calendars, bounds, randomized Add/Between inverse in four
  zones and interval/IsOpen agreement around transitions.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with E4's concurrent Contacts work in the tree).
- `dotnet test tests/Workspaces/TicketFlow.Workspaces.Tests -c Release` — Passed 42/42.
- `TICKETFLOW_REQUIRE_POSTGRES=1 TICKETFLOW_TEST_POSTGRES=<disposable postgres:18 on 127.0.0.1:55743> dotnet test tests/Workspaces/TicketFlow.Workspaces.Stores.Postgres.Tests -c Release` — Passed 11/11 (without PostgreSQL: 11 inconclusive).
- `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` — Passed 95/95; `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against the same container — Passed 15/15; `tests/TicketFlow.Tests` — Passed 6/6.
- `dotnet pack` of the three Workspaces projects — packages and symbols created with package validation.
- `bash scripts/verify-package-metadata.sh` currently stops at E4's in-progress `src/Contacts` projects (not yet in the solution); the Workspaces entries were added to `TicketFlow.slnx` and `scripts/nuget-projects.tsv` in dependency order.

Post-ticket parallel reviews have not been run yet.
