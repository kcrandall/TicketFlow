# T01 — Workspace settings, permissions and result contracts

Epic: [E3 — Workspaces, inboxes and teams](../PLAN.md)

Status: **Completed**

## Goal

Create the `TicketFlow.Workspaces.Abstractions` and `TicketFlow.Workspaces` packages with revisioned workspace settings (name,
default locale, time zone, feature toggles), the `workspace:read|manage`, `inbox:read|manage`, `team:read|manage` and
`calendar:manage` permissions, union command/store results and `AddWorkspaces()` / `AddInMemoryWorkspaceStores()` registration.

## Dependencies

E2 (access control).

## Acceptance criteria

- Settings are bounded and validated (Utf16, locale and time-zone format, ≤ 64 feature toggles); revision-fenced create/update.
- Every read and write authorizes through `IAccessEvaluator`; missing permissions are `Forbidden` before any store access.
- Missing permissions are added to the Access built-in catalog (reusing existing `*:manage` entries) so built-in roles can hold them.
- Results are C# unions switched exhaustively; registrations use TryAdd.

## Verification

- Focused MSTest projects for touched packages; Release build with zero warnings.

## Risks and boundaries

Additive changes to `TicketFlow.Access.Abstractions` only (new permissions, structure-read permissions on built-in roles). In-memory
stores are development examples.

## Results

Implemented `src/Workspaces/TicketFlow.Workspaces.Abstractions` and `src/Workspaces/TicketFlow.Workspaces`:

- `WorkspaceSettings` (name ≤ 128, `WorkspaceLocales`-validated locale, `WorkspaceTimeZones` id syntax, ≤ 64 token-keyed toggles),
  `StoredWorkspaceSettings`, `IWorkspaceSettingsStore`, `IWorkspaceSettingsService` / `WorkspaceSettingsService`
  (`workspace:read` / `workspace:manage`; time zones must resolve on the host).
- Unions `WorkspaceResult<T>(WorkspaceSucceeded<T>, WorkspaceRefused)` and `WorkspaceWriteResult<T>(Applied, NotFound, Conflict,
  Rejected)`; `WorkspacePage<T>`, `WorkspacePageRequest`, `WorkspaceLimits`, `WorkspaceResources`, `WorkspacePermissions`.
- Access (additive): `AccessPermissions.WorkspaceRead`, `InboxRead`, `TeamRead`, `CalendarManage` added to the built-in catalog;
  every built-in role holds the three read permissions (Admin also gets `calendar:manage` as a built-in permission).
- `AddWorkspaces()` (scoped services, idempotent permission registration) and `AddInMemoryWorkspaceStores()`; tests in
  `ModelTests`, `ServiceTests.SettingsRequireReadAndManagePermissions`, `CompositionTests`, settings store contract.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with E4's concurrent Contacts work in the tree).
- `dotnet test tests/Workspaces/TicketFlow.Workspaces.Tests -c Release` — Passed 42/42.
- `TICKETFLOW_REQUIRE_POSTGRES=1 TICKETFLOW_TEST_POSTGRES=<disposable postgres:18 on 127.0.0.1:55743> dotnet test tests/Workspaces/TicketFlow.Workspaces.Stores.Postgres.Tests -c Release` — Passed 11/11 (without PostgreSQL: 11 inconclusive).
- `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` — Passed 95/95; `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against the same container — Passed 15/15; `tests/TicketFlow.Tests` — Passed 6/6.
- `dotnet pack` of the three Workspaces projects — packages and symbols created with package validation.
- `bash scripts/verify-package-metadata.sh` currently stops at E4's in-progress `src/Contacts` projects (not yet in the solution); the Workspaces entries were added to `TicketFlow.slnx` and `scripts/nuget-projects.tsv` in dependency order.

Post-ticket parallel reviews have not been run yet.
