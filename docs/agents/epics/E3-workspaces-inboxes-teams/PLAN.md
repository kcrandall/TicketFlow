# E3 — Workspaces, inboxes and teams

Status: **Completed**

Administrable support structure that access grants and routing target.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Workspace settings, inbox definitions, teams and membership, business calendars.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Workspace settings, permissions and result contracts](tickets/T01-workspace-settings-and-permissions.md) | Completed | E2 |
| [T02 — Inboxes and inbox agent grants](tickets/T02-inboxes-and-agent-grants.md) | Completed | E3 T01 |
| [T03 — Teams, membership and principal resolution](tickets/T03-teams-and-membership.md) | Completed | E3 T01 |
| [T04 — Business calendars and business-time calculator](tickets/T04-business-calendars.md) | Completed | E3 T01 |
| [T05 — PostgreSQL workspace stores](tickets/T05-postgres-workspace-stores.md) | Completed | E3 T02, E3 T03, E3 T04 |

Packages: `TicketFlow.Workspaces.Abstractions`, `TicketFlow.Workspaces`, `TicketFlow.Workspaces.Stores.Postgres` under
`src/Workspaces`; tests under `tests/Workspaces`. Inboxes, teams and business calendars live in this feature family.

## Verification record

T01–T05: `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); Workspaces.Tests 42 passed; Workspaces.Stores.Postgres.Tests
11 passed against a disposable `postgres:18` with `TICKETFLOW_REQUIRE_POSTGRES=1`; Access.Tests 95 and Access.Stores.Postgres.Tests
15 passed after the additive permission change; the three Workspaces packages pack with validation. `verify-package-metadata.sh`
is blocked by E4's in-progress Contacts projects. Post-ticket reviews completed and all findings fixed with regression tests (see T03 Results): Workspaces.Tests 50, Workspaces.Stores.Postgres.Tests 13, Access.Tests 99, Access.Stores.Postgres.Tests 15 passed.
