# T05 — PostgreSQL workspace stores

Epic: [E3 — Workspaces, inboxes and teams](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Workspaces.Stores.Postgres`: settings, inbox, team (with member index) and calendar stores over an application-owned
`NpgsqlDataSource` with an append-only migration catalog, `tf_key_v1` keys and SQL-translated Access filters for lists.

## Dependencies

E3 T02, E3 T03, E3 T04.

## Acceptance criteria

Shared store contracts pass against PostgreSQL with two independent clients; per-user team limit holds under concurrency; list
filters match point checks; 256-unit identifiers work.

## Verification

- `TICKETFLOW_REQUIRE_POSTGRES=1` focused test run against a disposable `postgres:18` container.

## Risks and boundaries

Schema creation is explicit (`InitializeAsync`). Every statement is parameterized.

## Results

- `PostgresWorkspaceStore` implements all four contracts; `PostgresWorkspaceMigrations.Catalog` (`ticketflow.workspaces`, v1)
  creates `tf_workspace_settings`, `tf_workspace_inboxes`, `tf_workspace_teams`, `tf_workspace_team_members` (FK cascade, user
  index) and `tf_workspace_calendars` with `tf_key_v1` keys, raw-column comparison and `format_version`.
- Capacity and per-user team limits under workspace advisory locks; compare-and-set writes; lists translate the filter with
  `PostgresAccessFilterTranslator` over a derived table that exposes missing access columns as NULL (untranslatable → no rows).
- Tests (`PostgresWorkspaceStoreTests`, 11): all contracts with two independent data sources (including 256-unit identifiers and the
  concurrent membership race), SQL list parity over randomized grants (3 seeds), explicit/concurrent/checksum-verified
  initialization, corrupt rows fail closed, hostile values stay parameters, services and resolver over PostgreSQL.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with E4's concurrent Contacts work in the tree).
- `dotnet test tests/Workspaces/TicketFlow.Workspaces.Tests -c Release` — Passed 42/42.
- `TICKETFLOW_REQUIRE_POSTGRES=1 TICKETFLOW_TEST_POSTGRES=<disposable postgres:18 on 127.0.0.1:55743> dotnet test tests/Workspaces/TicketFlow.Workspaces.Stores.Postgres.Tests -c Release` — Passed 11/11 (without PostgreSQL: 11 inconclusive).
- `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` — Passed 95/95; `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against the same container — Passed 15/15; `tests/TicketFlow.Tests` — Passed 6/6.
- `dotnet pack` of the three Workspaces projects — packages and symbols created with package validation.
- `bash scripts/verify-package-metadata.sh` currently stops at E4's in-progress `src/Contacts` projects (not yet in the solution); the Workspaces entries were added to `TicketFlow.slnx` and `scripts/nuget-projects.tsv` in dependency order.

Post-ticket parallel reviews have not been run yet.
